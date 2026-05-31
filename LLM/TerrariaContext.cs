using Microsoft.Xna.Framework;
using Terraria;

namespace TerraClaw.LLM;

/// <summary>
/// Entry point for reusable Terraria observation components.
/// Use these builders before writing custom providers for common NPC, world, entity, and tile data.
/// </summary>
public static class TerrariaContext
{
    /// <summary>Starts an NPC context bundle for the given Terraria NPC.</summary>
    public static NpcContextBuilder Npc(NPC npc) => new(npc);

    /// <summary>Starts a world context bundle.</summary>
    public static WorldContextBuilder World() => new();

    /// <summary>Starts an entity context bundle.</summary>
    public static EntityContextBuilder Entities() => new();

    /// <summary>Starts a compact tile-area context bundle around a world position.</summary>
    public static TileContextBuilder Tiles(Vector2 center, int radiusTiles = 24) => new(center, radiusTiles);
}
