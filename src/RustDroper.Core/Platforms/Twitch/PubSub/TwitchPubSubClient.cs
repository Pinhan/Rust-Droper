using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Twitch.PubSub;

public sealed record PubSubOptions
{
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMinutes(4);

    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReconnectBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(60);
}

public enum PubSubStatus
{
    Connected,
    Disconnected,

    /// <summary>Sunucu LISTEN isteğini kimlik hatasıyla reddetti (token geçersiz/yetkisiz).</summary>
    AuthFailed,
}

/// <summary>
/// Twitch PubSub WebSocket istemcisi: kopunca üstel geri çekilmeyle yeniden bağlanır ve tüm konuları yeniden dinler,
/// düzenli PING gönderir, PONG gelmezse bağlantıyı yeniler. Olaylar abonelere eşzamanlı iletilir (işleyiciler hızlı olmalı).
/// </summary>
public sealed class TwitchPubSubClient
{
    public static readonly Uri DefaultEndpoint = new("wss://pubsub-edge.twitch.tv/v1");

    private readonly Func<string?> _tokenProvider;
    private readonly Uri _endpoint;
    private readonly PubSubOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _desired = new(StringComparer.Ordinal);
    private readonly List<Action<TwitchPubSubEvent>> _subscribers = [];
    private ClientWebSocket? _socket;
    private long _lastPongTicks;
    private long _lastMessageTicks;

