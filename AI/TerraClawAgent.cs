using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerraClaw.LLM;

namespace TerraClaw.AI;

/// <summary>
/// Base class for autonomous NPC agents. Subclass this to create custom agent behaviors.
/// Actions like movement, tile placement, and tile breaking are handled through the
/// bound NPC instance.
/// </summary>
public abstract class TerraClawAgent
{
    public NPC NPC { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public string LlmAgentId { get; internal set; } = Guid.NewGuid().ToString();

    /// <summary>Called once when the agent is bound to an NPC.</summary>
    public virtual void Initialize() { }

    /// <summary>Called every AI tick. Override to implement behavior.</summary>
    public virtual void AI() { }

    /// <summary>Called when the NPC is killed or the agent is removed.</summary>
    public virtual void OnKill() => IsActive = false;

    public void BindTo(NPC npc)
    {
        NPC = npc;
        Initialize();
    }

    // ── Action primitives ────────────────────────────────────────

    /// <summary>Move the NPC toward a world-space target at the given speed (pixels/tick).</summary>
    protected void MoveToward(Vector2 target, float speed)
    {
        var dir = target - NPC.Center;
        var dist = dir.Length();
        if (dist < 4f)
        {
            NPC.velocity = Vector2.Zero;
            return;
        }

        if (dist > 0)
            dir /= dist;
        NPC.velocity = dir * speed;
        NPC.direction = dir.X > 0 ? 1 : -1;
    }

    /// <summary>Teleport to a world-space position instantly.</summary>
    protected void Teleport(Vector2 pos)
    {
        NPC.Center = pos;
        NPC.velocity = Vector2.Zero;
    }

    /// <summary>Place a tile at tile coordinates (tx, ty). Returns true on success.</summary>
    protected bool PlaceTile(int tx, int ty, int tileType, int style = 0)
    {
        if (tx < 0 || tx >= Main.maxTilesX || ty < 0 || ty >= Main.maxTilesY)
            return false;

        var tile = Main.tile[tx, ty];
        if (tile == null || tile.HasTile)
            return false;

        WorldGen.PlaceTile(tx, ty, tileType, forced: true, plr: -1, style: style);
        if (Main.tile[tx, ty] != null && Main.tile[tx, ty].HasTile)
        {
            if (Main.netMode == NetmodeID.Server)
                NetMessage.SendTileSquare(-1, tx, ty);
            return true;
        }
        return false;
    }

    /// <summary>Place a wall at tile coordinates (tx, ty). Returns true on success.</summary>
    protected bool PlaceWall(int tx, int ty, int wallType)
    {
        if (tx < 0 || tx >= Main.maxTilesX || ty < 0 || ty >= Main.maxTilesY)
            return false;

        var tile = Main.tile[tx, ty];
        if (tile == null || tile.WallType > WallID.None)
            return false;

        WorldGen.PlaceWall(tx, ty, wallType, mute: true);
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendTileSquare(-1, tx, ty);
        return true;
    }

    /// <summary>Break a tile at tile coordinates. Returns true if a tile was destroyed.</summary>
    protected bool BreakTile(int tx, int ty)
    {
        if (tx < 0 || tx >= Main.maxTilesX || ty < 0 || ty >= Main.maxTilesY)
            return false;

        var tile = Main.tile[tx, ty];
        if (tile == null || !tile.HasTile)
            return false;

        WorldGen.KillTile(tx, ty);
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendTileSquare(-1, tx, ty);
        return true;
    }

    /// <summary>Break a wall at tile coordinates. Returns true if a wall was destroyed.</summary>
    protected bool BreakWall(int tx, int ty)
    {
        if (tx < 0 || tx >= Main.maxTilesX || ty < 0 || ty >= Main.maxTilesY)
            return false;

        var tile = Main.tile[tx, ty];
        if (tile == null || tile.WallType == WallID.None)
            return false;

        WorldGen.KillWall(tx, ty);
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendTileSquare(-1, tx, ty);
        return true;
    }

    /// <summary>Convert tile coordinates to world-space center position.</summary>
    protected static Vector2 TileToWorld(int tx, int ty) => new(tx * 16 + 8, ty * 16 + 8);

    /// <summary>Convert world-space position to tile coordinates.</summary>
    protected static (int x, int y) WorldToTile(Vector2 pos) => ((int)(pos.X / 16), (int)(pos.Y / 16));

    protected LlmRequestHandle RequestLlm(
        LlmObservation observation,
        LlmOutput output,
        string instruction,
        string system = "You are an AI controller inside Terraria. Return only JSON matching the requested output contract.",
        int timeoutMs = 30000)
    {
        if (LlmBridgeSystem.Instance == null)
            throw new InvalidOperationException("LlmBridgeSystem is not loaded.");
        return LlmBridgeSystem.Instance.Request(LlmAgentId, system, instruction, observation, output, timeoutMs);
    }
}
