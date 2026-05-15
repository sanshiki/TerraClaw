using System;
using Terraria;
namespace TerraClaw.Util;

public static class GeometryHelpers
{
    public static float Distance(float x1, float y1, float x2, float y2)
        => MathF.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));

    public static int TileDistance(int tx1, int ty1, int tx2, int ty2)
        => Math.Abs(tx1 - tx2) + Math.Abs(ty1 - ty2);

    public static bool IsInRange(float x1, float y1, float x2, float y2, float range)
        => Distance(x1, y1, x2, y2) <= range;

    /// <summary>
    /// Check line-of-sight between two tile positions (simple Bresenham raycast).
    /// Returns false if a solid tile blocks the line.
    /// </summary>
    public static bool HasLineOfSight(int tx1, int ty1, int tx2, int ty2)
    {
        int dx = Math.Abs(tx2 - tx1);
        int dy = -Math.Abs(ty2 - ty1);
        int sx = tx1 < tx2 ? 1 : -1;
        int sy = ty1 < ty2 ? 1 : -1;
        int err = dx + dy;

        int x = tx1, y = ty1;
        while (true)
        {
            if (x == tx2 && y == ty2) return true;

            var tile = Main.tile[x, y];
            if (tile != null && tile.HasTile && Main.tileSolid[tile.TileType])
                return false;

            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }
}
