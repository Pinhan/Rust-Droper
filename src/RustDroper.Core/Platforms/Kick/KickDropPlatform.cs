using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// Motorun Kick bağdaştırıcısı: kamusal kampanya listesi + canlı kanal keşfi (yayıncı kampanyası → kampanyanın kanalları;
/// genel kampanya → kampanyanın drop'a uygun yayınları, olmazsa Rust kategorisinin en kalabalıkları), HLS izleyici, kimlikli
/// <c>/drops/progress/summary</c> yoklaması (sunucu-gerçeği) ve (varsa) otomatik claim. <paramref name="autoClaimer"/> verilmezse
/// hiçbir şey claim edilmez. Kick'te aynı anda tek kanal için ilerleme olur: motor zaten tek kanal izler.
/// <para>İzleme: <paramref name="playerHost"/> verilirse Kick'in gerçek web oynatıcısı (<see cref="KickWebViewWatcher"/>, Tier B — sunucu
/// ilerlemesini bu verir); verilmezse yalnız HLS (<see cref="KickHlsWatcher"/>, Tier A) — U3 ölçümüne göre Kick'te krediye yetmez, yalnızca
/// tanılama/CLI içindir.</para>
/// </summary>
public sealed class KickDropPlatform : IDropPlatform
{
    private const string GeneralCampaignLanguage = "en";

    private readonly KickPublicClient _public;
    private readonly KickApiClient _api;
    private readonly HttpClient _hlsHttp;
    private readonly KickAutoClaimer? _autoClaimer;
    private readonly IKickPlayerHost? _playerHost;
    private readonly KickWebViewOptions? _webViewOptions;
    private readonly HlsQuality _quality;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private volatile PlatformSnapshot? _last;

    public KickDropPlatform(
        KickPublicClient publicClient,
        KickApiClient api,
        HttpClient hlsHttp,
        KickAutoClaimer? autoClaimer = null,
        HlsQuality quality = HlsQuality.LowestVideo,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        IKickPlayerHost? playerHost = null,
        KickWebViewOptions? webViewOptions = null)
    {
        ArgumentNullException.ThrowIfNull(publicClient);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(hlsHttp);
        _public = publicClient;
        _api = api;
        _hlsHttp = hlsHttp;
        _autoClaimer = autoClaimer;
        _playerHost = playerHost;
        _webViewOptions = webViewOptions;
        _quality = quality;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        if (autoClaimer is not null)
        {
            autoClaimer.Completed += record =>
                DropClaimed?.Invoke(new ClaimNotice(record.DropId, record.Outcome == "Claimed", record.Status));
        }
    }

    public Platform Platform => Platform.Kick;

    /// <summary>Kick'te push bildirimi kullanılmıyor: yayın kapanması izleyicinin <c>Offline</c> sonucuyla anlaşılır.</summary>
    public event Action<string>? ChannelWentOffline
    {
        add { }
        remove { }
    }

    public event Action<ClaimNotice>? DropClaimed;

    /// <summary>Son keşifte görülen, verilen oyuna ait kampanya kimlikleri (otomatik claim yalnızca bunlarda çalışır).</summary>
    public IReadOnlySet<string> KnownCampaignIds =>
        _last?.Campaigns.Select(c => c.Id).ToHashSet(StringComparer.Ordinal) ?? [];

    public async Task<PlatformSnapshot> DiscoverAsync(string gameSlug, CancellationToken cancellationToken)
    {
        var all = await _public.GetCampaignsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var warning in all.Warnings)
        {
            _logger.LogWarning("Kick kampanya ayrıştırma uyarısı: {Warning}", warning);
        }

        var now = _time.GetUtcNow();
        var campaigns = all.Items
            .ForGame(gameSlug)
            .Where(c => c.Status != CampaignStatus.Expired)
            .ToList();

        var channels = new Dictionary<string, IReadOnlyList<LiveChannel>>(StringComparer.Ordinal);
        IReadOnlyList<LiveChannel>? categoryStreams = null;
        foreach (var campaign in campaigns.Where(c => c.IsRunningAt(now)))
        {
            var live = campaign.Scope == CampaignScope.Streamer
                ? await FindStreamerChannelsAsync(campaign, cancellationToken).ConfigureAwait(false)
                : await FindGeneralChannelsAsync(campaign, categoryStreams, cancellationToken).ConfigureAwait(false);
            if (campaign.Scope == CampaignScope.General && live.Fallback is not null)
            {
                categoryStreams = live.Fallback;
            }

            channels[campaign.Id] = live.Channels;
        }

