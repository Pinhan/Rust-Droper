namespace RustDroper.Core.Domain;

public enum Platform
{
    Twitch,
    Kick,
}

/// <summary>Genel = kategorideki Drops-Enabled herhangi bir yayın; Yayıncı = belirli kanal(lar).</summary>
public enum CampaignScope
{
    General,
    Streamer,
}

public enum CampaignStatus
{
    Unknown,
    Upcoming,
    Active,
    Expired,
}

public sealed record Streamer(string Id, string Login, string DisplayName, Uri ProfileUrl, Uri? AvatarUrl);

/// <summary>Tek bir drop ödülü. <see cref="RequiredMinutes"/> kampanya içindeki kümülatif izleme süresidir.</summary>
public sealed record DropReward(string Id, string Name, int RequiredMinutes, Uri? ImageUrl);

public sealed record Campaign(
    Platform Platform,
    string Id,
    string Name,
    string GameSlug,
    string GameName,
    CampaignStatus Status,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    CampaignScope Scope,
    IReadOnlyList<Streamer> Streamers,
    IReadOnlyList<DropReward> Rewards,
    Uri? ConnectUrl)
{
    /// <summary>
    /// Tarihler biliniyorsa onlara göre, bilinmiyorsa API durumuna göre kampanya şu an çalışıyor mu.
    /// API'nin <c>status</c> alanı bayatlayabileceği için tarihler önceliklidir.
    /// </summary>
    public bool IsRunningAt(DateTimeOffset now)
    {
        if (StartsAt is { } start && now < start)
        {
            return false;
        }

        if (EndsAt is { } end && now >= end)
        {
            return false;
        }

        return StartsAt is not null || EndsAt is not null || Status == CampaignStatus.Active;
    }
}

public static class CampaignFilters
{
    public static IEnumerable<Campaign> ForGame(this IEnumerable<Campaign> campaigns, string gameSlug) =>
        campaigns.Where(c => string.Equals(c.GameSlug, gameSlug, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<Campaign> RunningAt(this IEnumerable<Campaign> campaigns, DateTimeOffset now) =>
        campaigns.Where(c => c.IsRunningAt(now));
}
