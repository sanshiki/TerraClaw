using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using TerraClaw.Agents.TerraClaw;
using TerraClaw.AI;
using TerraClaw.Event;
using TerraClaw.Network;
using TerraClaw.Observation;

namespace TerraClaw.Core;

/// <summary>
/// Main bridge mod system.
/// Owns the WebSocket server, legacy bridge agents, observation collector, and /agent instruction delivery.
/// </summary>
public class BridgeModSystem : ModSystem
{
    /// <summary>Current loaded bridge system instance.</summary>
    public static BridgeModSystem Instance { get; private set; } = null!;

    /// <summary>WebSocket server used by the Python runtime and dashboard bridge.</summary>
    public WebSocketServer WebSocketServer { get; private set; } = null!;

    /// <summary>Legacy observation collector used by Python-driven bridge agents.</summary>
    public ObservationCollector Collector { get; private set; } = null!;

    /// <summary>Global event bus used by legacy observation/event messages.</summary>
    public EventBus EventBus { get; private set; } = null!;

    /// <summary>Bridge runtime configuration.</summary>
    public BridgeConfig Config { get; private set; } = null!;

    private readonly Dictionary<string, TerraClawAgent> _agents = new();
    private readonly Dictionary<string, string> _connectionToAgentId = new();
    private int _tickCounter;

    /// <summary>Optional spawn position consumed by legacy agent.register requests.</summary>
    public static Vector2? PendingSpawnPosition { get; set; }

    // Pending agent registrations queued from WebSocket thread, processed on main thread
    private readonly ConcurrentQueue<(BridgeConnection conn, float x, float y, string agentName, int observationRadius)> _pendingRegistrations = new();

    public override void Load()
    {
        Instance = this;
        Config = BridgeConfig.Default;
        EventBus = new EventBus();
        Collector = new ObservationCollector(Config, EventBus);
        WebSocketServer = new WebSocketServer(Config, Collector, EventBus);
        WebSocketServer.OnClientDisconnected += OnConnectionLost;
        _tickCounter = 0;
    }

    public override void PostUpdateEverything()
    {
        _tickCounter++;

        ProcessPendingRegistrations();

        if (_tickCounter == 1)
        {
            WebSocketServer.Start();
        }

        if (_tickCounter % Config.TickSyncIntervalTicks == 0)
        {
            SendAgentObservations();
        }

        EventBus.Flush();
    }

    private void ProcessPendingRegistrations()
    {
        while (_pendingRegistrations.TryDequeue(out var entry))
        {
            DoAgentRegister(entry.conn, entry.x, entry.y, entry.agentName, entry.observationRadius);
        }
    }

    private void DoAgentRegister(BridgeConnection conn, float x, float y, string agentName = "terraclaw", int observationRadius = 10)
    {
        if (_connectionToAgentId.TryGetValue(conn.Id, out var existingId))
            UnregisterAgent(existingId);

        var agentId = Guid.NewGuid().ToString();
        var agent = CreateAgent(agentId, conn.Id, agentName);

        var source = new Terraria.DataStructures.EntitySource_SpawnNPC();
        int npcIndex = AI.TerraClawAgentNPC.Spawn(new Vector2(x, y), source, agent, agentName);

        if (npcIndex < 0)
        {
            conn.SendError("AGENT_ERROR", "Failed to spawn agent NPC");
            return;
        }

        _agents[agentId] = agent;
        if (agent is BridgeAgent bridgeAgent)
            bridgeAgent.ObservationRadius = observationRadius;
        _connectionToAgentId[conn.Id] = agentId;

        var npc = Main.npc[npcIndex];
        Main.NewText($"[AI] Agent spawned at ({npc.Center.X:F0}, {npc.Center.Y:F0})", 150, 255, 100);
        var response = MessageSerializer.BuildMessage("agent.registered", new
        {
            agent_id = agentId,
            entity_index = npcIndex,
            position = new { x = npc.Center.X, y = npc.Center.Y },
        }, sessionId: conn.Id);
        conn.OutgoingQueue.Enqueue(response);
    }

    private static TerraClawAgent CreateAgent(string agentId, string connectionId, string agentName)
    {
        return agentName switch
        {
            "terraclaw" => new ExampleTerraClawAgent(agentId, connectionId, agentName),
            _ => new BridgeAgent(agentId, connectionId, agentName),
        };
    }

