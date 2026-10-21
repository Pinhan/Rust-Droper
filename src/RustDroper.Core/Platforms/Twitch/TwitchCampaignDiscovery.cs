using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms;

namespace RustDroper.Core.Platforms.Twitch;

/// <param name="Channels">Oyunun Drops-Enabled canlı kanalları (dizinden).</param>
/// <param name="Campaigns">Kanallar üzerinden keşfedilen, izlemeyle kazanılan kampanyalar.</param>
/// <param name="ChannelsByCampaign">Kampanya kimliği → o kampanyanın gözlendiği kanal kimlikleri (izlenebilecek adaylar).</param>
/// <param name="Warnings">Atlanan/başarısız kanal sorguları (tüm keşfi düşürmez).</param>
public sealed record TwitchDiscovery(
    IReadOnlyList<LiveChannel> Channels,
    IReadOnlyList<Campaign> Campaigns,
    IReadOnlyDictionary<string, IReadOnlyList<LiveChannel>> ChannelsByCampaign,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Kampanya keşfi (kanal üzerinden). <c>ViewerDropsDashboard</c>/<c>DropCampaignDetails</c> SMARTBOX istemcisinde <c>null</c>
/// döndüğü için (2026-10-15 canlı sınama) kampanyalar şöyle bulunur: oyun dizini (DROPS_ENABLED) → ilk N kanalın
/// <c>AvailableDrops</c> sorgusu → kampanyaları kimliğe göre birleştir.
/// </summary>
public sealed class TwitchCampaignDiscovery
{
    private readonly TwitchGqlClient _gql;
    private readonly ILogger _logger;

    public TwitchCampaignDiscovery(TwitchGqlClient gql, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gql);
        _gql = gql;
        _logger = logger ?? NullLogger.Instance;
    }

    public static Dictionary<string, object?> DirectoryVariables(string gameSlug) => new()
    {
        ["limit"] = 30,
        ["slug"] = gameSlug,
        ["imageWidth"] = 50,
        ["includeCostreaming"] = false,
        ["includeIsDJ"] = false,
        ["sortTypeIsRecency"] = false,
        ["options"] = new Dictionary<string, object?>
        {
            ["includeRestricted"] = new[] { "SUB_ONLY_LIVE" },
            ["systemFilters"] = new[] { "DROPS_ENABLED" },
            ["sort"] = "RELEVANCE",
            ["tags"] = Array.Empty<string>(),
            ["broadcasterLanguages"] = Array.Empty<string>(),
            ["freeformTags"] = null,
            ["requestID"] = "JIRA-VXP-2397",
        },
    };

    public async Task<TwitchDiscovery> DiscoverAsync(string gameSlug, int channelsToProbe = 5, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSlug);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelsToProbe, 1);

        IReadOnlyList<LiveChannel> channels;
        using (var directory = await _gql.ExecuteAsync(GqlOperations.GameDirectory, DirectoryVariables(gameSlug), cancellationToken).ConfigureAwait(false))
        {
            channels = TwitchDirectoryParser.Parse(directory.Data);
        }

        var campaigns = new Dictionary<string, Campaign>(StringComparer.Ordinal);
        var seen = new Dictionary<string, List<LiveChannel>>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var channel in channels.Take(channelsToProbe))
        {
            try
            {
                using var available = await _gql.ExecuteAsync(
                    GqlOperations.AvailableDrops,
                    new Dictionary<string, object?> { ["channelID"] = channel.Id },
                    cancellationToken).ConfigureAwait(false);
                var parsed = TwitchAvailableDropsParser.Parse(available.Data);

                foreach (var campaign in parsed.Campaigns)
                {
                    campaigns.TryAdd(campaign.Id, campaign);
                    if (!seen.TryGetValue(campaign.Id, out var list))
                    {
                        seen[campaign.Id] = list = [];
                    }

                    list.Add(channel);
                }
            }
            catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException or TwitchTransientException)
            {
                warnings.Add($"{channel.Login}: {ex.Message}");
                _logger.LogWarning("Kanal {Channel} için AvailableDrops alınamadı: {Reason}", channel.Login, ex.Message);
            }
        }

        // Kapsam sezgisi [Ç]: kampanya, sorgulanan kanalların en az yarısında (ve ≥2'sinde) görülüyorsa genel; aksi halde
        // görüldüğü kanallara özel (ACL) varsayılır. Canlı gözlem: "Ignite" 3/5 kanalda (genel), "Season 10.5" 2/5 (kanala özgü).
        // İzleme kararı kapsama değil ChannelsByCampaign'e dayanır; kapsam yalnızca etikettir.
        var probed = Math.Min(channelsToProbe, channels.Count);
        var finalCampaigns = campaigns.Values
            .Select(c => seen[c.Id].Count >= 2 && seen[c.Id].Count * 2 >= probed
                ? c with { Scope = CampaignScope.General }
                : c with
                {
                    Scope = CampaignScope.Streamer,
                    Streamers = [.. seen[c.Id].Select(ch => new Streamer(ch.Id, ch.Login, ch.DisplayName, ch.Url, ch.AvatarUrl))],
                })
            .OrderBy(c => c.EndsAt ?? DateTimeOffset.MaxValue)
            .ToList();

        return new TwitchDiscovery(
            channels,
            finalCampaigns,
            seen.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<LiveChannel>)kv.Value, StringComparer.Ordinal),
            warnings);
    }
}
