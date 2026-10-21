using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;

namespace RustDroper.Core.Media;

/// <summary>Bir drop kartı için medya bilgisi (Facepunch kataloğu): görsel yolu önbellekteyse dolu.</summary>
public sealed record DropMedia(
    string Name,
    int? Minutes,
    string? DurationText,
    Uri? ImageUrl,
    Uri? VideoUrl,
    string? ImagePath,
    bool IsStreamerDrop,
    string? StreamerName,
    Uri? StreamerUrl);

/// <summary>
/// Arayüzün medya kaynağı. Arayüz ham veriden bağımsızdır: ağ yoksa/başarısızsa kartlar yer tutucuyla yine çizilir.
/// </summary>
public interface IDropMediaProvider
{
    /// <summary>Platformun resmî drop sayfasından çekilen drop kataloğu (yoksa/henüz çekilmediyse boş).</summary>
    IReadOnlyList<DropMedia> Catalog(Platform platform);

    /// <summary>Adres önbellekteyse yerel yol (ağ yok); değilse <c>null</c>.</summary>
    string? CachedPath(Uri url);

    /// <summary>Önbellekten verir, yoksa indirir (izin verilen alan adlarıyla sınırlı); olmazsa <c>null</c>.</summary>
    Task<string?> EnsureAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>Platformun resmî sayfasındaki şu anki etkinlik (aktif &gt; yaklaşan &gt; son biten); sayfa/yapı yoksa <c>null</c>.</summary>
    FacepunchEvent? CurrentEvent(Platform platform, DateTimeOffset now);

    /// <summary>Katalog ya da görseller güncellenince tetiklenir (herhangi bir iş parçacığında).</summary>
    event Action? Changed;
}

/// <summary>
/// Facepunch drop sayfasını (Twitch) açılışta ve 6 saatte bir çeker, ayrıştırır, görselleri diske indirir. Kampanya yokken de
/// kartların görünmesini sağlar. Tüm hatalar yutulur (günlüğe yazılır); eski katalog korunur.
/// </summary>
public sealed class FacepunchMediaService : IDropMediaProvider, IDisposable
{
    public static readonly Uri TwitchPage = new("https://twitch.facepunch.com/");
    public static readonly Uri KickPage = new("https://kick.facepunch.com/");
    /// <summary>İzinli medya alan adları (yalnız HTTPS): Facepunch drop görselleri, Kick ödül görselleri, Twitch ödül görselleri (envanter <c>imageURL</c>, claim kartları).</summary>
    public static readonly string[] AllowedMediaHosts = ["files.facepunch.com", "ext.cdn.kick.com", "files.kick.com", "static-cdn.jtvnw.net"];

    private const int MaxPageBytes = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly DropMediaCache _cache;
    private readonly Uri _page;
    private readonly Uri? _kickPage;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _retry;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private volatile IReadOnlyList<FacepunchDrop> _drops = [];
    private volatile IReadOnlyList<FacepunchDrop> _kickDrops = [];
    private volatile IReadOnlyList<FacepunchEvent> _events = [];
    private volatile IReadOnlyList<FacepunchEvent> _kickEvents = [];
    private int _started;

    public FacepunchMediaService(
        HttpClient http,
        DropMediaCache cache,
        Uri? page = null,
        TimeSpan? interval = null,
        TimeSpan? retry = null,
        TimeProvider? time = null,
        ILogger? logger = null,
        Uri? kickPage = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);
        _http = http;
        _cache = cache;
        _page = page ?? TwitchPage;
        _kickPage = kickPage;
        _interval = interval ?? TimeSpan.FromHours(6);
        _retry = retry ?? TimeSpan.FromMinutes(15);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public event Action? Changed;

    /// <summary>Son başarılı çekimin ham drop listesi.</summary>
    public IReadOnlyList<FacepunchDrop> Drops => _drops;

    public FacepunchEvent? CurrentEvent(Platform platform, DateTimeOffset now) =>
        FacepunchEvent.Pick(platform == Platform.Kick ? _kickEvents : _events, now);

