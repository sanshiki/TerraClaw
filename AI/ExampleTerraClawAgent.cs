using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraClaw.AI;

/// <summary>
/// Example agent that floats through walls without collision.
/// Demonstrates basic movement, tile placement, and tile breaking.
/// </summary>
public class ExampleTerraClawAgent : TerraClawAgent
{
    private Vector2 _target;
    private int _actionTimer;
    private int _retargetTimer;
    private const float MoveSpeed = 4f;

    public override void Initialize()
    {
        // No-clip: ignore tiles and gravity
        NPC.noTileCollide = true;
        NPC.noGravity = true;
        NPC.damage = 0;
        NPC.friendly = true;
        NPC.life = 9999;
        NPC.lifeMax = 9999;

        PickNewTarget();
    }

    public override void AI()
    {
        if (!IsActive) return;

        _actionTimer++;
        _retargetTimer++;

        // Move toward target
        MoveToward(_target, MoveSpeed);

        // Periodically interact with the world
        if (_actionTimer >= 30) // every 0.5s
        {
            _actionTimer = 0;
            PerformWorldInteraction();
        }

        // Pick a new target every few seconds
        if (_retargetTimer >= 180) // every 3s
        {
            _retargetTimer = 0;
            PickNewTarget();
        }

        // Emit some light/dust for visibility
        if (Main.rand.NextBool(3))
        {
            var dust = Dust.NewDustDirect(NPC.position, NPC.width, NPC.height,
                DustID.TreasureSparkle, 0f, 0f, 100, default, 0.8f);
            dust.noGravity = true;
        }
    }

    private void PickNewTarget()
    {
        // Pick a random position within ~30 tiles of current position
        var (cx, cy) = WorldToTile(NPC.Center);
        int tx = cx + Main.rand.Next(-30, 31);
        int ty = cy + Main.rand.Next(-20, 21);
        tx = Math.Clamp(tx, 10, Main.maxTilesX - 10);
        ty = Math.Clamp(ty, 10, Main.maxTilesY - 10);
        _target = TileToWorld(tx, ty);
    }

    private void PerformWorldInteraction()
    {
        // Randomly choose an action: break tile, place tile, place wall, or do nothing
        int action = Main.rand.Next(6);
        var (tx, ty) = WorldToTile(NPC.Center);

        switch (action)
        {
            case 0: // Break a nearby tile in front
                int bx = tx + NPC.direction * 2;
                int by = ty;
                BreakTile(bx, by);
                break;

            case 1: // Break tile below
                BreakTile(tx, ty + 3);
                break;

            case 2: // Place a dirt tile below if missing
                var below = Main.tile[tx, ty + 3];
                if (below != null && !below.HasTile)
                    PlaceTile(tx, ty + 3, TileID.Dirt);
                break;

            case 3: // Place a stone tile
                var spot = Main.tile[tx + 2, ty];
                if (spot != null && !spot.HasTile)
                    PlaceTile(tx + 2, ty, TileID.Stone);
                break;

            case 4: // Place a glass wall in empty space
                PlaceWall(tx, ty, WallID.Glass);
                break;

            case 5: // Break wall
                BreakWall(tx + 1, ty);
                break;
        }
    }
}
