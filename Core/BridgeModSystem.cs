using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using TerraClaw.Agents.TerraClaw;
using TerraClaw.Event;
using TerraClaw.Network;
using TerraClaw.Observation;

namespace TerraClaw.Core;

/// <summary>
/// Main bridge mod system.
/// Owns the WebSocket server, observation collector, event bus, and /agent instruction delivery.
/// </summary>
public class BridgeModSystem : ModSystem
{
    /// <summary>Current loaded bridge system instance.</summary>
    public static BridgeModSystem Instance { get; private set; } = null!;

    /// <summary>WebSocket server used by the Python runtime and dashboard bridge.</summary>
    public WebSocketServer WebSocketServer { get; private set; } = null!;

    /// <summary>Observation collector retained for legacy observation and dashboard paths.</summary>
    public ObservationCollector Collector { get; private set; } = null!;

    /// <summary>Global event bus used by bridge observation/event messages.</summary>
    public EventBus EventBus { get; private set; } = null!;

    /// <summary>Bridge runtime configuration.</summary>
    public BridgeConfig Config { get; private set; } = null!;

    private int _tickCounter;

    public override void Load()
    {
        Instance = this;
        Config = BridgeConfig.Default;
        EventBus = new EventBus();
        Collector = new ObservationCollector(Config, EventBus);
        WebSocketServer = new WebSocketServer(Config, Collector, EventBus);
        _tickCounter = 0;
    }

    public override void PostUpdateEverything()
    {
        _tickCounter++;

        if (_tickCounter == 1)
            WebSocketServer.Start();

        EventBus.Flush();
    }

    /// <summary>Delivers /agent chat text to all active example TerraClaw agents.</summary>
    public int DeliverPlayerInstruction(string playerName, string instruction)
    {
        return ExampleTerraClawAgent.DeliverPlayerInstruction(playerName, instruction);
    }

    public override void Unload()
    {
        WebSocketServer?.Stop();
        WebSocketServer?.Dispose();
        Collector = null!;
        EventBus = null!;
        Config = null!;
        Instance = null!;
    }

    /// <summary>Deprecated legacy runtime registration endpoint.</summary>
    public void HandleAgentRegister(BridgeConnection conn, JsonElement payload)
    {
        conn.SendError("AGENT_DEPRECATED", "agent.register is deprecated. Spawn Agents/TerraClaw/ExampleTerraClawAgent with AgentSpawner or a ModNPC call.");
    }

    /// <summary>Deprecated legacy runtime action endpoint.</summary>
    public void HandleAgentAction(BridgeConnection conn, JsonElement payload)
    {
        conn.SendError("AGENT_DEPRECATED", "agent.action is deprecated. Implement behavior directly in a ModNPC AI method or use LlmBridgeSystem.Request.");
    }

    /// <summary>Deprecated legacy observation configuration endpoint.</summary>
    public void HandleObservationConfigure(BridgeConnection conn, JsonElement payload)
    {
        conn.SendError("AGENT_DEPRECATED", "observation.configure for legacy agents is deprecated.");
    }

    /// <summary>Deprecated legacy action cancellation endpoint.</summary>
    public void HandleAgentActionCancel(BridgeConnection conn, JsonElement payload)
    {
        conn.SendError("AGENT_DEPRECATED", "agent.action.cancel is deprecated.");
    }

    /// <summary>Deprecated legacy unregister endpoint.</summary>
    public void HandleAgentUnregister(BridgeConnection conn, JsonElement payload)
    {
        conn.SendError("AGENT_DEPRECATED", "agent.unregister is deprecated.");
    }
}
