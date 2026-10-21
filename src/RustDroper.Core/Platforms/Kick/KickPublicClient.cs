using System.Net;
using System.Net.Http.Headers;
using RustDroper.Core.Domain;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>Kick'in kimlik doğrulaması gerektirmeyen herkese açık drop kampanya uçları.</summary>
public sealed class KickPublicClient
{
    public static readonly Uri CampaignsUri = new("https://web.kick.com/api/v1/drops/campaigns");

    private const string BrowserUserAgent = KickConstants.BrowserUserAgent;

    private readonly HttpClient _http;

    public KickPublicClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>Varsayılan ayarlı bir <see cref="HttpClient"/> (zaman aşımı, sıkıştırma, tarayıcı UA).</summary>
    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<ParseResult<Campaign>> GetCampaignsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .GetAsync(CampaignsUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformHttpException(response.StatusCode, CampaignsUri);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return KickCampaignParser.Parse(json);
    }
}
