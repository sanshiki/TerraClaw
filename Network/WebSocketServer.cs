using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TerraClaw.Network;

public class WebSocketServer : IDisposable
{
    private readonly Core.BridgeConfig _config;
    private readonly Observation.ObservationCollector _collector;
    private readonly Event.EventBus _eventBus;
    private readonly ConcurrentDictionary<string, BridgeConnection> _connections = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public bool IsRunning { get; private set; }
    public IEnumerable<BridgeConnection> Connections => _connections.Values;
    public event Action<string> OnClientDisconnected;

    public WebSocketServer(
        Core.BridgeConfig config,
        Observation.ObservationCollector collector,
        Event.EventBus eventBus)
    {
        _config = config;
        _collector = collector;
        _eventBus = eventBus;
    }

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{_config.ListenHost}:{_config.ListenPort}/");
        _listener.Start();
        IsRunning = true;
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));

        Terraria.ModLoader.ModContent.GetInstance<TerraClaw>()
            .Logger.Info($"[TerraClaw] WebSocket server listening on ws://{_config.ListenHost}:{_config.ListenPort}/bridge");
    }

    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();
        _listener?.Stop();

        foreach (var conn in _connections.Values)
            conn.Dispose();
        _connections.Clear();
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync().WaitAsync(ct);
                if (context.Request.IsWebSocketRequest)
                    _ = HandleConnection(context, ct);
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                Terraria.ModLoader.ModContent.GetInstance<TerraClaw>()
                    .Logger.Error($"[TerraClaw] Listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleConnection(HttpListenerContext context, CancellationToken ct)
    {
        WebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        }
        catch (Exception ex)
        {
            Terraria.ModLoader.ModContent.GetInstance<TerraClaw>()
                .Logger.Error($"[TerraClaw] WebSocket accept error: {ex.Message}");
            return;
        }

        var conn = new BridgeConnection(wsContext.WebSocket, _config);
        _connections.TryAdd(conn.Id, conn);

        try
        {
            await conn.RunHandshake();
            conn.IsAuthenticated = true;

            // Subscribe to default events
            var subscribedTypes = conn.SubscribedEventTypes;
            foreach (var eventType in _eventBus.GetAllEventTypes())
                subscribedTypes.Add(eventType);

            _ = Task.Run(() => SendLoop(conn, ct), ct);
            await ReceiveLoop(conn, ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Terraria.ModLoader.ModContent.GetInstance<TerraClaw>()
                .Logger.Info($"[TerraClaw] Client disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            Terraria.ModLoader.ModContent.GetInstance<TerraClaw>()
                .Logger.Error($"[TerraClaw] Connection error: {ex}");
        }
        finally
        {
            _connections.TryRemove(conn.Id, out _);
            OnClientDisconnected?.Invoke(conn.Id);
            conn.Dispose();
        }
    }

    private async Task SendLoop(BridgeConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && conn.IsOpen)
        {
            try
            {
                if (conn.OutgoingQueue.TryDequeue(out var message))
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await conn.Socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct);
                }
                else
                {
                    await Task.Delay(1, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
        }
    }

    private async Task ReceiveLoop(BridgeConnection conn, CancellationToken ct)
    {
        var buffer = new byte[_config.MaxMessageSizeBytes];
        var messageBuffer = new List<byte>();

        while (!ct.IsCancellationRequested && conn.IsOpen)
        {
            var result = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                break;
            }

            messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                messageBuffer.Clear();
                ProcessIncomingMessage(conn, json);
            }
        }
    }

    private void ProcessIncomingMessage(BridgeConnection conn, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                conn.SendError("PROTOCOL_ERROR", "Missing 'type' field");
                return;
            }

            var msgType = typeProp.GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p : default;

            switch (msgType)
            {
                case "ping":
                    conn.SendPong();
                    break;

                case "event.subscribe":
                    if (payload.TryGetProperty("event_types", out var eventTypes) && eventTypes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var et in eventTypes.EnumerateArray())
                            conn.SubscribedEventTypes.Add(et.GetString()!);
                    }
                    break;

                case "event.unsubscribe":
                    if (payload.TryGetProperty("event_types", out var unsubTypes) && unsubTypes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var et in unsubTypes.EnumerateArray())
                            conn.SubscribedEventTypes.Remove(et.GetString()!);
                    }
                    break;

                case "skill.execute":
                    break;

                case "skill.cancel":
                    break;

                case "agent.register":
                    Core.BridgeModSystem.Instance?.HandleAgentRegister(conn, payload);
                    break;

                case "agent.action":
                    Core.BridgeModSystem.Instance?.HandleAgentAction(conn, payload);
                    break;

                case "agent.action.cancel":
                    Core.BridgeModSystem.Instance?.HandleAgentActionCancel(conn, payload);
                    break;

                case "agent.unregister":
                    Core.BridgeModSystem.Instance?.HandleAgentUnregister(conn, payload);
                    break;

                default:
                    conn.SendError("PROTOCOL_ERROR", $"Unknown message type: {msgType}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            conn.SendError("PROTOCOL_ERROR", $"Invalid JSON: {ex.Message}");
        }
    }

    public void Broadcast(string message)
    {
        foreach (var conn in _connections.Values)
        {
            if (conn.IsAuthenticated && conn.IsOpen)
                conn.OutgoingQueue.Enqueue(message);
        }
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
    }
}
