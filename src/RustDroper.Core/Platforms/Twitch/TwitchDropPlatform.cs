using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch.PubSub;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Platforms.Twitch;

/// <summary>
/// Motorun Twitch bağdaştırıcısı: kanal üzerinden keşif, HLS izleyici, <c>dropCurrentSession</c> yoklaması ve (varsa) PubSub +
/// otomatik claim. <paramref name="autoClaimer"/> verilmezse hiçbir şey claim edilmez.
/// </summary>
public sealed class TwitchDropPlatform : IDropPlatform
{
    private readonly TwitchGqlClient _gql;
    private readonly HttpClient _hlsHttp;
    private readonly string _userId;
    private readonly TwitchPubSubClient? _pubsub;
    private readonly TwitchAutoClaimer? _autoClaimer;
    private readonly HlsQuality _quality;
    private readonly int _probeChannels;
    private readonly ILogger _logger;
    private IReadOnlyCollection<string> _knownEligible = [];
    private readonly Func<Campaign, bool>? _campaignFilter;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _dropCampaigns = new(StringComparer.Ordinal);

    public TwitchDropPlatform(
        TwitchGqlClient gql,
        HttpClient hlsHttp,
        string userId,
        TwitchPubSubClient? pubsub = null,
        TwitchAutoClaimer? autoClaimer = null,
        HlsQuality quality = HlsQuality.LowestVideo,
        int probeChannels = 100,
        ILogger? logger = null,
        Func<Campaign, bool>? campaignFilter = null)
    {
        ArgumentNullException.ThrowIfNull(gql);
        ArgumentNullException.ThrowIfNull(hlsHttp);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        _gql = gql;
        _hlsHttp = hlsHttp;
        _userId = userId;
        _pubsub = pubsub;
        _autoClaimer = autoClaimer;
        _quality = quality;
        _probeChannels = probeChannels;
        _logger = logger ?? NullLogger.Instance;
        _campaignFilter = campaignFilter;

        if (pubsub is not null)
        {
            pubsub.Subscribe(OnPubSubEvent);
            autoClaimer?.Attach(pubsub);
        }

        if (autoClaimer is not null)
        {
            autoClaimer.Completed += record =>
                DropClaimed?.Invoke(new ClaimNotice(record.DropId, record.Outcome != "Failed", record.Status));

            if (pubsub is not null)
            {
                // drop-claim olayı gelmezse (U8 canlı gözlemi) tamamlanan drop için yedek claim.
                // (Abonelik PubSub istemcisinde tutulur; platform ömrü kadar yaşar.)
                new TwitchClaimFallback(userId, autoClaimer.Enqueue, dropId => _dropCampaigns.GetValueOrDefault(dropId), logger: _logger)
                    .Attach(pubsub);
            }
        }
    }

    public Platform Platform => Platform.Twitch;

    public event Action<string>? ChannelWentOffline;

    public event Action<ClaimNotice>? DropClaimed;

    public async Task<PlatformSnapshot> DiscoverAsync(string gameSlug, CancellationToken cancellationToken)
    {
        var discovery = await new TwitchCampaignDiscovery(_gql, _logger)
            .DiscoverAsync(gameSlug, _probeChannels, cancellationToken, knownEligibleChannelIds: _knownEligible).ConfigureAwait(false);
        _knownEligible = [.. discovery.ChannelsByCampaign.Values.SelectMany(l => l).Select(c => c.Id).Distinct(StringComparer.Ordinal)];
        // "Yalnız Rust ödülleri": Rust yayınında bulunan ama Rust ödülü vermeyen kampanyalar (ör. yayıncının sohbet rozeti) elenir.
        var kept = discovery.Campaigns;
        var excluded = new List<string>();
        if (_campaignFilter is not null)
        {
            kept = [.. discovery.Campaigns.Where(c =>
            {
                if (_campaignFilter(c))
                {
                    return true;
                }

                excluded.Add(c.Name);
                return false;
            })];
            if (excluded.Count > 0)
            {
                _logger.LogInformation("Rust dışı kampanya atlandı: {Names}", string.Join(", ", excluded));
            }
        }

        foreach (var campaign in kept)
        {
            foreach (var reward in campaign.Rewards)
            {
                _dropCampaigns[reward.Id] = campaign.Id;
            }
        }

        var keptIds = kept.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var channels = discovery.ChannelsByCampaign.Where(kv => keptIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new PlatformSnapshot(kept, channels, excluded);
    }

    public IWatcher CreateWatcher() => new TwitchHlsWatcher(_gql, _hlsHttp, _quality, logger: _logger);

    public async Task<DropSession?> GetSessionAsync(LiveChannel channel, CancellationToken cancellationToken)
    {
        using var result = await _gql.ExecuteAsync(
            GqlOperations.CurrentDrop,
            TwitchCurrentSessionParser.Variables(channel.Id),
            cancellationToken).ConfigureAwait(false);
        var session = TwitchCurrentSessionParser.Parse(result.Data);
        return session is { DropId: { Length: > 0 } dropId }
            ? new DropSession(dropId, session.CurrentMinutes, session.RequiredMinutes)
            : null;
    }

    public void OnTargetChanged(LiveChannel? channel)
    {
        if (_pubsub is null)
        {
            return;
        }

        var topics = new List<string> { TwitchPubSubProtocol.UserDropTopic(_userId) };
        if (channel is not null)
        {
            topics.Add(TwitchPubSubProtocol.PlaybackTopic(channel.Id));
        }

        // Fire-and-forget: konu güncellemesi bağlı değilse yeniden bağlanınca zaten gönderilir.
        _ = _pubsub.SetTopicsAsync(topics).ContinueWith(
            t => _logger.LogWarning("PubSub konuları güncellenemedi: {Reason}", t.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        if (_pubsub is not null)
        {
            await _pubsub.SetTopicsAsync([TwitchPubSubProtocol.UserDropTopic(_userId)], cancellationToken).ConfigureAwait(false);
            tasks.Add(_pubsub.RunAsync(cancellationToken));
        }

        if (_autoClaimer is not null)
        {
            tasks.Add(_autoClaimer.RunAsync(cancellationToken));
        }

        if (tasks.Count == 0)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void OnPubSubEvent(TwitchPubSubEvent pubSubEvent)
    {
        if (pubSubEvent is StreamStateEvent { IsUp: false } down)
        {
            ChannelWentOffline?.Invoke(down.ChannelId);
        }
    }
}
