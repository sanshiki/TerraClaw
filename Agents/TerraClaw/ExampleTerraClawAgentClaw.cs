#nullable enable

using Microsoft.Xna.Framework;
using System;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraClaw.Util;

namespace TerraClaw.Agents.TerraClaw;

/// <summary>Visual claw companion for <see cref="ExampleTerraClawAgent"/>.</summary>
public sealed class ExampleTerraClawAgentClaw : ModProjectile
{
    private const float IdleSpeedMax = 50f;
    private const float IdleSpeedMin = 5f;
    private const float IdleInertia = 80f;
    private const float StrikeSpeed = 27f;
    private const float StrikeInertia = 5f;
    private const float ReturnSpeed = 27f;
    private const float ReturnInertia = 20f;
    private const float HitDistance = 10f;
    private const float ReturnDistance = 14f;

    private Point _breakTile;

    private ClawMode Mode
    {
        get => (ClawMode)(int)Projectile.localAI[0];
        set => Projectile.localAI[0] = (float)value;
    }

    private int ParentNpcIndex => (int)Projectile.ai[0];
    private int SlotSide => Projectile.ai[1] < 0f ? -1 : 1;

    public int Side { get; private set; } = 1;

    public bool CanAcceptBreakTarget => Mode == ClawMode.Idle;

    public override void SetDefaults()
    {
        Projectile.width = 16;
        Projectile.height = 16;
        Projectile.friendly = true;
        Projectile.tileCollide = false;
        Projectile.ignoreWater = true;
        Projectile.penetrate = -1;
        Projectile.timeLeft = 2;
    }

    public override bool? CanDamage() => false;

    public override void AI()
    {
        if (!TryGetParent(out NPC? parent, out ExampleTerraClawAgent? agent))
        {
            Projectile.Kill();
            return;
        }

        Projectile.timeLeft = 2;
        Side = parent.spriteDirection == 1 ? -1 : 1;
        Projectile.spriteDirection = Side;

        switch (Mode)
        {
            case ClawMode.BreakOutbound:
                UpdateStrike(agent);
                break;
            case ClawMode.BreakReturn:
                UpdateReturn(parent, agent);
                break;
            default:
                UpdateIdle(parent);
                break;
        }

        Projectile.rotation = Projectile.velocity.X * 0.04f + Side * 0.25f;
    }

    public void BeginBreakTile(Point tile)
    {
        _breakTile = tile;
        Mode = ClawMode.BreakOutbound;
        Projectile.netUpdate = true;
    }

    public void Recall()
    {
        if (Mode != ClawMode.Idle)
        {
            Mode = ClawMode.BreakReturn;
            Projectile.netUpdate = true;
        }
    }

    private void UpdateIdle(NPC parent)
    {
        Vector2 home = GetHomePosition(parent);
        float speed = MathHelper.Clamp(Vector2.DistanceSquared(Projectile.Center, home), IdleSpeedMin, IdleSpeedMax);
        Projectile.velocity = AIHelper.HomeinToTarget(Projectile.Center, Projectile.velocity, home, speed, IdleInertia);
        Projectile.velocity *= 0.94f;
    }

    private void UpdateStrike(ExampleTerraClawAgent agent)
    {
        Vector2 target = new(_breakTile.X * 16f + 8f, _breakTile.Y * 16f + 8f);
        Projectile.velocity = AIHelper.HomeinToTarget(Projectile.Center, Projectile.velocity, target, StrikeSpeed, StrikeInertia);

        if (Vector2.DistanceSquared(Projectile.Center, target) > HitDistance * HitDistance)
            return;

        bool brokeTile = TryBreakTile(_breakTile);
        agent.NotifyClawBreakComplete(brokeTile);
        SpawnImpactDust(target);
        Mode = ClawMode.BreakReturn;
        Projectile.netUpdate = true;
    }

    private void UpdateReturn(NPC parent, ExampleTerraClawAgent agent)
    {
        Vector2 home = GetHomePosition(parent);
        Projectile.velocity = AIHelper.HomeinToTarget(Projectile.Center, Projectile.velocity, home, ReturnSpeed, ReturnInertia);
        Projectile.velocity *= 0.94f;

        if (Vector2.DistanceSquared(Projectile.Center, home) > ReturnDistance * ReturnDistance)
            return;

        Mode = ClawMode.Idle;
        Projectile.netUpdate = true;
        agent.NotifyClawReturned();
    }

    private Vector2 GetHomePosition(NPC parent)
    {
        float phase = (float)Main.GameUpdateCount * 0.06f + (SlotSide < 0 ? MathHelper.Pi : 0f);
        Vector2 bob = new(MathF.Sin(phase) * 4f, MathF.Cos(phase * 1.3f) * 5f);
        return parent.Center + new Vector2(SlotSide * 34f, 30f) + bob;
    }

    private bool TryGetParent(out NPC? parent, out ExampleTerraClawAgent? agent)
    {
        parent = null;
        agent = null;

        if (ParentNpcIndex < 0 || ParentNpcIndex >= Main.maxNPCs)
            return false;

        NPC candidate = Main.npc[ParentNpcIndex];
        if (!candidate.active || candidate.ModNPC is not ExampleTerraClawAgent terraClawAgent)
            return false;

        parent = candidate;
        agent = terraClawAgent;
        return true;
    }

    private static bool TryBreakTile(Point tilePoint)
    {
        if (!WorldGen.InWorld(tilePoint.X, tilePoint.Y, 10))
            return false;

        Tile tile = Main.tile[tilePoint.X, tilePoint.Y];
        if (!tile.HasTile || !Main.tileSolid[tile.TileType])
            return false;

        if (Main.netMode != NetmodeID.MultiplayerClient)
        {
            WorldGen.KillTile(tilePoint.X, tilePoint.Y, fail: false, effectOnly: false, noItem: false);
            if (Main.netMode == NetmodeID.Server)
                NetMessage.SendData(MessageID.TileManipulation, -1, -1, null, 0, tilePoint.X, tilePoint.Y);
        }

        return true;
    }

    private static void SpawnImpactDust(Vector2 center)
    {
        for (int i = 0; i < 8; i++)
        {
            float angle = (float)(Main.rand.NextDouble() * MathHelper.TwoPi);
            float distance = (float)(Main.rand.NextDouble() * 8.0);
            Vector2 offset = new(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance);
            Dust.QuickDust(center + offset, Color.OrangeRed);
        }
    }

    private enum ClawMode
    {
        Idle,
        BreakOutbound,
        BreakReturn,
    }
}
