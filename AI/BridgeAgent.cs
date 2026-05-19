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

    private readonly ConcurrentQueue<PendingAgentAction> _actionQueue = new();
    private PendingAgentAction _currentAction;
    private int _currentActionTick;

    public BridgeAgent(string agentId, string connectionId)
    {
        AgentId = agentId;
        ConnectionId = connectionId;
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

            default:
                return AgentActionResult.Failed("UNKNOWN_ACTION", $"Unknown action type: {action.ActionType}");
        }
    }

    private AgentActionResult ExecuteMoveTo(PendingAgentAction action, int elapsedTicks)
    {
        float x = (float)action.GetParam("x", 0.0);
        float y = (float)action.GetParam("y", 0.0);
        float speed = (float)action.GetParam("speed", 4.0);
        float arrivalRadius = (float)action.GetParam("arrival_radius", 16.0);

        var target = new Vector2(x, y);
        float dist = Vector2.Distance(NPC.Center, target);

        if (dist <= arrivalRadius)
        {
            NPC.velocity = Vector2.Zero;
            return AgentActionResult.Done(new
            {
                position = new { x = NPC.Center.X, y = NPC.Center.Y },
                distance_remaining = dist,
            });
        }

        MoveToward(target, speed);
        return null; // still running
    }

    private AgentActionResult ExecutePlaceTile(PendingAgentAction action)
    {
        int tx = (int)action.GetParam("tx", -1.0);
        int ty = (int)action.GetParam("ty", -1.0);
        int tileType = (int)action.GetParam("tile_type", (double)TileID.Dirt);
        int style = (int)action.GetParam("style", 0.0);

        if (tx < 0 || ty < 0)
            return AgentActionResult.Failed("INVALID_PARAMS", "tx and ty required");

        bool ok = PlaceTile(tx, ty, tileType, style);
        return new AgentActionResult
        {
            Success = ok,
            Data = new { tile_placed = ok, position = new { x = tx, y = ty }, tile_type = tileType },
        };
    }

    private AgentActionResult ExecutePlaceWall(PendingAgentAction action)
    {
        int tx = (int)action.GetParam("tx", -1.0);
        int ty = (int)action.GetParam("ty", -1.0);
        int wallType = (int)action.GetParam("wall_type", (double)WallID.Glass);

        if (tx < 0 || ty < 0)
            return AgentActionResult.Failed("INVALID_PARAMS", "tx and ty required");

        bool ok = PlaceWall(tx, ty, wallType);
        return new AgentActionResult
        {
            Success = ok,
            Data = new { wall_placed = ok, position = new { x = tx, y = ty }, wall_type = wallType },
        };
    }

    private AgentActionResult ExecuteBreakTile(PendingAgentAction action)
    {
        int tx = (int)action.GetParam("tx", -1.0);
        int ty = (int)action.GetParam("ty", -1.0);

        if (tx < 0 || ty < 0)
            return AgentActionResult.Failed("INVALID_PARAMS", "tx and ty required");

        bool ok = BreakTile(tx, ty);
        return new AgentActionResult
        {
            Success = ok,
            Data = new { tile_broken = ok, position = new { x = tx, y = ty } },
        };
    }

    private AgentActionResult ExecuteBreakWall(PendingAgentAction action)
    {
        int tx = (int)action.GetParam("tx", -1.0);
        int ty = (int)action.GetParam("ty", -1.0);

        if (tx < 0 || ty < 0)
            return AgentActionResult.Failed("INVALID_PARAMS", "tx and ty required");

        bool ok = BreakWall(tx, ty);
        return new AgentActionResult
        {
            Success = ok,
            Data = new { wall_broken = ok, position = new { x = tx, y = ty } },
        };
    }

    private AgentActionResult ExecuteWait(PendingAgentAction action, int elapsedTicks)
    {
        int durationMs = (int)action.GetParam("duration_ms", 1000.0);
        int elapsedMs = (int)(elapsedTicks * (1000.0 / 60.0));

        if (elapsedMs >= durationMs)
            return AgentActionResult.Done(new { waited_ms = elapsedMs });

        return null; // still waiting
    }

    private AgentActionResult ExecuteTeleport(PendingAgentAction action)
    {
        float x = (float)action.GetParam("x", 0.0);
        float y = (float)action.GetParam("y", 0.0);
        Teleport(new Vector2(x, y));
        return AgentActionResult.Done(new { position = new { x, y } });
    }

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

    public double GetParam(string name, double defaultValue)
    {
        return Params.TryGetValue(name, out var val) ? val : defaultValue;
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
