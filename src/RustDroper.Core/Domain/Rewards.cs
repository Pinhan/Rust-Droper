namespace RustDroper.Core.Domain;

/// <summary>İlerlemekte olan tek bir drop'un sunucu tarafı durumu (sunucu-gerçeği).</summary>
/// <param name="ClaimId">Claim için gereken kimlik (Twitch: dropInstanceID); henüz yoksa <c>null</c>.</param>
public sealed record DropProgress(
    string CampaignId,
    string CampaignName,
    string DropId,
    string RewardName,
    int RequiredMinutes,
    int WatchedMinutes,
    bool IsClaimed,
    string? ClaimId,
    bool PreconditionsMet)
{
    public int RemainingMinutes => Math.Max(0, RequiredMinutes - WatchedMinutes);

    public double Percent => RequiredMinutes <= 0 ? 0 : Math.Min(100.0, WatchedMinutes * 100.0 / RequiredMinutes);

    public bool IsClaimable => !IsClaimed && ClaimId is not null && WatchedMinutes >= RequiredMinutes;
}

/// <summary>Daha önce talep (claim) edilmiş bir ödül (platform envanteri / geçmişi).</summary>
/// <param name="AccountLinkUrl">Ödülün teslimi için gereken hesap bağlama sayfası (Rust için Facepunch /connect).</param>
/// <param name="AccountLinked">Hesap bağlı mı; API bilgi vermiyorsa <c>null</c>.</param>
/// <param name="GameName">Ödülün oyunu (Twitch <c>game.displayName</c>); Rust ödüllerinde genellikle yok (<c>null</c>). Yayıncı adı envanterde YOKTUR.</param>
public sealed record ClaimedReward(
    Platform Platform,
    string Id,
    string Name,
    DateTimeOffset? LastAwardedAt,
    int Count,
    bool? AccountLinked,
    Uri? AccountLinkUrl,
    Uri? ImageUrl,
    string? GameName = null);
