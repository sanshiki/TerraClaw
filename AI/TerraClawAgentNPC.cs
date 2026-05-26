using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraClaw.AI;

/// <summary>
/// Host NPC that delegates AI to a TerraClawAgent.
/// Spawn this NPC to run an autonomous agent in the world.
/// </summary>
public class TerraClawAgentNPC : ModNPC
{
    /// <summary>The agent driving this NPC's behavior.</summary>
    public TerraClawAgent Agent { get; set; } = null!;
    // public override string Texture => "Terraria/Images/Projectile_" + ProjectileID.FallingStar; // Just use a placeholder texture

    public override void SetStaticDefaults()
    {
        Main.npcFrameCount[Type] = 4;
    }

    public override void SetDefaults()
    {
        NPC.width = 18;
        NPC.height = 28;
        NPC.damage = 0;
        NPC.defense = 0;
        NPC.lifeMax = 9999;
        NPC.knockBackResist = 0f;
        NPC.dontTakeDamage = true;
        NPC.noTileCollide = true;
        NPC.noGravity = true;
        NPC.friendly = true;
        NPC.chaseable = false;
        NPC.hide = true;
        NPC.dontCountMe = true;

        Agent = new ExampleTerraClawAgent();
        Agent.BindTo(NPC);
    }

    public override void AI()
    {
        if (Agent == null) return;
        Agent.NPC = NPC;
        Agent.AI();

        // velocity damping
        NPC.velocity *= 0.95f;
    }

    public override bool CheckDead()
    {
        Agent.OnKill();
        NPC.life = NPC.lifeMax;
        NPC.active = true;
        return false; // can't die
    }

    public override void FindFrame(int frameHeight)
    {
        NPC.frameCounter++;

        if (NPC.frameCounter >= 10)
        {
            NPC.frameCounter = 0;
            NPC.frame.Y += frameHeight;

            if (NPC.frame.Y >= frameHeight * 4)
            {
                NPC.frame.Y = 0;
            }
        }
    }

    public override bool CheckActive() => false; // never despawn

    /// <summary>
    /// Spawn an agent NPC at the given world coordinates.
    /// Returns the NPC whoAmI, or -1 on failure.
    /// </summary>
    public static int Spawn(Vector2 worldPos, Terraria.DataStructures.IEntitySource source, TerraClawAgent? agent = null, string agentName = "")
    {
        int index = NPC.NewNPC(source, (int)worldPos.X, (int)worldPos.Y,
            ModContent.NPCType<TerraClawAgentNPC>());
        if (index >= 0 && index < Main.maxNPCs)
        {
            var npc = Main.npc[index];
            if (!string.IsNullOrEmpty(agentName))
                npc.GivenName = agentName;
            if (npc.ModNPC is TerraClawAgentNPC agentNpc && agent != null)
            {
                agentNpc.Agent = agent;
                agent.LlmAgentId = string.IsNullOrWhiteSpace(agent.LlmAgentId) ? Guid.NewGuid().ToString() : agent.LlmAgentId;
                agent.BindTo(npc);
            }
        }
        return index;
    }
}
