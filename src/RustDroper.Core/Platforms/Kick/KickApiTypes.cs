namespace RustDroper.Core.Platforms.Kick;

/// <summary>Bir ödülün hesaba özgü durumu. <see cref="Progress"/> 0–1 aralığında kesir (referans modelindeki <c>progress</c>).</summary>
public sealed record KickRewardProgress(string RewardId, double Progress, bool Claimed)
{
    /// <summary>Ödül için gereken süre dolmuş mu (kayan nokta hatası payıyla).</summary>
    public bool IsComplete => Progress >= 0.999999;
}

/// <summary>Kampanyanın hesaba özgü ilerlemesi (<c>/drops/progress</c> öğesi ya da <c>/drops/progress/summary</c>).</summary>
/// <param name="ProgressUnits">İzlenen dakika (<c>progress_units</c>); sunucu vermediyse <c>null</c>.</param>
/// <param name="AppConnected">Hesap kampanyanın oyun/uygulamasına bağlı mı (<c>user_app_connected</c>; gerçek hesapla görüldü, 2026-10-19); yoksa <c>null</c>.</param>
public sealed record KickCampaignProgress(string CampaignId, double? ProgressUnits, IReadOnlyList<KickRewardProgress> Rewards, bool? AppConnected = null);

/// <param name="Rejected">Sunucu claim'i açıkça reddetti (400/404/409/422): tekrar denemek anlamsız. Geçici hata fırlatılır, buraya gelmez.</param>
public sealed record KickClaimResult(bool Success, bool Rejected, string? Detail);

/// <summary>Kanal bilgisi (<c>kick.com/api/v2/channels/{slug}</c>). <see cref="PlaybackUrl"/> IVS HLS ana listesidir (token'lı, kısa ömürlü; kayda/loga yazılmaz).</summary>
public sealed record KickChannelInfo(
    string Id,
    string Slug,
    string DisplayName,
    bool IsLive,
    Uri? PlaybackUrl,
    int Viewers,
    string? Title,
    Uri? AvatarUrl,
    int? CategoryId,
    string? CategorySlug);
