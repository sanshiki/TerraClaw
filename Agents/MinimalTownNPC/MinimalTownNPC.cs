using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using System;
using System.Collections.Generic;

namespace TerraClaw.Agents.NPCs
{
    [AutoloadHead]
    public class MinimalTownNPC : ModNPC
    {
        // 复用向导贴图
        public override string Texture => $"Terraria/Images/NPC_{NPCID.Guide}";

        // 复用向导头像
        public override string HeadTexture => "Terraria/Images/TownNPCs/Guide_Default";

        public override void SetStaticDefaults()
        {
            Main.npcFrameCount[Type] = Main.npcFrameCount[NPCID.Guide];

            NPCID.Sets.ActsLikeTownNPC[Type] = true;

            // 复用向导的城镇NPC配置
            NPCID.Sets.ExtraFramesCount[Type] =
                NPCID.Sets.ExtraFramesCount[NPCID.Guide];
            NPCID.Sets.AttackFrameCount[Type] =
                NPCID.Sets.AttackFrameCount[NPCID.Guide];
            NPCID.Sets.DangerDetectRange[Type] =
                NPCID.Sets.DangerDetectRange[NPCID.Guide];
            NPCID.Sets.AttackType[Type] =
                NPCID.Sets.AttackType[NPCID.Guide];
            NPCID.Sets.AttackTime[Type] =
                NPCID.Sets.AttackTime[NPCID.Guide];
            NPCID.Sets.AttackAverageChance[Type] =
                NPCID.Sets.AttackAverageChance[NPCID.Guide];
        }

        public override void SetDefaults()
        {
            NPC.townNPC = true;
            NPC.friendly = true;

            NPC.width = 18;
            NPC.height = 40;

            NPC.aiStyle = 7;

            NPC.damage = 10;
            NPC.defense = 15;
            NPC.lifeMax = 250;

            AnimationType = NPCID.Guide;
        }

        public override bool CanTownNPCSpawn(int numTownNPCs)
        {
            return true;
        }

        public override List< string > SetNPCNameList()
        {
            return new List< string > { "测试NPC" };
        }

        public override string GetChat()
        {
            return "你好，世界。";
        }

        // 攻击方式
        public override void TownNPCAttackStrength(ref int damage, ref float knockback)
        {
            damage = 20;
            knockback = 4f;
        }

        public override void TownNPCAttackCooldown(ref int cooldown, ref int randExtraCooldown)
        {
            cooldown = 30;
            randExtraCooldown = 15;
        }

        public override void TownNPCAttackProj(ref int projType, ref int attackDelay)
        {
            projType = ProjectileID.WoodenArrowFriendly;
            attackDelay = 1;
        }

        public override void TownNPCAttackProjSpeed(ref float multiplier, ref float gravityCorrection, ref float randomOffset)
        {
            multiplier = 8f;
        }
    }
}