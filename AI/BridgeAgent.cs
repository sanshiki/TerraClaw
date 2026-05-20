using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraClaw.AI;

public class BridgeAgent : TerraClawAgent
{
    public string AgentId { get; private set; }
    public string ConnectionId { get; private set; }
    public string AgentName { get; private set; }

    private readonly ConcurrentQueue<PendingAgentAction> _actionQueue = new();
    private PendingAgentAction _currentAction;
    private int _currentActionTick;

    public BridgeAgent(string agentId, string connectionId, string agentName = "terraclaw")
    {
        AgentId = agentId;
        ConnectionId = connectionId;
        AgentName = agentName;
    }

    public override void Initialize()
    {
        NPC.noTileCollide = true;
        NPC.noGravity = true;
        NPC.damage = 0;
        NPC.friendly = true;
        NPC.life = 9999;
        NPC.lifeMax = 9999;
        NPC.hide = false;
        NPC.chaseable = true;
    }

    /// <summary>True when the agent has a bridge action queued or in progress.</summary>
    protected bool IsActionPending => _currentAction != null || !_actionQueue.IsEmpty;

    public override void AI()
    {
        if (!IsActive) return;

        if (_currentAction == null && _actionQueue.TryDequeue(out var next))
        {
            _currentAction = next;
            _currentActionTick = 0;
            var p = _currentAction;
            var ps = string.Join(", ", p.Params.Select(kv => $"{kv.Key}={kv.Value}"));
            Main.NewText($"[NPC] Executing: {p.ActionType} ({ps})", 100, 200, 255);
        }

        if (_currentAction == null)
        {
            NPC.velocity = Vector2.Zero;
            return;
        }

        _currentActionTick++;
        AgentActionResult result;
        try
        {
            result = ExecuteAction(_currentAction, _currentActionTick);
        }
        catch (Exception ex)
        {
            Main.NewText($"[NPC] Error in {_currentAction.ActionType}: {ex.Message}", 255, 80, 80);
            ReportActionResult(_currentAction, AgentActionResult.Failed("EXCEPTION", ex.Message));
            _currentAction = null;
            return;
        }

        if (result != null)
        {
            Main.NewText($"[NPC] Done: {_currentAction.ActionType} success={result.Success}", 100, 255, 100);
            var completed = _currentAction;
            _currentAction = null;
            ReportActionResult(completed, result);
        }
    }

    public override void OnKill()
    {
        base.OnKill();
        _actionQueue.Clear();
        _currentAction = null;
    }

    public void EnqueueAction(PendingAgentAction action)
    {
        _actionQueue.Enqueue(action);
    }

    public void ClearQueue()
    {
        while (_actionQueue.TryDequeue(out _)) { }
        var cancelled = _currentAction;
        _currentAction = null;
        _currentActionTick = 0;

        // Send result for the cancelled action so Python can resolve its future
        if (cancelled != null)
            ReportActionResult(cancelled, AgentActionResult.Failed("CANCELLED", "Action was cancelled"));
    }

    private AgentActionResult ExecuteAction(PendingAgentAction action, int elapsedTicks)
    {
        int elapsedMs = (int)(elapsedTicks * (1000.0 / 60.0));
        int timeoutMs = action.TimeoutMs > 0 ? action.TimeoutMs : 30000;

        if (elapsedMs > timeoutMs)
            return AgentActionResult.Failed("TIMEOUT", $"Action exceeded {timeoutMs}ms");

        switch (action.ActionType)
        {
            case "move_to":
                return ExecuteMoveTo(action, elapsedTicks);

            case "place_tile":
                return ExecutePlaceTile(action);

            case "place_wall":
                return ExecutePlaceWall(action);

            case "break_tile":
                return ExecuteBreakTile(action);

            case "break_wall":
                return ExecuteBreakWall(action);

            case "wait":
                return ExecuteWait(action, elapsedTicks);

            case "teleport":
                return ExecuteTeleport(action);

            case "talk":
                return ExecuteTalk(action);

            default:
                return AgentActionResult.Failed("UNKNOWN_ACTION", $"Unknown action type: {action.ActionType}");
        }
    }

    protected virtual AgentActionResult ExecuteMoveTo(PendingAgentAction action, int elapsedTicks)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecuteMoveTo not implemented");

    protected virtual AgentActionResult ExecutePlaceTile(PendingAgentAction action)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecutePlaceTile not implemented");

    protected virtual AgentActionResult ExecutePlaceWall(PendingAgentAction action)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecutePlaceWall not implemented");

    protected virtual AgentActionResult ExecuteBreakTile(PendingAgentAction action)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecuteBreakTile not implemented");

    protected virtual AgentActionResult ExecuteBreakWall(PendingAgentAction action)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecuteBreakWall not implemented");

    protected virtual AgentActionResult ExecuteWait(PendingAgentAction action, int elapsedTicks)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecuteWait not implemented");

    protected virtual AgentActionResult ExecuteTeleport(PendingAgentAction action)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecuteTeleport not implemented");

    protected virtual AgentActionResult ExecuteTalk(PendingAgentAction action)
        => AgentActionResult.Failed("NOT_IMPLEMENTED", "ExecuteTalk not implemented");

    private void ReportActionResult(PendingAgentAction action, AgentActionResult result)
    {
        Core.BridgeModSystem.Instance?.SendAgentActionResult(
            AgentId, ConnectionId, action.ActionId, result);
    }
}

public class PendingAgentAction
{
    public string ActionId { get; set; } = Guid.NewGuid().ToString();
    public string ActionType { get; set; } = "";
    public int TimeoutMs { get; set; } = 30000;
    public Dictionary<string, double> Params { get; set; } = new();
    public Dictionary<string, string> StringParams { get; set; } = new();

    public double GetParam(string name, double defaultValue)
    {
        return Params.TryGetValue(name, out var val) ? val : defaultValue;
    }

    public string GetStringParam(string name, string defaultValue)
    {
        return StringParams.TryGetValue(name, out var val) ? val : defaultValue;
    }
}

public class AgentActionResult
{
    public bool Success { get; set; }
    public object Data { get; set; }
    public (string Code, string Message)? Error { get; set; }

    public static AgentActionResult Done(object data = null)
    {
        return new AgentActionResult { Success = true, Data = data };
    }

    public static AgentActionResult Failed(string code, string message)
    {
        return new AgentActionResult { Success = false, Error = (code, message) };
    }
}
