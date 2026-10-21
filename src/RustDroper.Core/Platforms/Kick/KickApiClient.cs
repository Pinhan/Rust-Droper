using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// Kick drop istemcisi: kimlikli <c>web.kick.com/api/v1/drops/*</c> (ilerleme, özet, claim) + kamusal canlı yayın/kanal uçları.
/// Hız sınırı (varsayılan 2 istek/sn), GET'lerde geçici hata için yeniden deneme; <b>claim yeniden denenmez</b> (çift claim riski:
/// çağıran karar verir). Hata sözleşmesi: <see cref="PlatformAuthException"/> / <see cref="PlatformDataException"/> /
/// <see cref="PlatformHttpException"/>. Token <paramref name="accessToken"/> ile istek başına okunur; asla loglanmaz.
/// </summary>
public sealed class KickApiClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _accessToken;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public KickApiClient(
        HttpClient http,
        Func<string?> accessToken,
        SlidingWindowRateLimiter? limiter = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(accessToken);
        _http = http;
        _accessToken = accessToken;
        _time = timeProvider ?? TimeProvider.System;
        _limiter = limiter ?? new SlidingWindowRateLimiter(2, TimeSpan.FromSeconds(1), _time);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Hesabın tüm kampanya ilerlemesi (<c>GET /drops/progress</c>).</summary>
    public async Task<ParseResult<KickCampaignProgress>> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        var body = await GetAsync(KickConstants.ProgressUri, authenticated: true, userAgent: null, cancellationToken).ConfigureAwait(false);
        return KickProgressParser.ParseProgress(body);
    }

    /// <summary>Tek kampanyanın ilerleme özeti (<c>progress_units</c> = izlenen dakika). Henüz ilerleme yoksa <c>null</c>.</summary>
    public async Task<KickCampaignProgress?> GetSummaryAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignId);
        try
        {
            var body = await GetAsync(KickConstants.SummaryUri(campaignId), authenticated: true, userAgent: null, cancellationToken).ConfigureAwait(false);
            return KickProgressParser.ParseSummary(body, campaignId);
        }
        catch (PlatformHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Bu kampanyada henüz ilerleme kaydı yok (olası cevap): "ilerleme yok" demektir, hata değil.
            return null;
        }
    }

    /// <summary>
    /// Ödülü claim eder (<c>POST /drops/claim {reward_id, campaign_id}</c>). 2xx → başarı; 400/404/409/422 → <see cref="KickClaimResult.Rejected"/>;
    /// kimlik/ağ/sunucu hataları istisna olarak yükselir. Tek deneme (yeniden deneme çağıranın kararı).
    /// </summary>
    public async Task<KickClaimResult> ClaimAsync(string rewardId, string campaignId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rewardId);
        ArgumentException.ThrowIfNullOrEmpty(campaignId);
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        using var request = KickHttp.Request(HttpMethod.Post, KickConstants.ClaimUri, RequireToken());
        request.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["reward_id"] = rewardId, ["campaign_id"] = campaignId }),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new KickClaimResult(true, false, ExtractMessage(body));
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            return new KickClaimResult(false, true, ExtractMessage(body) ?? $"HTTP {(int)response.StatusCode}");
        }

        throw KickHttp.Classify(response, KickConstants.ClaimUri, body, authenticated: true);
    }

    /// <summary>Rust kategorisinde izleyiciye göre sıralı canlı yayınlar (kamusal; mobil UA).</summary>
    public async Task<ParseResult<LiveChannel>> GetLiveStreamsAsync(int categoryId = KickConstants.RustCategoryId, int limit = 24, CancellationToken cancellationToken = default)
    {
        var body = await GetAsync(KickConstants.LivestreamsUri(categoryId, limit), authenticated: false, KickConstants.MobileUserAgent, cancellationToken).ConfigureAwait(false);
        return KickLiveParser.ParseLivestreams(body);
    }

    /// <summary>Kampanyanın drop'a uygun canlı yayınları (genel kampanyalar için; kamusal). Yayıncı kampanyasında boş dönebilir.</summary>
    public async Task<ParseResult<LiveChannel>> GetCampaignLiveStreamsAsync(string campaignId, string languageCode = "en", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignId);
        var body = await GetAsync(KickConstants.CampaignLivestreamsUri(campaignId, languageCode), authenticated: false, userAgent: null, cancellationToken).ConfigureAwait(false);
        return KickLiveParser.ParseLivestreams(body);
    }

    /// <summary>Kanal bilgisi + oynatıcı URL'si (kamusal). Kanal yoksa (404) <c>null</c>.</summary>
    public async Task<KickChannelInfo?> GetChannelAsync(string slug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        try
        {
            var body = await GetAsync(KickConstants.ChannelUri(slug), authenticated: false, userAgent: null, cancellationToken).ConfigureAwait(false);
            return KickLiveParser.ParseChannel(body);
        }
        catch (PlatformHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private string RequireToken() =>
        _accessToken() is { Length: > 0 } token ? token : throw new PlatformAuthException(Loc.T("err.kick.noToken"));

    private Task<string> GetAsync(Uri uri, bool authenticated, string? userAgent, CancellationToken cancellationToken) =>
        Retry.ExecuteAsync(
            async ct =>
            {
                await _limiter.WaitAsync(ct).ConfigureAwait(false);
                using var request = KickHttp.Request(HttpMethod.Get, uri, authenticated ? RequireToken() : null, userAgent);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                return await KickHttp.ReadAsync(response, uri, authenticated, ct).ConfigureAwait(false);
            },
            new RetryOptions { MaxAttempts = 3 },
            _time,
            onRetry: (attempt, delay, ex) =>
                _logger.LogWarning("Kick {Host} geçici hata (deneme {Attempt}), {Delay:0.0}s sonra tekrar: {Reason}", uri.Host, attempt, delay.TotalSeconds, ex.Message),
            cancellationToken: cancellationToken);

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetStringOrNull("message");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