    public TwitchPubSubClient(Func<string?> tokenProvider, Uri? endpoint = null, PubSubOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _endpoint = endpoint ?? DefaultEndpoint;
        _options = options ?? new PubSubOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public event Action<PubSubStatus, string?>? StatusChanged;

    public bool IsConnected { get; private set; }

    /// <summary>Son çerçevenin (PONG dahil) alındığı an; bağlantı canlılık göstergesi.</summary>
    public DateTimeOffset? LastMessageAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastMessageTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public IDisposable Subscribe(Action<TwitchPubSubEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _subscribers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _subscribers.Remove(handler);
            }
        });
    }

    /// <summary>İstenen konu kümesini ayarlar; bağlıysa fark LISTEN/UNLISTEN olarak gönderilir.</summary>
    public async Task SetTopicsAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        string[] added;
        string[] removed;
        lock (_gate)
        {
            var next = new HashSet<string>(topics, StringComparer.Ordinal);
            added = [.. next.Except(_desired)];
            removed = [.. _desired.Except(next)];
            _desired.Clear();
            _desired.UnionWith(next);
        }

        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                if (removed.Length > 0)
                {
                    await SendAsync(socket, TwitchPubSubProtocol.BuildUnlisten(removed, NewNonce()), cancellationToken).ConfigureAwait(false);
                }

                if (added.Length > 0 && _tokenProvider() is { Length: > 0 } token)
                {
                    await SendAsync(socket, TwitchPubSubProtocol.BuildListen(added, token, NewNonce()), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
            {
                // Bağlantı kopmuş/kapanıyor: yeniden bağlanınca istenen konular zaten tamamı dinlenecek.
            }
        }
    }

    /// <summary>İptal edilene kadar bağlı kalmaya çalışır.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = Environment.TickCount64;
            try
            {
                await ConnectAndServeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or TimeoutException or InvalidOperationException)
            {
                _logger.LogWarning("PubSub bağlantısı koptu: {Reason}", ex.Message);
            }
            finally
            {
                SetConnected(false, Loc.T("err.pubsub.closed"));
            }

            // Uzun süre sağlıklı kaldıysa geri çekilmeyi sıfırla.
            failures = Environment.TickCount64 - startedAt > 60_000 ? 1 : failures + 1;
            var delay = TimeSpan.FromSeconds(Math.Min(
                _options.ReconnectMaxDelay.TotalSeconds,
                _options.ReconnectBaseDelay.TotalSeconds * Math.Pow(2, failures - 1)));
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // PING/PONG'u uygulama düzeyinde biz yönetiyoruz.
        await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        _socket = socket;
        Interlocked.Exchange(ref _lastPongTicks, DateTimeOffset.UtcNow.Ticks);
        SetConnected(true, null);

        string[] topics;
        lock (_gate)
        {
            topics = [.. _desired];
        }

        if (topics.Length > 0 && _tokenProvider() is { Length: > 0 } token)
        {
            await SendAsync(socket, TwitchPubSubProtocol.BuildListen(topics, token, NewNonce()), cancellationToken).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveLoopAsync(socket, linked.Token);
        var ping = PingLoopAsync(socket, linked.Token);
        try
        {
            await Task.WhenAny(receive, ping).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            _socket = null;
            await Task.WhenAll(Settle(receive), Settle(ping)).ConfigureAwait(false);
        }

        // Hangi görev biterse bitsin (hata ya da sunucu kapatması) bağlantı yenilenir; hata varsa yukarı taşı.
        await (receive.IsFaulted ? receive : ping.IsFaulted ? ping : Task.CompletedTask).ConfigureAwait(false);
        if (socket.State == WebSocketState.CloseReceived)
        {
            throw new WebSocketException(Loc.T("err.pubsub.serverClosed"));
        }
    }

    private static async Task Settle(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var frame = TwitchPubSubProtocol.Parse(text.ToString());
            text.Clear();
            Interlocked.Exchange(ref _lastMessageTicks, DateTimeOffset.UtcNow.Ticks);
            Handle(frame);
        }
    }

    private void Handle(PubSubFrame frame)
    {
        switch (frame.Kind)
        {
            case PubSubFrameKind.Pong:
                Interlocked.Exchange(ref _lastPongTicks, DateTimeOffset.UtcNow.Ticks);
                break;
            case PubSubFrameKind.Reconnect:
                // Sunucu yeniden bağlanmamızı istiyor: bağlantıyı kapatıp döngüde yenileriz.
                _socket?.Abort();
                break;
            case PubSubFrameKind.Response when !string.IsNullOrEmpty(frame.Error):
                _logger.LogWarning("PubSub LISTEN hatası: {Error}", frame.Error);
                if (frame.Error.Contains("BADAUTH", StringComparison.OrdinalIgnoreCase))
                {
                    StatusChanged?.Invoke(PubSubStatus.AuthFailed, frame.Error);
                }

                break;
            case PubSubFrameKind.Message when frame.Event is null:
                // Tanınmayan mesaj: yalnızca konu/tür günlüğe yazılır (içerik değil). Drop olaylarında bilgi düzeyi, gürültülü
                // yayın olaylarında (viewcount...) hata ayıklama düzeyi.
                _logger.Log(
                    frame.Topic?.StartsWith(TwitchPubSubProtocol.UserDropEvents, StringComparison.Ordinal) == true ? LogLevel.Information : LogLevel.Debug,
                    "PubSub tanınmayan mesaj: {Topic} / {Type}",
                    frame.Topic,
                    frame.MessageType);
                break;
            case PubSubFrameKind.Message when frame.Event is { } pubSubEvent:
                Action<TwitchPubSubEvent>[] handlers;
                lock (_gate)
                {
                    handlers = [.. _subscribers];
                }

                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(pubSubEvent);
                    }
                    catch (Exception ex)
                    {
                        // Bir abonenin hatası bağlantıyı veya diğer aboneleri düşürmemeli.
                        _logger.LogError(ex, "PubSub olay işleyicisi hata verdi");
                    }
                }

                break;
        }
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(_options.PingInterval, cancellationToken).ConfigureAwait(false);
            var sentAt = DateTimeOffset.UtcNow.Ticks;
            await SendAsync(socket, TwitchPubSubProtocol.BuildPing(), cancellationToken).ConfigureAwait(false);
            await Task.Delay(_options.PongTimeout, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Read(ref _lastPongTicks) < sentAt)
            {
                throw new TimeoutException(Loc.T("err.pubsub.pong"));
            }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetConnected(bool connected, string? reason)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        StatusChanged?.Invoke(connected ? PubSubStatus.Connected : PubSubStatus.Disconnected, reason);
    }

    private static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
