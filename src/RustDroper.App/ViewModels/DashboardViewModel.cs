using System.Collections.ObjectModel;
using RustDroper.App.Infrastructure;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Localization;
using RustDroper.Core.Media;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.ViewModels;

/// <summary>Panonun bir güncellemesi için gereken tüm ham veri (hepsi salt okunur; sunucu-gerçeği + motor görüntüsü).</summary>
/// <param name="Campaigns">Son keşif sonucu (yoksa <c>null</c>).</param>
/// <param name="Inventory">Son envanter (yoksa <c>null</c>).</param>
/// <param name="ClaimedDropIds">Bilinen claim edilmiş drop kimlikleri (geçmiş + envanter + bu çalışma).</param>
public sealed record DashboardInput(
    EngineSnapshot Engine,
    bool EngineRunning,
    PlatformSnapshot? Campaigns,
    TwitchInventorySnapshot? Inventory,
    IReadOnlySet<string> ClaimedDropIds,
    DateTimeOffset Now,
    Platform Platform = Platform.Twitch);

/// <summary>
/// Salt okunur pano: "şu an", genel/yayıncı kampanya listeleri, claim edilen ödüller + Facepunch bağlantı durumu, son olaylar.
/// Yalnızca verilen <see cref="DashboardInput"/>'tan türetir; ağ/iş mantığı yoktur.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    public const string FacepunchHost = "twitch.facepunch.com";
    public const string KickFacepunchHost = "kick.facepunch.com";
    private const int MaxClaimedShown = 5;
    private const string UnknownStreamerKey = "Yayıncı";

    private static readonly Uri DefaultConnectUrl = new("https://twitch.facepunch.com/connect");
    private static readonly Uri DefaultKickConnectUrl = new("https://kick.facepunch.com/connect");

    private static readonly TimeSpan ImageRetry = TimeSpan.FromMinutes(10);

    private IReadOnlyList<string> _localPriority = [];
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly IDropMediaProvider? _media;
    private readonly Dictionary<string, DateTimeOffset> _imageRequests = new(StringComparer.Ordinal);
    private string _totalsText = string.Empty;
    private string _generalCountText = string.Empty;
    private string _streamerCountText = string.Empty;
    private string _claimedMoreText = string.Empty;
    private string _linkStatusText = Loc.T("dash.link.initial");
    private Severity _linkSeverity = Severity.Neutral;
    private Uri? _connectUrl;

    public DashboardViewModel(IBrowserLauncher browser, TimeProvider? time = null, TimeZoneInfo? zone = null, IDropMediaProvider? media = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _media = media;
        Now = new NowViewModel();
        OpenUrlCommand = new RelayCommand(p =>
        {
            if (p is Uri url)
            {
                browser.Open(url);
            }
        });
    }

    /// <summary>Arka planda bir görsel indi (herhangi bir iş parçacığında): sahibi pano güncellemesini UI iş parçacığına kuyruklar.</summary>
    public event Action? ImagesReady;

    public NowViewModel Now { get; }

    /// <summary>Yayıncı drop kartları, tek ızgarada ve öncelik sırasıyla (sürükle-bırakla sıralanır). Kartlar <see cref="StreamerCampaigns"/>'taki kalıcı örneklerdir.</summary>
    public ObservableCollection<DropRow> StreamerCards { get; } = [];

    /// <summary>Birden çok yayıncı kartı varken sıralama ipucu gösterilir.</summary>
    public bool HasStreamerOrderHint => StreamerCards.Count > 1;

    /// <summary>Kalıcı öncelik listesini (kampanya kimlikleri) verir; yoksa yalnız bu oturumdaki yerel sıra kullanılır.</summary>
    public Func<IReadOnlyList<string>>? PriorityProvider { get; set; }

    /// <summary>Kullanıcı sırayı değiştirince tam (yeni) öncelik listesiyle çağrılır (ayarlara kaydetmek için).</summary>
    public Action<IReadOnlyList<string>>? PriorityChanged { get; set; }

    /// <summary>Kart ortasındaki etkinlik bilgisi (ad, durum, tarihler, kalan süre); veri yoksa <c>HasEvent=false</c>.</summary>
    public EventInfoViewModel Event { get; } = new();

    public ObservableCollection<CampaignItem> GeneralCampaigns { get; } = [];

    public ObservableCollection<CampaignItem> StreamerCampaigns { get; } = [];

    public ObservableCollection<ClaimedItem> ClaimedRewards { get; } = [];

    public ObservableCollection<EventItem> Events { get; } = [];

    /// <summary>Bağlantıyı (kanal, Facepunch...) varsayılan tarayıcıda açar; parametre <see cref="Uri"/>.</summary>
    public RelayCommand OpenUrlCommand { get; }

    /// <summary>"5 Drop": bölüm başlığının yanındaki drop sayısı (kart yoksa boş).</summary>
    public string GeneralCountText
    {
        get => _generalCountText;
        private set => SetProperty(ref _generalCountText, value);
    }

    public string StreamerCountText
    {
        get => _streamerCountText;
        private set => SetProperty(ref _streamerCountText, value);
    }

    private static string CountText(IEnumerable<CampaignItem> campaigns)
    {
        var count = campaigns.Sum(c => c.Drops.Count);
        return count == 0 ? string.Empty : Loc.Plural("dash.count", count, count);
    }

    public string TotalsText
    {
        get => _totalsText;
        private set => SetProperty(ref _totalsText, value);
    }

    public string ClaimedMoreText
    {
        get => _claimedMoreText;
        private set => SetProperty(ref _claimedMoreText, value);
    }

    public string LinkStatusText
    {
        get => _linkStatusText;
        private set => SetProperty(ref _linkStatusText, value);
    }

    public Severity LinkSeverity
    {
        get => _linkSeverity;
        private set
        {
            if (SetProperty(ref _linkSeverity, value))
            {
                OnPropertyChanged(nameof(CanConnect));
            }
        }
    }

    /// <summary>Facepunch hesap bağlama sayfası (bağlı değilse/bilinmiyorsa "Bağla" için).</summary>
    public Uri? ConnectUrl
    {
        get => _connectUrl;
        private set => SetProperty(ref _connectUrl, value);
    }

    /// <summary>"Hesabı bağla" düğmesi yalnızca hesap bağlı değilse/bilinmiyorsa gösterilir.</summary>
    public bool CanConnect => LinkSeverity != Severity.Ok;

    public bool HasGeneral => GeneralCampaigns.Count > 0;

    public bool HasStreamer => StreamerCampaigns.Count > 0;

    public bool HasClaimed => ClaimedRewards.Count > 0;

    public bool HasEvents => Events.Count > 0;

    public void Tick()
    {
        var now = _time.GetUtcNow();
        Now.Tick(now);
        Event.Tick(now); // dakikada bir hesaplar
    }

    private readonly object _updateGate = new();

    /// <summary>Panoyu günceller. Üretimde arayüz iş parçacığında seri çağrılır; kilit, sahte (anında) dispatcher'larla farklı iş parçacıklarından gelen çağrılarda koleksiyon yarışını önler.</summary>
    public void Update(DashboardInput input)
    {
        lock (_updateGate)
        {
            UpdateCore(input);
        }
    }

    private void UpdateCore(DashboardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var now = input.Now;
        var progress = (input.Inventory?.InProgress ?? [])
            .GroupBy(p => p.DropId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var claimed = new HashSet<string>(input.ClaimedDropIds, StringComparer.Ordinal);
        claimed.UnionWith(progress.Values.Where(p => p.IsClaimed).Select(p => p.DropId));

        var session = input.EngineRunning ? input.Engine.Session : null;
        var currentCampaignId = input.EngineRunning ? input.Engine.Target?.Campaign.Id : null;
        var catalog = BuildCatalog(input.Platform);
        var eventData = BuildEvent(input);
        Event.Update(eventData, now, _zone);
        var eventEnded = eventData is not null && Event.Status == Core.Media.FacepunchEventStatus.Ended;
        var general = new List<CampaignData>();
        var streamer = new List<CampaignData>();
        var totalDrops = 0;
        var claimedDrops = 0;
        int? currentRemaining = null;
        string? currentDropName = null;

        foreach (var campaign in OrderedCampaigns(input.Campaigns, now))
        {
            var links = BuildStreamerLinks(campaign, input.Campaigns);
            var rows = campaign.Rewards
                .OrderBy(r => r.RequiredMinutes)
                .Select(r => BuildRow(r, progress, claimed, session, catalog, campaign.Scope == CampaignScope.Streamer ? links : [], input.Now))
                .ToList();
            rows = [.. rows.Select(r => r with { CampaignId = campaign.Id })];
            totalDrops += rows.Count;
            claimedDrops += rows.Count(r => r.IsClaimed);
            var isCurrent = campaign.Id == currentCampaignId;
            if (isCurrent)
            {
                currentRemaining = RemainingMinutes(campaign, progress, claimed, session);
                currentDropName = rows.FirstOrDefault(r => r.IsCurrent)?.Name;
            }

            var item = BuildCampaign(campaign, rows, isCurrent, input.Campaigns, now, links);
            (campaign.Scope == CampaignScope.Streamer ? streamer : general).Add(item);
        }

        Now.Update(input.Engine, input.EngineRunning, now, _zone, currentRemaining, currentDropName, input.Platform == Platform.Kick);
        if (general.Count == 0)
        {
            general.AddRange(CatalogCampaigns(catalog, streamerDrops: false, input.Now, eventEnded));
        }

        if (streamer.Count == 0)
        {
            streamer.AddRange(CatalogCampaigns(catalog, streamerDrops: true, input.Now, eventEnded));
        }

        var priority = PriorityProvider?.Invoke() ?? _localPriority;
        streamer = [.. streamer.OrderBy(c => DropPlanner.RankOf(priority, c.Id))]; // kullanıcı sırası; sırasızlar sona (kendi sırasıyla)
        ReconcileCampaigns(GeneralCampaigns, general);
        ReconcileCampaigns(StreamerCampaigns, streamer);
        SyncStreamerCards();
        // Gerçek kampanya yoksa toplam satırı gösterilmez (boş-durum metni bölümlerin kendi satırıdır: "Aktif … yok").
        TotalsText = totalDrops == 0 ? string.Empty : Loc.T("dash.totals", claimedDrops, totalDrops);
        BuildClaimed(input.Inventory, input.Campaigns, input.Platform);
        Replace(Events, input.Engine.Recent.Reverse().Select(e => new EventItem(
            TimeZoneInfo.ConvertTime(e.At, _zone).ToString("HH:mm:ss", Fmt.Tr),
            Fmt.EventSeverity(e.Level),
            e.Text)).ToList());
        GeneralCountText = CountText(GeneralCampaigns);
        StreamerCountText = CountText(StreamerCampaigns);
        OnPropertyChanged(nameof(HasGeneral));
        OnPropertyChanged(nameof(HasStreamer));
        OnPropertyChanged(nameof(HasClaimed));
        OnPropertyChanged(nameof(HasEvents));
    }

    /// <summary>
    /// Kampanyada kalan izleme süresi. Drop'lar paralel ilerliyor varsayımıyla (planlayıcıyla tutarlı, bkz. U4) en uzun kalan eşik.
    /// </summary>
    internal static int RemainingMinutes(Campaign campaign, IReadOnlyDictionary<string, DropProgress> progress, ISet<string> claimed, DropSession? session)
    {
        var remaining = 0;
        foreach (var reward in campaign.Rewards.Where(r => !claimed.Contains(r.Id)))
        {
            var watched = progress.TryGetValue(reward.Id, out var p) ? p.WatchedMinutes : 0;
            if (session?.DropId == reward.Id)
            {
                watched = Math.Max(watched, session.CurrentMinutes);
            }

            remaining = Math.Max(remaining, reward.RequiredMinutes - watched);
        }

        return Math.Max(0, remaining);
    }

    private static IEnumerable<Campaign> OrderedCampaigns(PlatformSnapshot? snapshot, DateTimeOffset now) =>
        (snapshot?.Campaigns ?? [])
            .Where(c => c.Status != CampaignStatus.Expired && (c.EndsAt is null || c.EndsAt > now))
            .OrderByDescending(c => c.IsRunningAt(now))
            .ThenBy(c => c.EndsAt ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.Name, StringComparer.Ordinal);

    private DropRowData BuildRow(
        DropReward reward,
        IReadOnlyDictionary<string, DropProgress> progress,
        ISet<string> claimed,
        DropSession? session,
        IReadOnlyDictionary<string, DropMedia> catalog,
        IReadOnlyList<StreamerLink> streamers,
        DateTimeOffset now)
    {
        var required = Math.Max(1, reward.RequiredMinutes);
        var live = session?.DropId == reward.Id;
        var inventory = progress.GetValueOrDefault(reward.Id);
        var watched = inventory?.WatchedMinutes ?? 0;
        if (live)
        {
            watched = Math.Max(watched, session!.CurrentMinutes);
        }

        var isClaimed = claimed.Contains(reward.Id);
        if (isClaimed)
        {
            watched = required;
        }

        var percent = Math.Min(100.0, watched * 100.0 / required);
        var remaining = Math.Max(0, required - watched);
        var (state, severity, kind) = (isClaimed, live, watched >= required, inventory?.IsClaimable == true, watched > 0) switch
        {
            (true, _, _, _, _) => (Loc.T("drop.state.claimed"), Severity.Ok, DropState.Claimed),
            (_, true, true, _, _) => (Loc.T("drop.state.claimPending"), Severity.Warn, DropState.ClaimReady),
            (_, true, _, _, _) => (Loc.T("drop.state.watching"), Severity.Info, DropState.Watching),
            (_, _, _, true, _) => (Loc.T("drop.state.claimable"), Severity.Warn, DropState.ClaimReady),
            (_, _, true, _, _) => (Loc.T("drop.state.completed"), Severity.Warn, DropState.ClaimReady),
            (_, _, _, _, true) => (Loc.T("drop.state.inProgress"), Severity.Neutral, DropState.InProgress),
            _ => (Loc.T("drop.state.queued"), Severity.Neutral, DropState.Queued),
        };

        catalog.TryGetValue(DropNames.Key(reward.Name), out var media);
        var primary = streamers.Count > 0 ? streamers[0] : null;
        return new DropRowData(
            reward.Id,
            reward.Name,
            percent,
            Fmt.Percent(percent),
            $"{Fmt.Minutes(watched)} / {Fmt.Minutes(required)}",
            isClaimed || remaining == 0 ? string.Empty : Loc.T("drop.remaining", Fmt.Minutes(remaining)),
            state,
            severity,
            isClaimed,
            live,
            kind,
            Nbsp(Fmt.Minutes(required)),
            media?.ImagePath ?? ResolveImage(media?.ImageUrl ?? reward.ImageUrl, now),
            media?.ImageUrl ?? reward.ImageUrl,
            primary is null ? null : streamers.Count > 1 ? $"{primary.Name} +{streamers.Count - 1}" : primary.Name,
            primary?.Url,
            primary?.IsLive == true);
    }

    /// <summary>
    /// Etkinlik bilgisi: platformun resmî Rust etkinliği (Facepunch sayfası) varsa ad/tarih/ayrıntı ONDAN gelir; keşfedilen kampanyalar (genel &gt; yayıncı;
    /// aktif &gt; yaklaşan &gt; son biten) yalnızca Facepunch verisi yoksa ya da eksik alanı tamamlamak için kullanılır. Böylece Rust dışı bir yayıncının
    /// (ör. rozet veren) kampanyası "etkinlik" olarak görünmez. Hiçbir veri yoksa <c>null</c>.
    /// </summary>
    private EventData? BuildEvent(DashboardInput input)
    {
        var now = input.Now;
        var fp = _media?.CurrentEvent(input.Platform, now);
        var campaign = (input.Campaigns?.Campaigns ?? [])
            .OrderBy(c => c.Scope == CampaignScope.General ? 0 : 1)
            .ThenBy(c => c.IsRunningAt(now) ? 0 : c.StartsAt > now ? 1 : 2)
            .ThenBy(c => c.IsRunningAt(now) || c.StartsAt > now ? (c.StartsAt ?? DateTimeOffset.MaxValue).UtcTicks : -(c.EndsAt ?? DateTimeOffset.MinValue).UtcTicks)
            .FirstOrDefault();
        if (campaign is null && fp is null)
        {
            return null;
        }

        var fallbackTitle = Loc.T(input.Platform == Platform.Kick ? "dash.event.kick" : "dash.event.twitch");
        var title = fp?.Title ?? campaign?.Name ?? fallbackTitle;
        var start = fp?.StartsAt ?? campaign?.StartsAt;
        var end = fp?.EndsAt ?? campaign?.EndsAt;
        var detail = fp is not null ? EventInfoViewModel.DetailFrom(fp) : null;
        var badge = fp?.BadgeClass ?? campaign?.Status switch
        {
            CampaignStatus.Upcoming => "is-pending",
            CampaignStatus.Active => "is-active",
            CampaignStatus.Expired => "is-ended",
            _ => null,
        };
        return start is null && end is null && badge is null ? null : new EventData(title, detail, start, end, badge);
    }

    /// <summary>Süre metni ("5 sa") kart başlığında satır sonunda bölünmesin.</summary>
    private static string Nbsp(string text) => text.Replace(' ', ' ');

    /// <summary>Ham görsel adresini yerel dosyaya çevirir: önbellekteyse yol; değilse arka planda indirmeyi başlatır (10 dk'da bir yeniden dener) ve <c>null</c> döner.</summary>
    private string? ResolveImage(Uri? url, DateTimeOffset now)
    {
        if (url is null || _media is null)
        {
            return null;
        }

        if (_media.CachedPath(url) is { } path)
        {
            return path;
        }

        var key = url.AbsoluteUri;
        if (!_imageRequests.TryGetValue(key, out var last) || now - last >= ImageRetry)
        {
            _imageRequests[key] = now;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await _media.EnsureAsync(url, CancellationToken.None).ConfigureAwait(false) is not null)
                    {
                        ImagesReady?.Invoke();
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // görsel olmadan devam: kart yer tutucu çizer
                }
            });
        }

        return null;
    }

    /// <summary>Platformun resmî sayfasından çekilen katalog, ad anahtarına göre (ilk eşleşen kazanır).</summary>
    private IReadOnlyDictionary<string, DropMedia> BuildCatalog(Platform platform)
    {
        var map = new Dictionary<string, DropMedia>(StringComparer.Ordinal);
        foreach (var drop in _media?.Catalog(platform) ?? [])
        {
            map.TryAdd(DropNames.Key(drop.Name), drop);
        }

        return map;
    }

    /// <summary>Kampanya yokken de kartlar görünsün: katalogdan "Etkinlik bekleniyor" kartları (ilerleme 0).</summary>
    private IEnumerable<CampaignData> CatalogCampaigns(IReadOnlyDictionary<string, DropMedia> catalog, bool streamerDrops, DateTimeOffset now, bool eventEnded)
    {
        var drops = catalog.Values.Where(d => d.IsStreamerDrop == streamerDrops).ToList();
        if (drops.Count == 0)
        {
            yield break;
        }

        DropRowData Row(DropMedia d, IReadOnlyList<StreamerLink> links)
        {
            var required = d.Minutes ?? 0;
            return new DropRowData(
                "fp:" + DropNames.Key(d.Name),
                d.Name,
                0,
                Fmt.Percent(0),
                required > 0 ? $"{Fmt.Minutes(0)} / {Fmt.Minutes(required)}" : string.Empty,
                string.Empty,
                Loc.T(eventEnded ? "dash.eventEnded" : "dash.eventWaiting"),
                Severity.Neutral,
                false,
                false,
                eventEnded ? DropState.Ended : DropState.Waiting,
                required > 0 ? Nbsp(Fmt.Minutes(required)) : "—",
                d.ImagePath ?? ResolveImage(d.ImageUrl, now),
                d.ImageUrl,
                links.Count > 0 ? links[0].Name : d.StreamerName,
                links.Count > 0 ? links[0].Url : d.StreamerUrl,
                false);
        }

        if (!streamerDrops)
        {
            yield return new CampaignData(
                "fp-general", string.Empty, Loc.T(eventEnded ? "dash.eventEnded" : "dash.eventWaiting"), eventEnded ? Severity.Neutral : Severity.Info, string.Empty, [], string.Empty,
                Loc.Plural("dash.catalog.general", drops.Count, drops.Count), false, drops.Select(d => Row(d, [])).ToList());
            yield break;
        }

        // Kimlik (öncelik listesine kaydedilir) dilden bağımsız kalır: adı bilinmeyen yayıncı grubu her dilde aynı anahtarı kullanır.
        foreach (var group in drops.GroupBy(d => d.StreamerName ?? UnknownStreamerKey, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var shownName = group.Key == UnknownStreamerKey ? Loc.T("dash.streamerDefault") : group.Key;
            IReadOnlyList<StreamerLink> links = first.StreamerUrl is { } url ? [new StreamerLink(shownName, url, false)] : [];
            yield return new CampaignData(
                "fp-streamer:" + DropNames.Key(group.Key), shownName, Loc.T(eventEnded ? "dash.eventEnded" : "dash.eventWaiting"), eventEnded ? Severity.Neutral : Severity.Info, string.Empty, links, string.Empty,
                Loc.Plural("dash.count", group.Count(), group.Count()), false, group.Select(d => Row(d, links) with { CampaignId = "fp-streamer:" + DropNames.Key(group.Key) }).ToList());
        }
    }

    /// <summary>
    /// Bir yayıncı kartını (ve kampanyasını) <paramref name="targetIndex"/> konumundaki kartın yerine taşır, yeni öncelik listesini kaydeder
    /// (<see cref="PriorityChanged"/>) ve görünümü hemen yeniden sıralar. Aynı kampanyanın kartları birlikte gider.
    /// </summary>
    public void MoveStreamerCard(DropRow row, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.CampaignId is not { } moving || StreamerCards.Count == 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, StreamerCards.Count - 1);
        var order = StreamerCards.Select(c => c.CampaignId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var targetCampaign = StreamerCards[targetIndex].CampaignId;
        if (targetCampaign is null || string.Equals(targetCampaign, moving, StringComparison.Ordinal))
        {
            return;
        }

        var from = order.IndexOf(moving);
        var to = order.IndexOf(targetCampaign);
        order.RemoveAt(from);
        order.Insert(to, moving); // ileri taşımada hedefin arkasına, geriye taşımada önüne düşer

        var saved = PriorityProvider?.Invoke() ?? _localPriority;
        var full = PriorityLists.Clean([.. order, .. saved.Where(id => !order.Contains(id, StringComparer.Ordinal))]); // şu an görünmeyenlerin sırası korunur
        _localPriority = full;
        PriorityChanged?.Invoke(full);
        ApplyStreamerOrder(full);
    }

    private void ApplyStreamerOrder(IReadOnlyList<string> priority)
    {
        var sorted = StreamerCampaigns.OrderBy(c => DropPlanner.RankOf(priority, c.Id)).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var at = StreamerCampaigns.IndexOf(sorted[i]);
            if (at != i)
            {
                StreamerCampaigns.Move(at, i);
            }
        }

        SyncStreamerCards();
    }

    /// <summary>Yayıncı kartları = yayıncı kampanyalarının kartları (kampanya sırasıyla); mevcut örnekler yerinde korunur.</summary>
    private void SyncStreamerCards()
    {
        var wanted = StreamerCampaigns.SelectMany(c => c.Drops).ToList();
        var hintBefore = HasStreamerOrderHint;
        for (var i = StreamerCards.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(StreamerCards[i]))
            {
                StreamerCards.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var at = StreamerCards.IndexOf(wanted[i]);
            if (at < 0)
            {
                StreamerCards.Insert(i, wanted[i]);
            }
            else if (at != i)
            {
                StreamerCards.Move(at, i);
            }
        }

        if (hintBefore != HasStreamerOrderHint)
        {
            OnPropertyChanged(nameof(HasStreamerOrderHint));
        }
    }

    private void ReconcileCampaigns(ObservableCollection<CampaignItem> target, IReadOnlyList<CampaignData> data) =>
        Reconcile.Items(target, data, item => item.Id, d => d.Id, id => new CampaignItem(id), (item, d) => item.Apply(d));

    private static IReadOnlyList<StreamerLink> BuildStreamerLinks(Campaign campaign, PlatformSnapshot? snapshot)
    {
        if (campaign.Scope != CampaignScope.Streamer)
        {
            return [];
        }

        var live = snapshot is not null && snapshot.ChannelsByCampaign.TryGetValue(campaign.Id, out var channels) ? channels : [];
        var liveIds = live.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return campaign.Streamers
            .Select(s => new StreamerLink(s.DisplayName, s.ProfileUrl, liveIds.Contains(s.Id) || live.Any(c => string.Equals(c.Login, s.Login, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    private CampaignData BuildCampaign(Campaign campaign, IReadOnlyList<DropRowData> rows, bool isCurrent, PlatformSnapshot? snapshot, DateTimeOffset now, IReadOnlyList<StreamerLink> streamers)
    {
        var running = campaign.IsRunningAt(now);
        var live = snapshot is not null && snapshot.ChannelsByCampaign.TryGetValue(campaign.Id, out var channels)
            ? channels
            : [];
        var timeText = running
            ? campaign.EndsAt is { } end ? Loc.T("dash.campaign.ends", Fmt.When(end, now, _zone)) : Loc.T("dash.campaign.noLimit")
            : campaign.StartsAt is { } start ? Loc.T("dash.campaign.starts", Fmt.When(start, now, _zone)) : Loc.T("dash.campaign.soon");
        var watchable = campaign.Scope == CampaignScope.General
            ? live.Count > 0 ? Loc.Plural("dash.campaign.liveChannels", live.Count, live.Count) : Loc.T("dash.campaign.noLive")
            : string.Empty;

        return new CampaignData(
            campaign.Id,
            campaign.Name,
            Loc.T(running ? "dash.campaign.active" : "dash.campaign.upcoming"),
            running ? Severity.Ok : Severity.Info,
            timeText,
            streamers,
            watchable,
            Loc.T("dash.campaign.summary", rows.Count(r => r.IsClaimed), rows.Count),
            isCurrent,
            rows);
    }

    private void BuildClaimed(TwitchInventorySnapshot? inventory, PlatformSnapshot? snapshot, Platform platform)
    {
        var host = platform == Platform.Kick ? KickFacepunchHost : FacepunchHost;
        var claimed = inventory?.Claimed ?? [];
        // Son 5 (yeni → eski; tarihi bilinmeyenler sonda, kendi sırasıyla); yalnız bunların görseli indirilir.
        var shown = claimed.OrderByDescending(r => r.LastAwardedAt).DistinctBy(r => r.Id, StringComparer.Ordinal).Take(MaxClaimedShown)
            .Select(r => new ClaimedData(
                r.Id,
                r.Name,
                ClaimSource(r, platform),
                string.Join(" · ", new[]
                {
                    r.Count > 1 ? $"×{r.Count}" : null,
                    r.LastAwardedAt is { } at ? TimeZoneInfo.ConvertTime(at, _zone).ToString(Loc.T("fmt.date.dayYear"), Loc.Culture) : null,
                }.Where(x => x is not null)),
                ResolveImage(r.ImageUrl, _time.GetUtcNow())))
            .ToList();
        Reconcile.Items(ClaimedRewards, shown, item => item.Id, d => d.Id, id => new ClaimedItem(id), (item, d) => item.Apply(d));
        ClaimedMoreText = claimed.Count > MaxClaimedShown ? Loc.Plural("dash.claimedMore", claimed.Count - MaxClaimedShown, claimed.Count - MaxClaimedShown) : string.Empty;

        var linked = inventory?.IsLinkedTo(host);
        (LinkStatusText, LinkSeverity) = linked switch
        {
            true => (Loc.T("dash.link.linked"), Severity.Ok),
            false => (Loc.T("dash.link.notLinked"), Severity.Error),
            _ => (Loc.T("dash.link.unknown"), Severity.Neutral),
        };
        ConnectUrl = claimed
            .Select(r => r.AccountLinkUrl)
            .FirstOrDefault(u => u is not null && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
            ?? snapshot?.Campaigns.Select(c => c.ConnectUrl).FirstOrDefault(u => u is not null)
            ?? (platform == Platform.Kick ? DefaultKickConnectUrl : DefaultConnectUrl);
    }

    /// <summary>Kaynak satırı (yayıncı adı envanterde yok): oyun adı → "Rust · Facepunch" (Facepunch bağlantılı) → platform adı.</summary>
    internal static string ClaimSource(ClaimedReward reward, Platform platform) =>
        !string.IsNullOrWhiteSpace(reward.GameName) ? reward.GameName
        : reward.AccountLinkUrl is { } link && link.Host.EndsWith("facepunch.com", StringComparison.OrdinalIgnoreCase) ? "Rust · Facepunch"
        : platform == Platform.Kick ? "Kick" : "Twitch";

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
