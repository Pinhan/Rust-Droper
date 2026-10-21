using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch.PubSub;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Platforms.Twitch;

/// <summary>
/// Gerçek Twitch bağlantısı kurar (CLI <c>run twitch</c> ile aynı kurulum): GQL + PubSub + HLS izleyici, isteğe bağlı otomatik claim.
/// <paramref name="gqlHttp"/> uzun ömürlüdür ve burada sahiplenilmez; HLS için bağlantı başına ayrı istemci açılır.
/// </summary>
public sealed class TwitchConnectionFactory : IDropConnectionFactory
{
    private readonly HttpClient _gqlHttp;
    private readonly TwitchClientProfile _profile;
    private readonly ISecretStore _secrets;
    private readonly string _hashesPath;
    private readonly JsonLinesLog<ClaimRecord> _history;
    private readonly HlsQuality _quality;
    private readonly ILogger _logger;
    private readonly Func<Domain.Campaign, bool>? _campaignFilter;

    public TwitchConnectionFactory(
        HttpClient gqlHttp,
        TwitchClientProfile profile,
        ISecretStore secrets,
        string hashesPath,
        JsonLinesLog<ClaimRecord> history,
        HlsQuality quality = HlsQuality.LowestVideo,
        ILogger? logger = null,
        Func<Domain.Campaign, bool>? campaignFilter = null)
    {
        ArgumentNullException.ThrowIfNull(gqlHttp);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashesPath);
        ArgumentNullException.ThrowIfNull(history);
        _gqlHttp = gqlHttp;
        _profile = profile;
        _secrets = secrets;
        _hashesPath = hashesPath;
        _history = history;
        _quality = quality;
        _logger = logger ?? NullLogger.Instance;
        _campaignFilter = campaignFilter;
    }

    public IDropConnection Create(TwitchSession session, Func<string?> accessToken, bool autoClaim)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(accessToken);
        var gql = new TwitchGqlClient(
            _gqlHttp, _profile, accessToken, new GqlHashRegistry(_hashesPath), TwitchDeviceId.GetOrCreate(_secrets), logger: _logger);
        var pubsub = new TwitchPubSubClient(accessToken, logger: _logger);
        var claimer = autoClaim ? new TwitchAutoClaimer(new TwitchClaimService(gql), _history, logger: _logger) : null;
        var hls = TwitchConstants.CreateHttpClient(_profile);
        var platform = new TwitchDropPlatform(gql, hls, session.UserId, pubsub, claimer, _quality, logger: _logger, campaignFilter: _campaignFilter);
        return new Connection(platform, gql, hls);
    }

    public IReadOnlyCollection<string> LoadClaimedDropIds() =>
        [.. _history.ReadAll().Where(r => r.Outcome != "Failed").Select(r => r.DropId)];

    private sealed class Connection(TwitchDropPlatform platform, TwitchGqlClient gql, HttpClient hls) : IDropConnection
    {
        public IDropPlatform Platform => platform;

        public async Task<TwitchInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken)
        {
            using var result = await gql.ExecuteAsync(
                GqlOperations.Inventory,
                new Dictionary<string, object?> { ["fetchRewardCampaigns"] = false },
                cancellationToken).ConfigureAwait(false);
            return TwitchInventoryParser.Parse(result.Data);
        }

        public void Dispose() => hls.Dispose();
    }
}
