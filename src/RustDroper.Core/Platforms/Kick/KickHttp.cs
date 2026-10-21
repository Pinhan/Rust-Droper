using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>Kick HTTP cevaplarının ortak sınıflandırması (kimlik / engel / geçici) ve istek kurulumu.</summary>
internal static class KickHttp
{
    public static HttpRequestMessage Request(HttpMethod method, Uri uri, string? bearer = null, string? userAgent = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(bearer))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        }

        if (userAgent is not null)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return request;
    }

    /// <summary>
    /// Başarılıysa gövdeyi döndürür. Başarısızsa: 401 ve <b>Kick'in kendi JSON'uyla gelen 403</b> (gerçek sınama, 2026-10-16:
    /// geçersiz/eksik token → <c>403 {"data":{"type":"Forbidden"}}</c>) kimlik hatasıdır; JSON olmayan 403 (Cloudflare engeli)
    /// ve diğerleri <see cref="PlatformHttpException"/>.
    /// </summary>
    public static async Task<string> ReadAsync(HttpResponseMessage response, Uri uri, bool authenticated, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        throw Classify(response, uri, body, authenticated);
    }

    public static Exception Classify(HttpResponseMessage response, Uri uri, string body, bool authenticated)
    {
        if (authenticated
            && (response.StatusCode == HttpStatusCode.Unauthorized
                || (response.StatusCode == HttpStatusCode.Forbidden && IsKickJson(body))))
        {
            return new PlatformAuthException(Loc.T("err.kick.badAuth", uri.Host, (int)response.StatusCode));
        }

        return new PlatformHttpException(response.StatusCode, uri, ReadRetryAfter(response.Headers));
    }

    public static bool IsKickJson(string body)
    {
        var trimmed = body.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static JsonDocument ParseJson(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new PlatformDataException(Loc.T("err.kick.badJson", what), ex);
        }
    }

    /// <summary>Kick'in <c>{"data": ...}</c> zarfını açar; zarf yoksa kökü döndürür.</summary>
    public static JsonElement Unwrap(JsonElement root) =>
        root.TryGetProp("data", out var data) ? data : root;

    private static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers) => headers.RetryAfter?.Delta;
}
