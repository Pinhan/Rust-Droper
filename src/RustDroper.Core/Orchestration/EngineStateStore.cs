using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Orchestration;

/// <summary>
/// Motorun yeniden başlatmada koruduğu hafıza. Her kayıt kendi geçerlilik sonuyla gelir; süresi geçen kayıt yok sayılır.
/// </summary>
public sealed record EngineMemory
{
    /// <summary>Tamamlanan (sunucu oturumu bitmiş) kampanya kimliği → kaydın geçerlilik sonu.</summary>
    public Dictionary<string, DateTimeOffset> CompletedUntil { get; init; } = [];

    /// <summary>Bekletmeye alınan kampanya kimliği → bekletme sonu.</summary>
    public Dictionary<string, DateTimeOffset> CooldownUntil { get; init; } = [];
}

/// <summary>Motorun hafızasını saklayan küçük arayüz (testte sahte). Uygulamalar hata fırlatabilir; motor bunları yutar.</summary>
public interface IEngineStateStore
{
    EngineMemory Load();

    void Save(EngineMemory memory);
}

/// <summary>Hesabın son bilinen envanteri (açılışta arayüz hemen dolsun diye; sunucu-gerçeği değildir).</summary>
public sealed record StoredInventory(string UserId, DateTimeOffset SavedAt, TwitchInventorySnapshot Snapshot);

/// <summary><c>data\state.json</c> içeriği. Tek hesaba aittir: başka hesap girerse sıfırlanır.</summary>
public sealed class PersistedState
{
    public string? UserId { get; set; }

    public EngineMemory Memory { get; set; } = new();

    public CachedInventory? Inventory { get; set; }
}

public sealed class CachedInventory
{
    public DateTimeOffset SavedAt { get; set; }

    public List<ClaimedReward> Claimed { get; set; } = [];

    public List<DropProgress> InProgress { get; set; } = [];
}

/// <summary>
/// <c>data\state.json</c> sahibi: motor hafızası + envanter önbelleği. Atomik/yedekli yazılır (<see cref="AtomicJsonStore{T}"/>);
/// dosya yok/bozuk/daha yeni sürümse varsayılana düşer, yazma hataları günlüğe gider ve asla fırlatılmaz.
/// Veriler hesaba bağlıdır (<c>UserId</c>): başka hesap girerse eski veri kullanılmaz.
/// </summary>
public sealed class StateStore
{
    public const int SchemaVersion = 1;

    private readonly AtomicJsonStore<PersistedState> _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private PersistedState _state;

    public StateStore(string path, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _store = new AtomicJsonStore<PersistedState>(path, SchemaVersion);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        var loaded = _store.Load();
        _state = Normalize(loaded.Value);
        LoadWarning = loaded.Warning;
        if (loaded.Warning is not null)
        {
            _logger.LogWarning("Durum dosyası: {Warning}", loaded.Warning);
        }
    }

    public string? LoadWarning { get; }

    /// <summary>Son yazma başarısız olduysa nedeni; başarılı yazmadan sonra <c>null</c>.</summary>
    public string? SaveWarning { get; private set; }

    /// <summary>Verilen hesabın motor hafızasına bağlı görünüm.</summary>
    public IEngineStateStore ForUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new UserView(this, userId);
    }

    /// <summary>Kayıtlı envanter (hesap kimliğiyle); yoksa <c>null</c>.</summary>
    public StoredInventory? LoadInventory()
    {
        lock (_gate)
        {
            if (_state.UserId is not { } userId || _state.Inventory is not { } cached)
            {
                return null;
            }

            return new StoredInventory(userId, cached.SavedAt, new TwitchInventorySnapshot([.. cached.Claimed], [.. cached.InProgress]));
        }
    }

    public void SaveInventory(string userId, TwitchInventorySnapshot inventory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(inventory);
        lock (_gate)
        {
            Own(userId);
            _state.Inventory = new CachedInventory
            {
                SavedAt = _time.GetUtcNow(),
                Claimed = [.. inventory.Claimed],
                InProgress = [.. inventory.InProgress],
            };
            Flush();
        }
    }

    /// <summary>Her şeyi siler (çıkış yapılınca).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _state = new PersistedState();
            Flush();
        }
    }

    private EngineMemory LoadMemory(string userId)
    {
        lock (_gate)
        {
            return _state.UserId == userId ? _state.Memory : new EngineMemory();
        }
    }

    private void SaveMemory(string userId, EngineMemory memory)
    {
        lock (_gate)
        {
            Own(userId);
            _state.Memory = memory;
            Flush();
        }
    }

    /// <summary>Elle bozulmuş (<c>null</c> alanlı) bir dosya sonraki kullanımda patlamasın.</summary>
    private static PersistedState Normalize(PersistedState state)
    {
        state.Memory ??= new EngineMemory();
        if (state.Memory.CompletedUntil is null || state.Memory.CooldownUntil is null)
        {
            state.Memory = new EngineMemory
            {
                CompletedUntil = state.Memory.CompletedUntil ?? [],
                CooldownUntil = state.Memory.CooldownUntil ?? [],
            };
        }

        if (state.Inventory is { } inventory)
        {
            inventory.Claimed ??= [];
            inventory.InProgress ??= [];
        }

        return state;
    }

    /// <summary>Durum başka bir hesaba aitse sıfırlar.</summary>
    private void Own(string userId)
    {
        if (_state.UserId != userId)
        {
            _state = new PersistedState { UserId = userId };
        }
    }

    private void Flush()
    {
        try
        {
            _store.Save(_state);
            SaveWarning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveWarning = $"Durum kaydedilemedi: {ex.Message}";
            _logger.LogWarning("{Warning}", SaveWarning);
        }
    }

    private sealed class UserView(StateStore owner, string userId) : IEngineStateStore
    {
        public EngineMemory Load() => owner.LoadMemory(userId);

        public void Save(EngineMemory memory) => owner.SaveMemory(userId, memory);
    }
}
