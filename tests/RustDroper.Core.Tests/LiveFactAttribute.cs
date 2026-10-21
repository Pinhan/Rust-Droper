namespace RustDroper.Core.Tests;

/// <summary>Gerçek ağa çıkan test. Yalnızca <c>RUSTDROPER_LIVE=1</c> iken çalışır; aksi halde "atlandı" görünür.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUSTDROPER_LIVE") != "1")
        {
            Skip = "Canlı test: RUSTDROPER_LIVE=1 ayarlayarak çalıştır.";
        }
    }
}
