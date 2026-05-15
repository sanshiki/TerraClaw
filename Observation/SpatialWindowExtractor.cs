using System;
using System.Collections.Generic;
using System.Text;
using System.IO.Compression;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraClaw.Observation;

public class SpatialWindowExtractor
{
    private const byte TileAir = 0;
    private const byte TileSolid = 1;
    private const byte TileLiquid = 2;

    /// <summary>
    /// Extract a spatial window of tiles around the player.
    /// Uses run-length encoding to compress the tile grid into a compact form.
    /// Additionally extracts "interesting" tiles as structured data.
    /// </summary>
    public object Extract(Player player, int radius)
    {
        return Extract(player.Center, radius);
    }

    /// <summary>
    /// Extract a spatial window of tiles around any world position.
    /// </summary>
    public object Extract(Vector2 center, int radius)
    {
        int cx = (int)(center.X / 16);
        int cy = (int)(center.Y / 16);

        int xMin = Math.Max(0, cx - radius);
        int xMax = Math.Min(Main.maxTilesX - 1, cx + radius);
        int yMin = Math.Max(0, cy - radius);
        int yMax = Math.Min(Main.maxTilesY - 1, cy + radius);

        int width = xMax - xMin + 1;
        int height = yMax - yMin + 1;

        // Build RLE-encoded solids, walls, and liquids
        var solids = new List<byte>(width * height / 4);
        var walls = new List<byte>(width * height / 4);
        var liquids = new List<byte>(width * height / 4);
        var interesting = new List<object>();

        byte lastSolid = TileAir, solidRun = 0;
        byte lastWall = 0, wallRun = 0;
        byte lastLiquid = TileAir, liquidRun = 0;

        for (int y = yMin; y <= yMax; y++)
        {
            for (int x = xMin; x <= xMax; x++)
            {
                var tile = Main.tile[x, y];
                if (tile == null) continue;

                bool isSolid = tile.HasTile && Main.tileSolid[tile.TileType];
                bool isLiquid = tile.LiquidAmount > 0;
                byte wallType = (byte)tile.WallType;
                byte solidByte = isSolid ? TileSolid : (isLiquid ? TileLiquid : TileAir);

                // RLE for solids
                if (solidByte == lastSolid && solidRun < 255)
                {
                    solidRun++;
                }
                else
                {
                    solids.Add(solidRun);
                    solids.Add(lastSolid);
                    lastSolid = solidByte;
                    solidRun = 1;
                }

                // RLE for walls
                if (wallType == lastWall && wallRun < 255)
                {
                    wallRun++;
                }
                else
                {
                    walls.Add(wallRun);
                    walls.Add(lastWall);
                    lastWall = wallType;
                    wallRun = 1;
                }

                // RLE for liquids
                byte liqByte = isLiquid ? (byte)Math.Min((int)tile.LiquidAmount, 255) : TileAir;
                if (liqByte == lastLiquid && liquidRun < 255)
                {
                    liquidRun++;
                }
                else
                {
                    liquids.Add(liquidRun);
                    liquids.Add(lastLiquid);
                    lastLiquid = liqByte;
                    liquidRun = 1;
                }

                // Extract interesting tiles
                if (tile.HasTile)
                {
                    var tileType = GetInterestingTileType(tile);
                    if (tileType != null)
                    {
                        interesting.Add(new
                        {
                            pos = new { x, y },
                            type = tileType,
                            quantity = 1,
                        });
                    }
                }
            }
        }

        // Flush final runs
        solids.Add(solidRun); solids.Add(lastSolid);
        walls.Add(wallRun); walls.Add(lastWall);
        liquids.Add(liquidRun); liquids.Add(lastLiquid);

        return new
        {
            center = new { x = cx, y = cy },
            radius,
            tiles = new
            {
                solids = Convert.ToBase64String(solids.ToArray()),
                walls = Convert.ToBase64String(walls.ToArray()),
                liquids = Convert.ToBase64String(liquids.ToArray()),
                interesting,
            },
        };
    }

    private static string? GetInterestingTileType(Tile tile)
    {
        int type = tile.TileType;

        // Ores
        if (type == Terraria.ID.TileID.Copper) return "ore_copper";
        if (type == Terraria.ID.TileID.Iron) return "ore_iron";
        if (type == Terraria.ID.TileID.Silver) return "ore_silver";
        if (type == Terraria.ID.TileID.Gold) return "ore_gold";
        if (type == Terraria.ID.TileID.Demonite) return "ore_demonite";
        if (type == Terraria.ID.TileID.Meteorite) return "ore_meteorite";
        if (type == Terraria.ID.TileID.Hellstone) return "ore_hellstone";
        if (type == Terraria.ID.TileID.Cobalt) return "ore_cobalt";
        if (type == Terraria.ID.TileID.Mythril) return "ore_mythril";
        if (type == Terraria.ID.TileID.Adamantite) return "ore_adamantite";

        // Containers
        if (type == Terraria.ID.TileID.Containers) return "chest";
        if (type == Terraria.ID.TileID.Containers2) return "chest2";

        // Special items
        if (type == Terraria.ID.TileID.Heart) return "life_crystal";
        if (type == Terraria.ID.TileID.ShadowOrbs) return "shadow_orb";
        if (type == Terraria.ID.TileID.Pots) return "pot";
        if (type == Terraria.ID.TileID.Plants || type == Terraria.ID.TileID.Plants2)
        {
            if (tile.TileFrameX >= 0 && tile.TileFrameX < 18) return "herb";
        }

        // Furniture / interactable
        if (type == Terraria.ID.TileID.Beds) return "bed";
        if (type == Terraria.ID.TileID.Anvils) return "anvil";
        if (type == Terraria.ID.TileID.Furnaces) return "furnace";
        if (type == Terraria.ID.TileID.WorkBenches) return "work_bench";
        if (type == Terraria.ID.TileID.TinkerersWorkbench) return "tinkerers_workshop";
        if (type == Terraria.ID.TileID.AlchemyTable) return "alchemy_table";
        if (type == Terraria.ID.TileID.Loom) return "loom";
        if (type == Terraria.ID.TileID.Sawmill) return "sawmill";

        // Doors / platforms
        if (type == Terraria.ID.TileID.ClosedDoor || type == Terraria.ID.TileID.OpenDoor)
            return "door";

        // Pressure plates, traps
        if (type == Terraria.ID.TileID.PressurePlates) return "pressure_plate";
        if (type == Terraria.ID.TileID.Boulder) return "boulder";

        // Wiring
        if (type == Terraria.ID.TileID.WirePipe) return null; // too common, skip

        return null;
    }
}
