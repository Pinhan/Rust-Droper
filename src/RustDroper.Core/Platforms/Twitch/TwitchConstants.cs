namespace RustDroper.Core.Platforms.Twitch;

/// <summary>Bir Twitch istemci kimliği + onunla tutarlı User-Agent/origin. Token'lar istemciye bağlıdır.</summary>
public sealed record TwitchClientProfile(string Name, string ClientId, string UserAgent, Uri Origin);

/// <summary>
/// Bilinen istemci profilleri (referans: TwitchDropsMiner sabitleri). Twitch bunları zaman zaman kapatır:
/// <b>2026-10-15 canlı sınama:</b> cihaz-kodu uç noktası <c>android_app</c> ve <c>web</c> için "invalid client" (400),
/// <c>smartbox</c> ve <c>mobileweb</c> için 200 döndürdü. Bu yüzden profil yapılandırılabilir ve sınanabilir tutulur.
/// </summary>
public static class TwitchClients
{
    public static readonly TwitchClientProfile SmartboxTv = new(
        "smartbox",
        "ue6666qo983tsx6so1t0vnawi233wa",
        "Mozilla/5.0 (Linux; Android 7.1; Smart Box C1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36",
        new Uri("https://android.tv.twitch.tv"));

    public static readonly TwitchClientProfile MobileWeb = new(
        "mobileweb",
        "r8s4dac0uhzifbpu9sjdiwzctle17ff",
        "Mozilla/5.0 (Linux; Android 16) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.7204.158 Mobile Safari/537.36",
        new Uri("https://m.twitch.tv"));

    public static IReadOnlyList<TwitchClientProfile> All { get; } = [SmartboxTv, MobileWeb];

    public static TwitchClientProfile? ByName(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class TwitchConstants
{
    public static readonly Uri DeviceCodeUri = new("https://id.twitch.tv/oauth2/device");

    public static readonly Uri TokenUri = new("https://id.twitch.tv/oauth2/token");

    public static readonly Uri ValidateUri = new("https://id.twitch.tv/oauth2/validate");

    public static readonly Uri GqlUri = new("https://gql.twitch.tv/gql");

    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    public static HttpClient CreateHttpClient(TwitchClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };

        // Bazı UA dizeleri katı UserAgent ayrıştırıcısından geçmez → doğrulamasız ekle.
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", profile.UserAgent);
        return client;
    }
}
