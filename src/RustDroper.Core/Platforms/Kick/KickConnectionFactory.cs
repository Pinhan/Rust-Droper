using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// Gerçek Kick bağlantısı kurar: kimlikli API + HLS izleyici, isteğe bağlı otomatik claim (yalnızca <c>autoClaim</c> true ise).
/// <paramref name="http"/> uzun ömürlüdür ve burada sahiplenilmez; HLS için bağlantı başına ayrı istemci açılır (<paramref name="hlsHttp"/> ile değiştirilebilir).
/// Tüm bağlantılar tek bir hız sınırlayıcıyı paylaşır (Kick'e hesap başına toplam ≈2 istek/sn).
/// </summary>
public sealed class KickConnectionFactory : IDropConnectionFactory
{
    private readonly HttpClient _http;
    private readonly JsonLinesLog<ClaimRecord> _history;
    private readonly Func<string> _gameSlug;
    private readonly HlsQuality _quality;
    private readonly Func<HttpClient> _hlsHttp;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly TimeSpan? _claimPollInterval;
    private readonly IKickPlayerHost? _playerHost;
    private readonly KickWebViewOptions? _webViewOptions;
    private readonly ILogger _logger;

    public KickConnectionFactory(
        HttpClient http,
        JsonLinesLog<ClaimRecord> history,
        Func<string> gameSlug,
        HlsQuality quality = HlsQuality.LowestVideo,
        Func<HttpClient>? hlsHttp = null,
        SlidingWindowRateLimiter? limiter = null,
        TimeSpan? claimPollInterval = null,
        ILogger? logger = null,
        IKickPlayerHost? playerHost = null,
        KickWebViewOptions? webViewOptions = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(gameSlug);
        _http = http;
        _history = history;
        _gameSlug = gameSlug;
        _quality = quality;
        _hlsHttp = hlsHttp ?? KickConstants.CreateHttpClient;
        _limiter = limiter ?? new SlidingWindowRateLimiter(2, TimeSpan.FromSeconds(1));
        _claimPollInterval = claimPollInterval;
        _playerHost = playerHost;
        _webViewOptions = webViewOptions;
        _logger = logger ?? NullLogger.Instance;
    }

    public IDropConnection Create(TwitchSession session, Func<string?> accessToken, bool autoClaim)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(accessToken);
        var api = new KickApiClient(_http, accessToken, _limiter, logger: _logger);
        var publicClient = new KickPublicClient(_http);
        var hls = _hlsHttp();
        KickDropPlatform? platform = null;
        var claimer = autoClaim
            ? new KickAutoClaimer(api, _history, () => platform!.KnownCampaignIds, logger: _logger, pollInterval: _claimPollInterval)
            : null;
        platform = new KickDropPlatform(publicClient, api, hls, claimer, _quality, logger: _logger, playerHost: _playerHost, webViewOptions: _webViewOptions);
        return new Connection(platform, api, publicClient, _gameSlug, hls);
    }

    public IReadOnlyCollection<string> LoadClaimedDropIds() =>
        [.. _history.ReadAll().Where(r => r.Platform == "Kick" && r.Outcome == "Claimed").Select(r => r.DropId)];

    private sealed class Connection(KickDropPlatform platform, KickApiClient api, KickPublicClient publicClient, Func<string> gameSlug, HttpClient hls) : IDropConnection
    {
        public IDropPlatform Platform => platform;

        public async Task<TwitchInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken)
        {
            var progress = await api.GetProgressAsync(cancellationToken).ConfigureAwait(false);
            var campaigns = await publicClient.GetCampaignsAsync(cancellationToken).ConfigureAwait(false);
            return KickInventoryBuilder.Build(progress.Items, [.. campaigns.Items.ForGame(gameSlug())]);
        }

        public void Dispose() => hls.Dispose();
    }
}

/// <summary>
/// Kick ilerleme verisini (hesaba özgü) kampanya bilgisiyle (kamusal) birleştirip arayüzün envanter şekline çevirir.
/// Yalnızca verilen (Rust) kampanyalar işlenir; hesapta ilerlemesi olmayan kampanya atlanır.
/// </summary>
public static class KickInventoryBuilder
{
    public static TwitchInventorySnapshot Build(IReadOnlyList<KickCampaignProgress> progress, IReadOnlyList<Campaign> campaigns)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(campaigns);
        var inProgress = new List<DropProgress>();
        var claimed = new List<ClaimedReward>();

        foreach (var campaign in campaigns.OrderByDescending(c => c.EndsAt ?? DateTimeOffset.MinValue))
        {
            var state = progress.FirstOrDefault(p => string.Equals(p.CampaignId, campaign.Id, StringComparison.Ordinal));
            if (state is null)
            {
                continue;
            }

            var units = state.ProgressUnits ?? state.Rewards
                .Select(r => (State: r, Reward: campaign.Rewards.FirstOrDefault(x => x.Id == r.RewardId)))
                .Where(x => x.Reward is not null)
                .Select(x => x.State.Progress * x.Reward!.RequiredMinutes)
                .DefaultIfEmpty(0)
                .Max();

            foreach (var reward in campaign.Rewards.OrderBy(r => r.RequiredMinutes))
            {
                var rewardState = state.Rewards.FirstOrDefault(r => string.Equals(r.RewardId, reward.Id, StringComparison.Ordinal));
                var isClaimed = rewardState?.Claimed == true;
                var required = reward.RequiredMinutes;
                var watched = isClaimed ? required : (int)Math.Min(required, Math.Floor(units));
                var complete = watched >= required;
                inProgress.Add(new DropProgress(
                    campaign.Id,
                    campaign.Name,
                    reward.Id,
                    reward.Name,
                    required,
                    watched,
                    isClaimed,
                    complete && !isClaimed ? reward.Id : null,
                    PreconditionsMet: true));
                if (isClaimed)
                {
                    claimed.Add(new ClaimedReward(Platform.Kick, reward.Id, reward.Name, null, 1, state.AppConnected, campaign.ConnectUrl, reward.ImageUrl));
                }
            }
        }

        return new TwitchInventorySnapshot(claimed, inProgress);
    }
}
