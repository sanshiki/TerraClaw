using System;
namespace TerraClaw.Core;

public class BridgeConfig
{
    public string ListenHost { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 9777;
    public string SharedSecret { get; set; } = "terraclaw-dev";
    public int ObservationSendRateHz { get; set; } = 10;
    public int SpatialWindowRadius { get; set; } = 30;
    public int HeartbeatIntervalMs { get; set; } = 2000;
    public int ConnectionTimeoutMs { get; set; } = 10000;
    public int MaxActionQueueSize { get; set; } = 50;
    public int MaxMessageSizeBytes { get; set; } = 1_048_576; // 1 MB
    public int TickSyncIntervalTicks { get; set; } = 6; // Send observations every N game ticks
    public bool EnableReplayRecording { get; set; } = true;
    public bool Verbose { get; set; } = false;

    public static BridgeConfig Default => new();
}