    public IReadOnlyList<DropMedia> Catalog(Platform platform) => platform is Platform.Twitch or Platform.Kick
        ? [.. (platform == Platform.Kick ? _kickDrops : _drops).Select(d => new DropMedia(
            d.Name,
            d.Minutes,
            d.DurationText,
            d.ImageUrl,
            d.VideoUrl,
            d.ImageUrl is null ? null : _cache.TryGet(d.ImageUrl),
            d.IsStreamerDrop,
            d.StreamerName,
            d.StreamerUrl))]
        : [];

    public string? CachedPath(Uri url) => _cache.TryGet(url);

    public Task<string?> EnsureAsync(Uri url, CancellationToken cancellationToken) => _cache.EnsureAsync(url, cancellationToken);

    /// <summary>Açılışta bir kez çeker, sonra her aralıkta (hata olursa kısa aralıkla) yeniler. İkinci çağrı etkisizdir.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    /// <summary>
    /// Sayfaları çeker + ayrıştırır + görselleri indirir (Twitch; varsa Kick). Başarısızlıkta eski katalog/etkinlik kalır.
    /// <c>true</c> = Twitch sayfasından en az bir drop ya da etkinlik okundu.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var twitchOk = await RefreshPageAsync(_page, Platform.Twitch, cancellationToken).ConfigureAwait(false);
        if (_kickPage is not null)
        {
            await RefreshPageAsync(_kickPage, Platform.Kick, cancellationToken).ConfigureAwait(false);
        }

        return twitchOk;
    }

    private async Task<bool> RefreshPageAsync(Uri page, Platform platform, CancellationToken cancellationToken)
    {
        try
        {
            var html = await FetchPageAsync(page, cancellationToken).ConfigureAwait(false);
            var drops = FacepunchPageParser.Parse(html, page);
            var events = FacepunchPageParser.ParseEvents(html);
            if (drops.Count == 0 && events.Count == 0)
            {
                _logger.LogInformation("{Host} sayfasında drop/etkinlik bulunamadı (sayfa yapısı değişmiş olabilir); eski veri korunuyor.", page.Host);
                return false;
            }

            if (events.Count > 0)
            {
                SetEvents(platform, events);
            }

            if (drops.Count == 0)
            {
                RaiseChanged();
                return true;
            }

            SetDrops(platform, drops);
            RaiseChanged(); // kartlar önce yer tutucuyla görünür
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(drops.Where(d => d.ImageUrl is not null).Select(async d =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _cache.EnsureAsync(d.ImageUrl!, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            RaiseChanged(); // görseller geldi
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            if (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("{Host} drop sayfası çekilemedi: {Message}", page.Host, ex.Message);
            }

            return false;
        }
    }

    private void SetDrops(Platform platform, IReadOnlyList<FacepunchDrop> drops)
    {
        if (platform == Platform.Kick)
        {
            _kickDrops = drops;
        }
        else
        {
            _drops = drops;
        }
    }

    private void SetEvents(Platform platform, IReadOnlyList<FacepunchEvent> events)
    {
        if (platform == Platform.Kick)
        {
            _kickEvents = events;
        }
        else
        {
            _events = events;
        }
    }

    public void Dispose() => _cts.Cancel();

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var ok = await RefreshAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(ok ? _interval : _retry, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string> FetchPageAsync(Uri page, CancellationToken cancellationToken)
    {
        if (page.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Yalnız HTTPS sayfa adresi desteklenir.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && !string.Equals(final.Host, page.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sayfa başka bir alan adına yönlendirildi.");
        }

        if (response.Content.Headers.ContentLength > MaxPageBytes)
        {
            throw new InvalidOperationException("Sayfa beklenenden büyük.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxPageBytes)
            {
                throw new InvalidOperationException("Sayfa beklenenden büyük.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Medya değişti bildirimi işlenemedi");
        }
    }
}