        var snapshot = new PlatformSnapshot(campaigns, channels);
        _last = snapshot;
        return snapshot;
    }

    public IWatcher CreateWatcher() => _playerHost is { } host
        ? new KickWebViewWatcher(host, _webViewOptions, _time, _logger)
        : new KickHlsWatcher(_api, _hlsHttp, _quality, _time, _logger);

    public async Task<DropSession?> GetSessionAsync(LiveChannel channel, CancellationToken cancellationToken)
    {
        if (_last is not { } snapshot)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var candidates = snapshot.Campaigns
            .Where(c => c.IsRunningAt(now)
                && snapshot.ChannelsByCampaign.TryGetValue(c.Id, out var channels)
                && channels.Any(ch => string.Equals(ch.Id, channel.Id, StringComparison.Ordinal)))
            .OrderBy(c => c.Scope == CampaignScope.Streamer ? 0 : 1)
            .ThenBy(c => c.EndsAt ?? DateTimeOffset.MaxValue);

        foreach (var campaign in candidates)
        {
            var summary = await _api.GetSummaryAsync(campaign.Id, cancellationToken).ConfigureAwait(false);
            if (summary is null)
            {
                continue;
            }

            if (SessionFor(campaign, summary) is { } session)
            {
                return session;
            }
        }

        return null;
    }

    public void OnTargetChanged(LiveChannel? channel)
    {
        // Kick'te kanala bağlı bir dinleyici (PubSub benzeri) yok.
    }

    public async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        if (_autoClaimer is null)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _autoClaimer.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sunucu ilerlemesinden (<c>progress_units</c> = izlenen dakika, kümülatif) o an ilerleyen ödülü bulur: eşiği henüz geçilmemiş en küçük ödül.
    /// Tüm eşikler geçildiyse: claim edilmemiş ödül varsa ve otomatik claim açıksa "tamamlandı, claim bekleniyor" (tamamlanmış oturum),
    /// aksi halde <c>null</c> (kampanya tamam; elle claim, arayüzde/bildirimde görünür).
    /// </summary>
    internal DropSession? SessionFor(Campaign campaign, KickCampaignProgress summary)
    {
        var rewards = campaign.Rewards.OrderBy(r => r.RequiredMinutes).ToList();
        if (rewards.Count == 0)
        {
            return null;
        }

        var claimed = summary.Rewards.Where(r => r.Claimed).Select(r => r.RewardId).ToHashSet(StringComparer.Ordinal);
        var units = summary.ProgressUnits ?? InferUnits(rewards, summary);
        var minutes = (int)Math.Floor(units);

        if (rewards.FirstOrDefault(r => units < r.RequiredMinutes) is { } next)
        {
            return new DropSession(next.Id, minutes, next.RequiredMinutes);
        }

        var last = rewards[^1];
        var unclaimed = rewards.Any(r => !claimed.Contains(r.Id));
        return _autoClaimer is not null && unclaimed
            ? new DropSession(last.Id, minutes, last.RequiredMinutes)
            : null;
    }

    private static double InferUnits(IReadOnlyList<DropReward> rewards, KickCampaignProgress summary) =>
        summary.Rewards
            .Select(p => (Progress: p.Progress, Reward: rewards.FirstOrDefault(r => r.Id == p.RewardId)))
            .Where(x => x.Reward is not null)
            .Select(x => x.Progress * x.Reward!.RequiredMinutes)
            .DefaultIfEmpty(0)
            .Max();

    private async Task<(IReadOnlyList<LiveChannel> Channels, IReadOnlyList<LiveChannel>? Fallback)> FindGeneralChannelsAsync(
        Campaign campaign,
        IReadOnlyList<LiveChannel>? cachedCategory,
        CancellationToken ct)
    {
        try
        {
            var eligible = await _api.GetCampaignLiveStreamsAsync(campaign.Id, GeneralCampaignLanguage, ct).ConfigureAwait(false);
            if (eligible.Items.Count > 0)
            {
                return ([.. eligible.Items.OrderByDescending(c => c.Viewers)], null);
            }
        }
        catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException)
        {
            _logger.LogWarning("Kick '{Campaign}' kampanya yayınları alınamadı: {Reason}", campaign.Name, ex.Message);
        }

        // Yedek: Rust kategorisinin en kalabalık yayınları (drop'a uygunluğu garanti değil; ilerleme yoksa bekçi kanalı değiştirir).
        if (cachedCategory is not null)
        {
            return (cachedCategory, cachedCategory);
        }

        var category = await _api.GetLiveStreamsAsync(cancellationToken: ct).ConfigureAwait(false);
        return (category.Items, category.Items);
    }

    private async Task<(IReadOnlyList<LiveChannel> Channels, IReadOnlyList<LiveChannel>? Fallback)> FindStreamerChannelsAsync(
        Campaign campaign,
        CancellationToken ct)
    {
        var live = new List<LiveChannel>();
        foreach (var streamer in campaign.Streamers)
        {
            try
            {
                var info = await _api.GetChannelAsync(streamer.Login, ct).ConfigureAwait(false);
                if (info is null || !info.IsLive)
                {
                    continue;
                }

                // Yayıncı kampanyası yalnız kampanyanın oyununda yayın yaparken geçerli sayılır (kategori biliniyorsa).
                if (info.CategorySlug is { Length: > 0 } category
                    && !string.Equals(category, campaign.GameSlug, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Kick: {Streamer} canlı ama '{Category}' kategorisinde; '{Campaign}' için atlandı.", streamer.Login, category, campaign.Name);
                    continue;
                }

                live.Add(new LiveChannel(Platform.Kick, info.Id, info.Slug, info.DisplayName, info.Viewers, info.Title, streamer.ProfileUrl, info.AvatarUrl ?? streamer.AvatarUrl));
            }
            catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException)
            {
                _logger.LogWarning("Kick kanalı {Streamer} sorgulanamadı: {Reason}", streamer.Login, ex.Message);
            }
        }

        return (live, null);
    }
}
