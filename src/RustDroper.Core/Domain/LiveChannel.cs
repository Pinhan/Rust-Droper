namespace RustDroper.Core.Domain;

/// <summary>Şu an canlı yayında olan, izlenebilir bir kanal.</summary>
public sealed record LiveChannel(
    Platform Platform,
    string Id,
    string Login,
    string DisplayName,
    int Viewers,
    string? Title,
    Uri Url,
    Uri? AvatarUrl);
