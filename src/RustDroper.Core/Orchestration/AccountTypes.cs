using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Orchestration;

public enum AccountStatus
{
    /// <summary>Henüz kontrol edilmedi.</summary>
    Unknown,

    /// <summary>Kayıtlı oturum doğrulanıyor.</summary>
    Restoring,

    LoggedOut,

    /// <summary>Cihaz-kodu girişi sürüyor (kullanıcı kodu onaylayacak).</summary>
    LoggingIn,

    LoggedIn,

    /// <summary>Kayıtlı oturum geçersiz/iptal ve yenilenemedi: kullanıcı yeniden giriş yapmalı.</summary>
    NeedsLogin,

    /// <summary>Ağ/sunucu sorunu: oturum durumu bilinmiyor, token'lar korunuyor.</summary>
    Offline,
}

/// <summary>Hesap durumu (platformdan bağımsız arayüz görünümü). Token ASLA burada taşınmaz.</summary>
public sealed record AccountSnapshot(AccountStatus Status, string? Login = null, TwitchDeviceCode? DeviceCode = null, string? Error = null)
{
    public static AccountSnapshot Initial { get; } = new(AccountStatus.Unknown);
}
