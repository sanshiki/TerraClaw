using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraClaw.Items;

public class AgentSpawner : ModItem
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
            var worldPos = new Vector2(Main.MouseWorld.X, Main.MouseWorld.Y - 16);
            AI.TerraClawAgentNPC.Spawn(worldPos, player.GetSource_ItemUse(Item));
        }
        return true;
    }
}
