namespace RustDroper.Core.Orchestration;

/// <summary>
/// İşletim sisteminden gelen, motoru ilgilendiren olaylar (test: sahte). Dinleyiciler hızlı dönmelidir
/// (uyku bildirimi kısa sürede cevap bekler); olaylar herhangi bir iş parçacığında gelebilir.
/// </summary>
public interface ISystemEvents
{
    /// <summary>Sistem uykuya/hazırda bekletmeye geçiyor.</summary>
    event Action? Suspending;

    /// <summary>Sistem uyandı (ağ henüz hazır olmayabilir).</summary>
    event Action? Resumed;

    /// <summary>Ağ erişilebilirliği değişti (<c>true</c> = en az bir bağlantı açık).</summary>
    event Action<bool>? NetworkAvailabilityChanged;
}
