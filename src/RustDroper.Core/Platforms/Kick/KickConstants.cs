using System.Net;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// Kick uç noktaları ve istemci kimliği. <b>U5 (2026-10-16, gerçek sınama):</b> .NET <c>HttpClient</c> (HTTP/1.1–2, tarayıcı UA)
/// ile kamusal kampanya listesi, canlı yayın listesi, kanal bilgisi (<c>playback_url</c> dahil) ve TV-login yoklaması Cloudflare'a
/// takılmadan çalışıyor; HTTP/3 şart değil (bu makinede msquic yok, gerekmedi).
/// </summary>
public static class KickConstants
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>Mobil API'ler (<c>mobile.kick.com</c>) referans projede bu UA ile kullanılıyor.</summary>
    public const string MobileUserAgent = "okhttp/4.12.0";

    /// <summary>Rust'ın Kick kategori kimliği (canlı yayın listesi için).</summary>
    public const int RustCategoryId = 13;

    public static readonly Uri SiteBase = new("https://kick.com/");

    public static readonly Uri WebApiBase = new("https://web.kick.com/");

    public static readonly Uri MobileApiBase = new("https://mobile.kick.com/");

    /// <summary>TV-login sayfası: kullanıcı tarayıcıda kendi Kick hesabıyla açıp onaylar.</summary>
    public static readonly Uri TvLoginPage = new("https://kick.com/tv/login");

    public static Uri TvLinkPollUri(string uuid) => new(SiteBase, "api/tv/link/authenticate/" + Uri.EscapeDataString(uuid));

    public static readonly Uri ProgressUri = new(WebApiBase, "api/v1/drops/progress");

    public static readonly Uri ClaimUri = new(WebApiBase, "api/v1/drops/claim");

    public static readonly Uri UserUri = new(SiteBase, "api/v1/user");

    public static Uri SummaryUri(string campaignId) =>
        new(WebApiBase, "api/v1/drops/progress/summary?campaign_id=" + Uri.EscapeDataString(campaignId));

    public static Uri LivestreamsUri(int categoryId, int limit) =>
        new(MobileApiBase, $"api/v1/livestreams?limit={limit}&category_id={categoryId}&sort=viewer_count_desc");

    /// <summary>Kampanyanın drop'a uygun canlı yayınları (kamusal). <c>lang_code</c> zorunlu: yoksa 400 (gerçek sınama).</summary>
    public static Uri CampaignLivestreamsUri(string campaignId, string languageCode) =>
        new(WebApiBase, $"api/v1/drops/campaigns/{Uri.EscapeDataString(campaignId)}/livestreams?lang_code={Uri.EscapeDataString(languageCode)}");

    public static Uri ChannelUri(string slug) => new(SiteBase, "api/v2/channels/" + Uri.EscapeDataString(slug));

    /// <summary>Zaman aşımı, sıkıştırma ve tarayıcı UA'lı ortak <see cref="HttpClient"/> (kimlik başlığı istek başına eklenir).</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }
}
