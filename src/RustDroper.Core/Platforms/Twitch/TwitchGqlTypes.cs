using System.Text.Json;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Twitch;

/// <summary>Bir persisted-query işlemi: ad + bilinen varsayılan sha256 (Twitch bunu zaman zaman değiştirir).</summary>
public sealed record GqlOperation(string Name, string DefaultHash);

/// <summary>
/// Bilinen işlemler. Hash değerleri 2026-10-15'te referans projeden (TwitchDropsMiner) alındı; canlıda doğrulanır.
/// Bozulurlarsa <see cref="GqlHashRegistry"/> üzerinden <c>hashes.json</c> ile değiştirilebilir (kod değişmeden).
/// </summary>
public static class GqlOperations
{
    public static readonly GqlOperation Inventory = new("Inventory", "8337eb8541b314040b0edde0c09c5c7a2783ba1960aa9edfbf3bac16d0fec404");

    public static readonly GqlOperation ViewerDropsDashboard = new("ViewerDropsDashboard", "c16bb890cc8ce7647a96ee69cd313d423a378a3dedadf630a1017cde18975feb");

    public static readonly GqlOperation DropCampaignDetails = new("DropCampaignDetails", "039277bf98f3130929262cc7c6efd9c141ca3749cb6dca442fc8ead9a53f77c1");

    public static readonly GqlOperation ClaimDrop = new("DropsPage_ClaimDropRewards", "a455deea71bdc9015b78eb49f4acfbce8baa7ccbedd28e549bb025bd0f751930");

    public static readonly GqlOperation CurrentDrop = new("DropCurrentSessionContext", "4d06b702d25d652afb9ef835d2a550031f1cf762b193523a92166f40ea3d142b");

    public static readonly GqlOperation GameDirectory = new("DirectoryPage_Game", "86bcceb4e8b1a51256ff8eed8bd8aae4acacf80d737efe904f84f3aeadf8cafd");

    public static readonly GqlOperation StreamInfo = new("VideoPlayerStreamInfoOverlayChannel", "198492e0857f6aedead9665c81c5a06d67b25b58034649687124083ff288597d");

    public static readonly GqlOperation AvailableDrops = new("DropsHighlightService_AvailableDrops", "782dad0f032942260171d2d80a654f88bdd0c5a9dddc392e9bc92218a0f42d20");

    public static readonly GqlOperation PlaybackAccessToken = new("PlaybackAccessToken", "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9");

    public static readonly GqlOperation SlugRedirect = new("DirectoryGameRedirect", "1f0300090caceec51f33c5e20647aceff9017f740f223c3c532ba6fa59f6b6cc");
}

/// <summary>Varsayılan hash'ler + kullanıcının/otomatik keşfin yazdığı <c>hashes.json</c> geçersiz kılmaları.</summary>
public sealed class GqlHashRegistry
{
    private sealed class Overrides
    {
        public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly AtomicJsonStore<Overrides>? _store;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _overrides;

    /// <summary>Yalnızca varsayılanlarla (bellek içi; test/geliştirme).</summary>
    public GqlHashRegistry()
    {
        _overrides = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Geçersiz kılmaları <paramref name="path"/> dosyasından yükler ve değişince yazar.</summary>
    public GqlHashRegistry(string path)
    {
        _store = new AtomicJsonStore<Overrides>(path, schemaVersion: 1);
        _overrides = new Dictionary<string, string>(_store.Load().Value.Hashes, StringComparer.Ordinal);
    }

    public string GetHash(GqlOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            return _overrides.GetValueOrDefault(operation.Name) ?? operation.DefaultHash;
        }
    }

    public void SetOverride(GqlOperation operation, string hash)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        lock (_gate)
        {
            _overrides[operation.Name] = hash;
            _store?.Save(new Overrides { Hashes = new Dictionary<string, string>(_overrides, StringComparer.Ordinal) });
        }
    }
}

/// <summary>Persisted-query hash'i Twitch tarafından tanınmıyor: hash kayması (iyileştirilebilir, çökme değil).</summary>
public sealed class TwitchHashException(string operationName)
    : PlatformDataException(Loc.T("err.twitch.gqlHash", operationName))
{
    public string OperationName { get; } = operationName;
}

/// <summary>Twitch istemci bütünlüğü (integrity) kontrolünü reddetti: bu istemci profiliyle bu işlem yapılamıyor [U2].</summary>
public sealed class TwitchIntegrityException(string operationName)
    : PlatformDataException(Loc.T("err.twitch.gqlIntegrity", operationName))
{
    public string OperationName { get; } = operationName;
}

/// <summary>Twitch GQL tarafında geçici sunucu hatası (service timeout/unavailable...). Yeniden denenir.</summary>
public sealed class TwitchTransientException(string message) : Exception(message);

/// <summary>Token reddedildi (401 / "unauthenticated"). Oturum yenilenmeli ya da yeniden giriş gerekir.</summary>
public sealed class TwitchUnauthorizedException(string message) : PlatformAuthException(message);

public static class GqlResponse
{
    private static readonly string[] TransientMarkers =
    [
        "service timeout", "service unavailable", "service error", "internal server error", "context deadline exceeded",
    ];

    /// <summary>
    /// Yanıtın <c>errors</c> dizisini sınıflar ve uygun istisnayı fırlatır; ölümcül hata yoksa <c>data</c> öğesini ve
    /// kısmi hata mesajlarını döndürür. Mesajlar yalnızca platformun hata metnini içerir (token/değişken içermez).
    /// </summary>
    public static (JsonElement Data, IReadOnlyList<string> PartialErrors) Evaluate(JsonElement root, string operationName)
    {
        IReadOnlyList<string> partial = [];
        if (root.TryGetProp("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => FormatError(e))
                .ToList();
            var all = string.Join(" | ", messages);
            var lower = all.ToLowerInvariant();

            if (lower.Contains("persistedquerynotfound", StringComparison.Ordinal))
            {
                throw new TwitchHashException(operationName);
            }

            if (lower.Contains("integrity", StringComparison.Ordinal))
            {
                throw new TwitchIntegrityException(operationName);
            }

            if (lower.Contains("unauthenticated", StringComparison.Ordinal) || lower.Contains("unauthorized", StringComparison.Ordinal))
            {
                throw new TwitchUnauthorizedException(all);
            }

            if (TransientMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            {
                throw new TwitchTransientException($"'{operationName}': {all}");
            }

            // Kısmi hata (data de var) tolere edilir; veri yoksa şema/yetki sorunudur.
            if (!root.TryGetProp("data", out _))
            {
                throw new PlatformDataException(Loc.T("err.twitch.gqlError", operationName, all));
            }

            partial = messages;
        }

        return root.TryGetProp("data", out var data)
            ? (data, partial)
            : throw new PlatformDataException(Loc.T("err.twitch.gqlNoData", operationName));
    }

    // "mesaj (path: a.b.c)" — hangi alanın boşaldığını görebilmek için.
    private static string FormatError(JsonElement error)
    {
        var message = error.GetStringOrNull("message") ?? string.Empty;
        if (error.TryGetProp("path", out var path) && path.ValueKind == JsonValueKind.Array)
        {
            message += " (path: " + string.Join('.', path.EnumerateArray().Select(p => p.ToString())) + ")";
        }

        return message;
    }
}
