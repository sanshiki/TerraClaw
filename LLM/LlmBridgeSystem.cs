using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ModLoader;
using TerraClaw.Network;

namespace TerraClaw.LLM;

public sealed class LlmBridgeSystem : ModSystem
{
    public static LlmBridgeSystem? Instance { get; private set; }

    private readonly Dictionary<string, LlmRequestHandle> _pending = new();

    public override void Load()
    {
        Instance = this;
    }

    public override void Unload()
    {
        _pending.Clear();
        Instance = null;
    }

    public override void PostUpdateEverything()
    {
        foreach (var handle in _pending.Values.ToList())
        {
            if (!handle.IsPending)
            {
                _pending.Remove(handle.RequestId);
                continue;
            }

            int elapsedMs = (int)((Main.GameUpdateCount - handle.StartedTick) * (1000.0 / 60.0));
            if (elapsedMs > handle.TimeoutMs)
            {
                handle.Timeout();
                _pending.Remove(handle.RequestId);
            }
        }
    }

    public LlmRequestHandle Request(
        string agentId,
        string system,
        string instruction,
        LlmObservation observation,
        LlmOutput output,
        int timeoutMs = 30000)
    {
        var existing = _pending.Values.FirstOrDefault(h => h.AgentId == agentId && h.IsPending);
        if (existing != null)
            return existing;

        string requestId = Guid.NewGuid().ToString();
        var handle = new LlmRequestHandle(requestId, agentId, (int)Main.GameUpdateCount, timeoutMs);
        _pending[requestId] = handle;

        var payload = new JsonObject
        {
            ["request_id"] = requestId,
            ["agent_id"] = agentId,
            ["system"] = system,
            ["instruction"] = instruction,
            ["observation"] = observation.ToJson(),
            ["output_contract"] = output.ToContractJson(),
            ["timeout_ms"] = timeoutMs,
        };

        Broadcast("llm.request", payload);
        return handle;
    }

    public void Cancel(string requestId)
    {
        var payload = new JsonObject { ["request_id"] = requestId };
        Broadcast("llm.cancel", payload);
        _pending.Remove(requestId);
    }

    public void HandleResponse(JsonElement payload)
    {
        string requestId = payload.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(requestId) || !_pending.TryGetValue(requestId, out var handle))
            return;

        string status = payload.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        if (status == "completed")
        {
            JsonNode? output = null;
            if (payload.TryGetProperty("output", out var outEl))
                output = JsonNode.Parse(outEl.GetRawText());
            handle.Complete(output);
        }
        else
        {
            string error = "LLM request failed";
            if (payload.TryGetProperty("error", out var err))
                error = err.GetString() ?? error;
            handle.Fail(error);
        }

        _pending.Remove(requestId);
    }

    private static void Broadcast(string type, object payload)
    {
        var system = Core.BridgeModSystem.Instance;
        if (system?.WebSocketServer == null)
            return;

        var json = MessageSerializer.BuildMessage(type, payload);
        system.WebSocketServer.Broadcast(json);
    }
}