    /// <summary>
    /// Delivers /agent chat text to all active in-game agents that implement <see cref="IPlayerInstructionReceiver"/>.
    /// </summary>
    public int DeliverPlayerInstruction(string playerName, string instruction)
    {
        int delivered = 0;
        foreach (var agent in _agents.Values)
        {
            if (!agent.IsActive || agent is not IPlayerInstructionReceiver receiver)
                continue;

            receiver.ReceivePlayerInstruction(playerName, instruction);
            delivered++;
        }

        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var npc = Main.npc[i];
            if (npc == null || !npc.active || npc.ModNPC is not TerraClawAgentNPC agentNpc)
                continue;

            var agent = agentNpc.Agent;
            if (agent == null || _agents.ContainsValue(agent)
                || !agent.IsActive || agent is not IPlayerInstructionReceiver receiver)
                continue;

            receiver.ReceivePlayerInstruction(playerName, instruction);
            delivered++;
        }
        return delivered;
    }

    public override void Unload()
    {
        if (WebSocketServer != null)
            WebSocketServer.OnClientDisconnected -= OnConnectionLost;

        foreach (var agent in _agents.Values)
        {
            agent.IsActive = false;
            if (agent.NPC != null && agent.NPC.active)
                agent.NPC.active = false;
        }
        _agents.Clear();
        _connectionToAgentId.Clear();

        WebSocketServer?.Stop();
        WebSocketServer?.Dispose();
        Collector = null!;
        EventBus = null!;
        Config = null!;
        Instance = null!;
    }

    // ── Agent observation ──────────────────────────────────────

    private void SendAgentObservations()
    {
        foreach (var kvp in _agents)
        {
            var agent = kvp.Value;
            var npc = agent.NPC;
            if (npc == null || !npc.active || npc.whoAmI < 0 || npc.whoAmI >= Main.maxNPCs)
                continue;

            if (agent is not BridgeAgent bridgeAgent)
                continue;

            var effectiveRadius = bridgeAgent.GetEffectiveObservationRadius();
            var json = Collector.BuildAgentObservation(npc, bridgeAgent.AgentId, _tickCounter, effectiveRadius);
            var conn = WebSocketServer.Connections.FirstOrDefault(c => c.Id == bridgeAgent.ConnectionId);
            conn?.OutgoingQueue.Enqueue(json);
        }
    }

    // ── Agent message handlers ─────────────────────────────────

    /// <summary>Handles a legacy runtime request to spawn and bind a bridge agent.</summary>
    public void HandleAgentRegister(BridgeConnection conn, JsonElement payload)
    {
        float x = 0, y = 0;
        string agentName = "terraclaw";
        int observationRadius = 10;

        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("agent_name", out var an))
                agentName = an.GetString() ?? "terraclaw";
            if (payload.TryGetProperty("observation_radius", out var or))
                observationRadius = or.GetInt32();
        }

        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("position", out var pos))
        {
            if (pos.TryGetProperty("x", out var px)) x = (float)px.GetDouble();
            if (pos.TryGetProperty("y", out var py)) y = (float)py.GetDouble();
        }
        else if (PendingSpawnPosition.HasValue)
        {
            x = PendingSpawnPosition.Value.X;
            y = PendingSpawnPosition.Value.Y;
            PendingSpawnPosition = null;
        }
        else
        {
            var player = Main.LocalPlayer;
            if (player != null) { x = player.Center.X; y = player.Center.Y - 32; }
        }

        // Queue registration to run on main thread (NPC.NewNPC must be main thread)
        _pendingRegistrations.Enqueue((conn, x, y, agentName, observationRadius));
    }

    /// <summary>Handles a legacy runtime action message for a bridge agent.</summary>
    public void HandleAgentAction(BridgeConnection conn, JsonElement payload)
    {
        string agentId;
        if (payload.TryGetProperty("agent_id", out var aid))
            agentId = aid.GetString();
        else if (_connectionToAgentId.TryGetValue(conn.Id, out var cid))
            agentId = cid;
        else
        {
            conn.SendError("AGENT_ERROR", "No agent bound to this connection");
            return;
        }

        if (!_agents.TryGetValue(agentId, out var agent))
        {
            conn.SendError("AGENT_ERROR", $"Agent {agentId} not found");
            return;
        }

        if (agent is not BridgeAgent bridgeAgent)
        {
            conn.SendError("AGENT_ERROR", "Agent does not support legacy agent.action messages");
            return;
        }

        if (bridgeAgent.ConnectionId != conn.Id)
        {
            conn.SendError("AGENT_ERROR", "Agent belongs to a different connection");
            return;
        }

        var action = new PendingAgentAction();
        if (payload.TryGetProperty("action_id", out var actionId))
            action.ActionId = actionId.GetString();
        if (payload.TryGetProperty("action_type", out var actionType))
            action.ActionType = actionType.GetString();
        if (payload.TryGetProperty("timeout_ms", out var timeout))
            action.TimeoutMs = timeout.GetInt32();
        if (payload.TryGetProperty("params", out var actionParams) && actionParams.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in actionParams.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    action.Params[prop.Name] = prop.Value.GetDouble();
                else if (prop.Value.ValueKind == JsonValueKind.String)
                    action.StringParams[prop.Name] = prop.Value.GetString();
            }
        }

        var paramSummary = string.Join(", ", action.Params.Select(kv => $"{kv.Key}={kv.Value}"));
        Main.NewText($"[AI] Action: {action.ActionType} ({paramSummary})", 100, 200, 255);

        if (Config.Verbose)
        {
            int tid = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Main.NewText($"[AI] Enqueue on T{tid}: {action.ActionId[..8]}... {action.ActionType}", 200, 200, 50);
        }

        bridgeAgent.EnqueueAction(action);

        var ack = MessageSerializer.BuildMessage("agent.action.queued", new
        {
            agent_id = agentId,
            action_id = action.ActionId,
            action_type = action.ActionType,
        }, sessionId: conn.Id);
        conn.OutgoingQueue.Enqueue(ack);
    }

    /// <summary>Handles legacy observation radius configuration for a bridge agent.</summary>
    public void HandleObservationConfigure(BridgeConnection conn, JsonElement payload)
    {
        if (!_connectionToAgentId.TryGetValue(conn.Id, out var agentId)
            || !_agents.TryGetValue(agentId, out var agent))
            return;

        if (agent is BridgeAgent bridgeAgent && payload.TryGetProperty("spatial_radius", out var radius))
            bridgeAgent.ObservationRadius = radius.GetInt32();
    }

    /// <summary>Cancels all queued legacy bridge actions for the selected agent.</summary>
    public void HandleAgentActionCancel(BridgeConnection conn, JsonElement payload)
    {
        string agentId;
        if (payload.TryGetProperty("agent_id", out var aid))
            agentId = aid.GetString();
        else if (_connectionToAgentId.TryGetValue(conn.Id, out var cid))
            agentId = cid;
        else return;

        if (_agents.TryGetValue(agentId, out var agent) && agent is BridgeAgent bridgeAgent)
            bridgeAgent.ClearQueue();
    }

    /// <summary>Handles a legacy runtime request to remove a bridge agent.</summary>
    public void HandleAgentUnregister(BridgeConnection conn, JsonElement payload)
    {
        string agentId;
        if (payload.TryGetProperty("agent_id", out var aid))
            agentId = aid.GetString();
        else if (_connectionToAgentId.TryGetValue(conn.Id, out var cid))
            agentId = cid;
        else return;

        UnregisterAgent(agentId);
    }

    /// <summary>Sends a legacy bridge action result back to the owning runtime connection.</summary>
    public void SendAgentActionResult(string agentId, string connectionId,
        string actionId, AgentActionResult result)
    {
        var conn = WebSocketServer.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn == null) return;

        var status = result.Success ? "completed" : "failed";
        if (Config.Verbose)
        {
            var color = result.Success ? new Microsoft.Xna.Framework.Color(150, 255, 100) : new Microsoft.Xna.Framework.Color(255, 100, 100);
            Main.NewText($"[AI] {status}: {actionId[..Math.Min(8, actionId.Length)]}...", color);
        }

        var payload = new Dictionary<string, object>
        {
            ["agent_id"] = agentId,
            ["action_id"] = actionId,
            ["status"] = result.Success ? "completed" : "failed",
        };

        if (result.Data != null)
            payload["result"] = result.Data;
        if (result.Error.HasValue)
            payload["error"] = new { code = result.Error.Value.Code, message = result.Error.Value.Message };

        var json = MessageSerializer.BuildMessage("agent.action.result", payload, sessionId: conn.Id);
        conn.OutgoingQueue.Enqueue(json);
    }

    // ── Lifecycle ──────────────────────────────────────────────

    private void OnConnectionLost(string connectionId)
    {
        if (_connectionToAgentId.TryGetValue(connectionId, out var agentId))
            UnregisterAgent(agentId);
    }

    private void UnregisterAgent(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return;

        Main.NewText($"[AI] Agent despawned: {agentId[..8]}...", 255, 150, 100);
        agent.IsActive = false;
        if (agent.NPC != null && agent.NPC.active)
        {
            agent.NPC.active = false;
            agent.NPC.life = 0;
        }
        _agents.Remove(agentId);

        var connToRemove = _connectionToAgentId
            .Where(kvp => kvp.Value == agentId)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var connId in connToRemove)
            _connectionToAgentId.Remove(connId);
    }
}
