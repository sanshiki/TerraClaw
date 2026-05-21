using Microsoft.Xna.Framework;
using TerraClaw.Util;
using Terraria;
using Terraria.ID;

namespace TerraClaw.AI;

/// <summary>
/// Example BridgeAgent subclass — demonstrates how to override atomic action implementations.
/// This is the default agent used for "terraclaw" in the factory.
/// </summary>
public class ExampleTerraClawAgent : BridgeAgent
{
    public ExampleTerraClawAgent(string agentId, string connectionId, string agentName = "terraclaw")
        : base(agentId, connectionId, agentName) { }

    /// <summary>Parameterless constructor for standalone NPC spawn (no bridge).</summary>
    public ExampleTerraClawAgent() : base("", "", "terraclaw") { }

    private const float INERTIA = 20f;

    // ── Atomic action implementations ──────────────────────────

    protected override AgentActionResult ExecuteMoveTo(PendingAgentAction action, int elapsedTicks)
    {
        float x = (float)action.GetParam("x", 0.0);
        float y = (float)action.GetParam("y", 0.0);
        float speed = (float)action.GetParam("speed", 4.0);
        float arrivalRadius = (float)action.GetParam("arrival_radius", 16.0);

        var target = new Vector2(x, y);
        float dist = Vector2.Distance(NPC.Center, target);

        if (dist <= arrivalRadius)
        {
            return AgentActionResult.Done(new
            {
                position = new { x = NPC.Center.X, y = NPC.Center.Y },
                distance_remaining = dist,
            });
        }



        var dir = target - NPC.Center;
        
        NPC.velocity = AIHelper.HomeinToTarget(NPC.Center, NPC.velocity, target, speed, INERTIA);

        NPC.direction = dir.X > 0 ? 1 : -1;

        return null;
    }

    protected override AgentActionResult ExecutePlaceTile(PendingAgentAction action)
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

    protected override AgentActionResult ExecutePlaceWall(PendingAgentAction action)
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


    private int bounceBackCnt = 0;
    private bool bounceBack = false;
    private AgentActionResult tileBreakingResult = null;
    protected override AgentActionResult ExecuteBreakTile(PendingAgentAction action)
    {
        int tx = (int)action.GetParam("tx", -1.0);
        int ty = (int)action.GetParam("ty", -1.0);

        AgentActionResult result = null;

        if (tx < 0 || ty < 0)
            return AgentActionResult.Failed("INVALID_PARAMS", "tx and ty required");

        if (tx >= Main.maxTilesX || ty >= Main.maxTilesY)
            return AgentActionResult.Failed("INVALID_PARAMS", "tx or ty out of bounds");

        var tile = Main.tile[tx, ty];
        if (tile == null || !tile.HasTile)
            return AgentActionResult.Failed("INVALID_PARAMS", "No tile to break at specified coordinates");

        Vector2 dist = new Vector2(tx * 16 + 8, ty * 16 + 8) - NPC.Center;
        Vector2 dir = dist;
        if(dir != Vector2.Zero)
            dir.Normalize();
        else dir = new Vector2(0, -1);

        if(bounceBack)
        {
            bounceBackCnt++;
            if(bounceBackCnt > 30)
            {
                bounceBack = false;
                bounceBackCnt = 0;
                result = tileBreakingResult;
                tileBreakingResult = null;
            }
        }
        else
        {
            // the closer to the tile, the faster to move
            NPC.velocity = dir * MathHelper.Clamp(0, 10f, 10f / (dist.Length()+0.01f));
            if(dist.Length() < 16)
            {
                // break the tile and bounce back
                NPC.velocity = -NPC.velocity * 0.6f;
                bounceBack = true;
                bool ok = BreakTile(tx, ty);
                tileBreakingResult = new AgentActionResult
                {
                    Success = ok,
                    Data = new { tile_broken = ok, position = new { x = tx, y = ty } },
                };
            }
        }

        return result;
    }

    protected override AgentActionResult ExecuteBreakWall(PendingAgentAction action)
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

    protected override AgentActionResult ExecuteWait(PendingAgentAction action, int elapsedTicks)
    {
        int durationMs = (int)action.GetParam("duration_ms", 1000.0);
        int elapsedMs = (int)(elapsedTicks * (1000.0 / 60.0));

        if (elapsedMs >= durationMs)
            return AgentActionResult.Done(new { waited_ms = elapsedMs });

        return null;
    }

    protected override AgentActionResult ExecuteTeleport(PendingAgentAction action)
    {
        float x = (float)action.GetParam("x", 0.0);
        float y = (float)action.GetParam("y", 0.0);
        Teleport(new Vector2(x, y));
        return AgentActionResult.Done(new { position = new { x, y } });
    }

    protected override AgentActionResult ExecuteTalk(PendingAgentAction action)
    {
        string text = action.GetStringParam("text", "");
        if (string.IsNullOrEmpty(text))
            return AgentActionResult.Failed("INVALID_PARAMS", "text is required");

        if (text.Length > 80)
            text = text[..80];

        Main.NewText($"<{NPC.FullName}> {text}", 200, 200, 100);
        CombatText.NewText(NPC.Hitbox, Color.Gold, text);

        return AgentActionResult.Done(new { said = text, truncated = text.Length > 80 });
    }
}
