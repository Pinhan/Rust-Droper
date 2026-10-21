using System.Text.Json;
using RustDroper.Core.Infrastructure;

namespace RustDroper.Core.Platforms.Twitch;

/// <summary>Sunucunun şu an izlediğimiz kanal için tuttuğu drop oturumu (<c>DropCurrentSessionContext</c>).</summary>
public sealed record TwitchCurrentSession(string? DropId, int CurrentMinutes, int RequiredMinutes);

public static class TwitchCurrentSessionParser
{
    public static IReadOnlyDictionary<string, object?> Variables(string channelId) =>
        new Dictionary<string, object?> { ["channelID"] = channelId, ["channelLogin"] = string.Empty };

    /// <summary>Aktif drop oturumu yoksa (<c>dropCurrentSession: null</c>) <c>null</c> döner.</summary>
    public static TwitchCurrentSession? Parse(JsonElement data)
    {
        if (!data.TryGetProp("currentUser", out var user) || !user.TryGetProp("dropCurrentSession", out var session))
        {
            return null;
        }

        return new TwitchCurrentSession(
            session.GetStringOrNull("dropID"),
            session.GetInt32OrNull("currentMinutesWatched") ?? 0,
            session.GetInt32OrNull("requiredMinutesWatched") ?? 0);
    }
}
