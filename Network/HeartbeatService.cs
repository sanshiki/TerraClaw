using System;
using System.Threading;
using System.Threading.Tasks;

namespace TerraClaw.Network;

public class HeartbeatService : IDisposable
{
    private readonly WebSocketServer _server;
    private readonly Core.BridgeConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public HeartbeatService(WebSocketServer server, Core.BridgeConfig config)
    {
        _server = server;
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_config.HeartbeatIntervalMs, ct);

            foreach (var conn in _server.Connections)
            {
                if (!conn.IsOpen) continue;

                var elapsed = DateTime.UtcNow - conn.LastMessageAt;
                if (elapsed.TotalMilliseconds > _config.ConnectionTimeoutMs)
                {
                    conn.Dispose();
                    continue;
                }
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
