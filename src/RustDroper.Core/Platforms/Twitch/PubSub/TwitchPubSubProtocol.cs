using System.Text.Json;
using RustDroper.Core.Infrastructure;

namespace RustDroper.Core.Platforms.Twitch.PubSub;

public abstract record TwitchPubSubEvent;

/// <summary><c>user-drop-events</c> → <c>drop-progress</c>: sunucu tarafı gerçek zamanlı ilerleme.</summary>
public sealed record DropProgressEvent(string DropId, string ChannelId, int CurrentMinutes, int RequiredMinutes) : TwitchPubSubEvent;

/// <summary><c>user-drop-events</c> → <c>drop-claim</c>: drop claim edilebilir. <see cref="DropInstanceId"/> claim için gereklidir.</summary>
public sealed record DropClaimEvent(string DropId, string ChannelId, string DropInstanceId) : TwitchPubSubEvent;

/// <summary><c>video-playback-by-id</c>: kanal yayını açıldı/kapandı.</summary>
public sealed record StreamStateEvent(string ChannelId, bool IsUp) : TwitchPubSubEvent;

public enum PubSubFrameKind
{
    Unknown,
    Response,
    Pong,
    Reconnect,
    Message,
}

/// <param name="Error">RESPONSE'ta hata metni; başarıda boş.</param>
/// <param name="Event">MESSAGE ise tanınan olay; tanınmayan türlerde <c>null</c>.</param>
/// <param name="MessageType">MESSAGE'ın iç <c>type</c> değeri (tanınmasa da); tanı amaçlı, içerik içermez.</param>
public sealed record PubSubFrame(PubSubFrameKind Kind, string? Nonce, string? Error, string? Topic, TwitchPubSubEvent? Event, string? MessageType = null);

/// <summary>
/// Twitch PubSub çerçeve kurma/ayrıştırma (saf, ağsız). Biçimler referans projeden (TwitchDropsMiner) alınmıştır; canlıda U7 ile doğrulanır.
/// Tanınmayan çerçeve/olaylar hata değil, yok sayılır (Twitch yeni türler ekleyebilir).
/// </summary>
public static class TwitchPubSubProtocol
{
    public const string UserDropEvents = "user-drop-events";
    public const string VideoPlayback = "video-playback-by-id";

    public static string UserDropTopic(string userId) => $"{UserDropEvents}.{userId}";

    public static string PlaybackTopic(string channelId) => $"{VideoPlayback}.{channelId}";

    public static string BuildListen(IEnumerable<string> topics, string authToken, string nonce) =>
        JsonSerializer.Serialize(new
        {
            type = "LISTEN",
            nonce,
            data = new { topics = topics.ToArray(), auth_token = authToken },
        });

    public static string BuildUnlisten(IEnumerable<string> topics, string nonce) =>
        JsonSerializer.Serialize(new { type = "UNLISTEN", nonce, data = new { topics = topics.ToArray() } });

    public static string BuildPing() => """{"type":"PING"}""";

    public static PubSubFrame Parse(string frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            return new PubSubFrame(PubSubFrameKind.Unknown, null, null, null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            switch (root.GetStringOrNull("type"))
            {
                case "PONG":
                    return new PubSubFrame(PubSubFrameKind.Pong, null, null, null, null);
                case "RECONNECT":
                    return new PubSubFrame(PubSubFrameKind.Reconnect, null, null, null, null);
                case "RESPONSE":
                    return new PubSubFrame(PubSubFrameKind.Response, root.GetStringOrNull("nonce"), root.GetStringOrNull("error") ?? string.Empty, null, null);
                case "MESSAGE":
                    return ParseMessage(root);
                default:
                    return new PubSubFrame(PubSubFrameKind.Unknown, null, null, null, null);
            }
        }
    }

    private static PubSubFrame ParseMessage(JsonElement root)
    {
        if (!root.TryGetProp("data", out var data))
        {
            return new PubSubFrame(PubSubFrameKind.Message, null, null, null, null);
        }

        var topic = data.GetStringOrNull("topic");
        var message = data.GetStringOrNull("message");
        TwitchPubSubEvent? parsed = null;
        string? innerType = null;
        if (topic is not null && message is not null)
        {
            try
            {
                using var inner = JsonDocument.Parse(message);
                innerType = inner.RootElement.GetStringOrNull("type");
                parsed = ParseEvent(topic, inner.RootElement);
            }
            catch (JsonException)
            {
                // Bozuk iç mesaj: olay yok sayılır.
            }
        }

        return new PubSubFrame(PubSubFrameKind.Message, null, null, topic, parsed, innerType);
    }

    private static TwitchPubSubEvent? ParseEvent(string topic, JsonElement message)
    {
        var type = message.GetStringOrNull("type");
        if (topic.StartsWith(UserDropEvents + ".", StringComparison.Ordinal) && message.TryGetProp("data", out var data))
        {
            switch (type)
            {
                case "drop-progress":
                    return data.GetStringOrNull("drop_id") is { Length: > 0 } progressDrop
                        ? new DropProgressEvent(
                            progressDrop,
                            data.GetStringOrNull("channel_id") ?? string.Empty,
                            data.GetInt32OrNull("current_progress_min") ?? 0,
                            data.GetInt32OrNull("required_progress_min") ?? 0)
                        : null;
                case "drop-claim":
                    return data.GetStringOrNull("drop_id") is { Length: > 0 } claimDrop
                           && data.GetStringOrNull("drop_instance_id") is { Length: > 0 } instance
                        ? new DropClaimEvent(claimDrop, data.GetStringOrNull("channel_id") ?? string.Empty, instance)
                        : null;
            }
        }

        if (topic.StartsWith(VideoPlayback + ".", StringComparison.Ordinal))
        {
            var channelId = topic[(VideoPlayback.Length + 1)..];
            return type switch
            {
                "stream-up" => new StreamStateEvent(channelId, true),
                "stream-down" => new StreamStateEvent(channelId, false),
                _ => null,
            };
        }

        return null;
    }
}
