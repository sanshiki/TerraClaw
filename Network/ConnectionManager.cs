using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TerraClaw.Network;

public class BridgeConnection : IDisposable
{
    public string Id { get; }
    public WebSocket Socket { get; }
    public bool IsAuthenticated { get; set; }
    public bool IsOpen => Socket.State == WebSocketState.Open;
    public ConcurrentQueue<string> OutgoingQueue { get; } = new();
    public HashSet<string> SubscribedEventTypes { get; } = new();
    public DateTime ConnectedAt { get; }
    public DateTime LastMessageAt { get; set; }

    private readonly Core.BridgeConfig _config;
    private long _messageSequence;

    public BridgeConnection(WebSocket socket, Core.BridgeConfig config)
    {
        Id = Guid.NewGuid().ToString();
        Socket = socket;
        _config = config;
        ConnectedAt = DateTime.UtcNow;
        LastMessageAt = DateTime.UtcNow;
    }

    public async Task RunHandshake()
    {
        var buffer = new byte[4096];
        var result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        if (result.MessageType == WebSocketMessageType.Close)
            throw new WebSocketException("Client closed before handshake");

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var msgType = root.GetProperty("type").GetString();
        if (msgType != "handshake")
            throw new InvalidOperationException($"Expected handshake, got {msgType}");

        var payload = root.GetProperty("payload");
        var version = payload.GetProperty("version").GetString();
        var auth = payload.GetProperty("auth").GetString();

        if (auth != _config.SharedSecret)
        {
            var error = MessageSerializer.BuildMessage("handshake_error", new
            {
                reason = "AUTH_FAILED",
                message = "Invalid shared secret"
            });
            await Socket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(error)),
                WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            throw new InvalidOperationException("Authentication failed");
        }

        var response = MessageSerializer.BuildMessage("handshake_ok", new
        {
            session_id = Id,
            server_version = "1.0",
            server_capabilities = new[] { "observations", "actions", "events", "skills" }
        }, sessionId: Id);
        await Socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(response)),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public long NextSequence() => Interlocked.Increment(ref _messageSequence);

    public void SendError(string code, string message, object? details = null)
    {
        var envelope = MessageSerializer.BuildMessage("error", new
        {
            error_code = code,
            message,
            details
        }, sessionId: Id);

        OutgoingQueue.Enqueue(envelope);
    }

    public void SendPong()
    {
        var pong = MessageSerializer.BuildMessage("pong", new
        {
            client_timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, sessionId: Id);
        OutgoingQueue.Enqueue(pong);
        LastMessageAt = DateTime.UtcNow;
    }

    public void Dispose()
    {
        if (Socket.State == WebSocketState.Open)
        {
            try
            {
                Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None)
                      .Wait(1000);
            }
            catch { }
        }
        Socket.Dispose();
    }
}
