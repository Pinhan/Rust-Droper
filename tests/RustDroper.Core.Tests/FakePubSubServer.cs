using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RustDroper.Core.Tests;

/// <summary>Gerçek TCP üzerinde minimal WebSocket sunucusu (PubSub istemcisini ağ katmanıyla birlikte sınamak için).</summary>
internal sealed partial class FakePubSubServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(WebSocket Socket, TcpClient Tcp)> _connections = [];
    private int _connectionCount;

    public FakePubSubServer()
    {
        _listener.Start();
        Endpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = AcceptLoopAsync();
    }

    public Uri Endpoint { get; }

    /// <summary>PING'e otomatik PONG döner (kapatılırsa istemcinin zaman aşımı sınanır).</summary>
    public bool AutoPong { get; set; } = true;

    /// <summary>Gelen her çerçeve için isteğe bağlı cevap üretir.</summary>
    public Func<string, string?>? Responder { get; set; }

    public ConcurrentQueue<string> Received { get; } = new();

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public int CountReceived(string fragment) => Received.Count(f => f.Contains(fragment, StringComparison.Ordinal));

    public async Task SendAsync(string text)
    {
        (WebSocket Socket, TcpClient Tcp) latest;
        lock (_connections)
        {
            latest = _connections[^1];
        }

        await latest.Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cts.Token);
    }

    public void DropAllConnections()
    {
        lock (_connections)
        {
            foreach (var (_, tcp) in _connections)
            {
                tcp.Close();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        DropAllConnections();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[4096];
            while (!header.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                {
                    return;
                }

                header.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var key = KeyPattern().Match(header.ToString()).Groups[1].Value.Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));

            var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
            lock (_connections)
            {
                _connections.Add((socket, client));
            }

            Interlocked.Increment(ref _connectionCount);

            var frame = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(frame, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                var text = Encoding.UTF8.GetString(frame, 0, result.Count);
                Received.Enqueue(text);
                string? reply = null;
                if (text.Contains("\"PING\"", StringComparison.Ordinal) && AutoPong)
                {
                    reply = """{"type":"PONG"}""";
                }
                else if (Responder?.Invoke(text) is { } custom)
                {
                    reply = custom;
                }

                if (reply is not null)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, _cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Bağlantı kapandı: sunucu tarafında beklenen durum.
        }
    }

    [GeneratedRegex("Sec-WebSocket-Key: (.+)\r\n", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();
}
