using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraClaw.Core;

namespace TerraClaw.Items;

public class BridgeAgentSpawner : ModItem
{
    public override void SetDefaults()
    {
        Item.width = 24;
        Item.height = 24;
        Item.useTime = 10;
        Item.useAnimation = 10;
        Item.useStyle = ItemUseStyleID.Swing;
        Item.UseSound = SoundID.Item6;
        Item.rare = ItemRarityID.Orange;
    }

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI == Main.myPlayer)
        {
            var pos = Main.MouseWorld;
            BridgeModSystem.PendingSpawnPosition = pos;
            Main.NewText($"[AI] Spawn position set to ({pos.X:F0}, {pos.Y:F0}) — next agent will spawn there", 150, 255, 100);
        }
        return true;
    }
}
