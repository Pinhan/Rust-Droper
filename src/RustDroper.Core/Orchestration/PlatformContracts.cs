using RustDroper.Core.Domain;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Orchestration;

/// <param name="ChannelsByCampaign">Kampanya kimliği → o kampanyanın izlenebildiği canlı kanallar (gözlenen).</param>
public sealed record PlatformSnapshot(
    IReadOnlyList<Campaign> Campaigns,
    IReadOnlyDictionary<string, IReadOnlyList<LiveChannel>> ChannelsByCampaign,
    IReadOnlyList<string>? ExcludedCampaignNames = null)
{
    /// <summary>"Yalnız Rust ödülleri" süzgeciyle elenen (Rust dışı) kampanyaların adları.</summary>
    public IReadOnlyList<string> Excluded => ExcludedCampaignNames ?? [];
}

/// <summary>Sunucunun o an ilerlettiği drop (sunucu-gerçeği).</summary>
public sealed record DropSession(string DropId, int CurrentMinutes, int RequiredMinutes)
{
    public double Percent => RequiredMinutes <= 0 ? 0 : Math.Min(100.0, CurrentMinutes * 100.0 / RequiredMinutes);

    public bool IsComplete => RequiredMinutes > 0 && CurrentMinutes >= RequiredMinutes;
}

public sealed record ClaimNotice(string DropId, bool Success, string? Detail);

/// <summary>
/// Motorun platformdan beklediği asgari yetenekler. Hata sözleşmesi: kimlik sorunu <c>PlatformAuthException</c>, geçici ağ/sunucu
/// sorunu <c>Retry.IsTransient</c> ile tanınan istisnalar, şema kayması <c>PlatformDataException</c> fırlatır; motor hiçbirinde çökmez.
/// </summary>
public interface IDropPlatform
{
    Platform Platform { get; }

    Task<PlatformSnapshot> DiscoverAsync(string gameSlug, CancellationToken cancellationToken);

    IWatcher CreateWatcher();

    /// <summary>Verilen kanal için sunucunun drop oturumu; ilerleyen drop yoksa <c>null</c>.</summary>
    Task<DropSession?> GetSessionAsync(LiveChannel channel, CancellationToken cancellationToken);

    /// <summary>İzlenen kanal değişti (null = hiçbiri); platform buna bağlı dinleyicilerini (ör. PubSub konuları) günceller.</summary>
    void OnTargetChanged(LiveChannel? channel);

    /// <summary>Arka plan işleri (ör. PubSub bağlantısı, otomatik claim); iptal edilene kadar çalışır.</summary>
    Task RunBackgroundAsync(CancellationToken cancellationToken);

    /// <summary>Bir kanalın yayını kapandı (kanal kimliği).</summary>
    event Action<string>? ChannelWentOffline;

    /// <summary>Bir drop claim işlemi sonuçlandı.</summary>
    event Action<ClaimNotice>? DropClaimed;
}
