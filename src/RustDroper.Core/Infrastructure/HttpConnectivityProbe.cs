using System.Net;
using RustDroper.Core.Orchestration;

namespace RustDroper.Core.Infrastructure;

/// <summary>
/// Bağlantı yoklaması: birkaç uç noktaya aynı anda hafif bir GET atar; herhangi biri beklenen yanıtı verirse "internet var".
/// Varsayılanlar: Windows'un kendi ağ denetimi (<c>msftconnecttest.com</c>, 200 beklenir; captive portal yönlendirmesi sayılmaz) ve
/// Twitch kimlik sunucusu (<c>id.twitch.tv</c>, belirteçsiz 401 döner → yanıt gelmesi yeter). İkisi birden engelliyse çevrimdışı sayılır.
/// </summary>
public sealed class HttpConnectivityProbe : IConnectivityProbe
{
    public sealed record Endpoint(Uri Uri, Func<HttpStatusCode, bool> Accepts);

    public static IReadOnlyList<Endpoint> DefaultEndpoints { get; } =
    [
        new(new Uri("http://www.msftconnecttest.com/connecttest.txt"), status => status == HttpStatusCode.OK),
        new(new Uri("https://id.twitch.tv/oauth2/validate"), status => (int)status < 500),
    ];

    private readonly HttpClient _http;
    private readonly IReadOnlyList<Endpoint> _endpoints;

    public HttpConnectivityProbe(HttpClient http, IReadOnlyList<Endpoint>? endpoints = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _endpoints = endpoints is { Count: > 0 } ? endpoints : DefaultEndpoints;
    }

    /// <summary>Kısa zaman aşımlı, yönlendirme izlemeyen, bağlantı havuzu kısa ömürlü istemci (ölü bağlantılar yoklamayı yanıltmasın).</summary>
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromSeconds(20),
        })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = _endpoints.Select(endpoint => CheckAsync(endpoint, cts.Token)).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                if (await done.ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false); // kalan yoklamaları bırak
        }
    }

    private async Task<bool> CheckAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return endpoint.Accepts(response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                   && !cancellationToken.IsCancellationRequested)
        {
            return false; // ulaşılamıyor (DNS/bağlantı/zaman aşımı); çağıranın iptali ise yayılır
        }
    }
}
