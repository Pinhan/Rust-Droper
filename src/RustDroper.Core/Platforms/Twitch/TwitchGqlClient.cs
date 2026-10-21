using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Twitch;

/// <summary>GQL cevabı: <see cref="Data"/> yalnızca <see cref="Dispose"/>'a kadar geçerlidir.</summary>
public sealed class GqlResult(JsonDocument document, JsonElement data, IReadOnlyList<string> partialErrors) : IDisposable
{
    public JsonElement Data { get; } = data;

    /// <summary>Veri döndü ama bazı alanlar hatayla boşaldı (ör. "service error (path: currentUser.dropCampaigns)").</summary>
    public IReadOnlyList<string> PartialErrors { get; } = partialErrors;

    public void Dispose() => document.Dispose();
}

/// <summary>Cihaz kimliği (X-Device-Id): bir kez üretilir, sır deposunda kalıcıdır.</summary>
public static class TwitchDeviceId
{
    private const string Key = "twitch.deviceId";

    public static string GetOrCreate(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (secrets.Get(Key) is { Length: >= 16 } existing)
        {
            return existing;
        }

        var created = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        secrets.Set(Key, created);
        return created;
    }
}

/// <summary>
/// Twitch GQL (persisted query) istemcisi: hız sınırı (varsayılan 5/sn), geçici hatada yeniden deneme,
/// <c>errors</c> dizisinin sınıflandırılması (hash kayması / integrity / yetki / geçici).
/// </summary>
public sealed class TwitchGqlClient
{
    private static readonly JsonSerializerOptions BodyOptions = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly TwitchClientProfile _profile;
    private readonly Func<string?> _accessToken;
    private readonly GqlHashRegistry _hashes;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly string _deviceId;
    private readonly string _sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public TwitchGqlClient(
        HttpClient http,
        TwitchClientProfile profile,
        Func<string?> accessToken,
        GqlHashRegistry hashes,
        string deviceId,
        SlidingWindowRateLimiter? limiter = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(accessToken);
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _http = http;
        _profile = profile;
        _accessToken = accessToken;
        _hashes = hashes;
        _deviceId = deviceId;
        _time = timeProvider ?? TimeProvider.System;

        // Referans: "GQL hız sınırı aşılırsa her şey bozuluyor" → 5 istek/sn.
        _limiter = limiter ?? new SlidingWindowRateLimiter(5, TimeSpan.FromSeconds(1), _time);
        _logger = logger ?? NullLogger.Instance;
    }

    public Task<GqlResult> ExecuteAsync(GqlOperation operation, IReadOnlyDictionary<string, object?>? variables = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var body = BuildBody(operation, variables);

        return Retry.ExecuteAsync(
            ct => SendOnceAsync(operation, body, ct),
            new RetryOptions { MaxAttempts = 4 },
            _time,
            onRetry: (attempt, delay, ex) =>
                _logger.LogWarning("GQL {Operation} geçici hata (deneme {Attempt}), {Delay:0.0}s sonra tekrar: {Reason}", operation.Name, attempt, delay.TotalSeconds, ex.Message),
            cancellationToken: cancellationToken);
    }

    private string BuildBody(GqlOperation operation, IReadOnlyDictionary<string, object?>? variables)
    {
        var payload = new Dictionary<string, object?>
        {
            ["operationName"] = operation.Name,
            ["variables"] = variables ?? new Dictionary<string, object?>(),
            ["extensions"] = new Dictionary<string, object?>
            {
                ["persistedQuery"] = new Dictionary<string, object?>
                {
                    ["version"] = 1,
                    ["sha256Hash"] = _hashes.GetHash(operation),
                },
            },
        };
        return JsonSerializer.Serialize(payload, BodyOptions);
    }

    private async Task<GqlResult> SendOnceAsync(GqlOperation operation, string body, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, TwitchConstants.GqlUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Client-Id", _profile.ClientId);
        request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
        request.Headers.TryAddWithoutValidation("Client-Session-Id", _sessionId);
        if (_accessToken() is { Length: > 0 } token)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new TwitchUnauthorizedException($"'{operation.Name}': HTTP 401");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformHttpException(response.StatusCode, TwitchConstants.GqlUri, ReadRetryAfter(response.Headers));
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new PlatformDataException(Loc.T("err.twitch.gqlBadJson", operation.Name), ex);
        }

        try
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            var (data, partial) = GqlResponse.Evaluate(root, operation.Name);
            if (partial.Count > 0)
            {
                _logger.LogWarning("GQL {Operation} kısmi hata: {Errors}", operation.Name, string.Join(" | ", partial));
            }

            return new GqlResult(document, data, partial);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers) =>
        headers.RetryAfter?.Delta;
}
