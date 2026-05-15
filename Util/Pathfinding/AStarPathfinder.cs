using System;
using System.Collections.Generic;
using Terraria;
namespace TerraClaw.Util.Pathfinding;

public static class AStarPathfinder
{
    private const int MaxIterations = 5000;

    /// <summary>
    /// A* pathfinding on the tile grid.
    /// Returns a list of (tileX, tileY) waypoints from start to goal.
    /// </summary>
    public static List<(int x, int y)> FindPath(int startX, int startY, int goalX, int goalY, bool allowMining = true)
    {
        var openSet = new PriorityQueue<PathNode, float>();
        var closedSet = new HashSet<(int, int)>();
        var nodes = new Dictionary<(int, int), PathNode>();

        var startNode = new PathNode(startX, startY, 0, Heuristic(startX, startY, goalX, goalY));
        nodes[(startX, startY)] = startNode;
        openSet.Enqueue(startNode, startNode.F);

        int iterations = 0;
        var directions = new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1) };

        while (openSet.Count > 0 && iterations < MaxIterations)
        {
            iterations++;
            var current = openSet.Dequeue();

            if (current.X == goalX && current.Y == goalY)
            {
                // Reconstruct path
                var path = new List<(int x, int y)>();
                var node = current;
                while (node != null)
                {
                    path.Add((node.X, node.Y));
                    node = node.Parent!;
                }
                path.Reverse();
                return path;
            }

            if (!closedSet.Add((current.X, current.Y)))
                continue;

            foreach (var (dx, dy) in directions)
            {
                int nx = current.X + dx;
                int ny = current.Y + dy;

                if (closedSet.Contains((nx, ny))) continue;
                if (nx < 0 || nx >= Main.maxTilesX || ny < 0 || ny >= Main.maxTilesY) continue;
                if (!IsWalkable(nx, ny, allowMining)) continue;

                // Diagonal movement requires both adjacent tiles to be walkable
                if (dx != 0 && dy != 0)
                {
                    if (!IsWalkable(current.X + dx, current.Y, allowMining) ||
                        !IsWalkable(current.X, current.Y + dy, allowMining))
                        continue;
                }

                float moveCost = (dx != 0 && dy != 0) ? 1.414f : 1.0f;
                // Penalize going through water
                var tile = Main.tile[nx, ny];
                if (tile != null && tile.LiquidAmount > 100)
                    moveCost *= 2.0f;
                // Penalize air tiles (prefer staying on ground)
                if (tile != null && !tile.HasTile)
                {
                    var below = Main.tile[nx, ny + 1];
                    if (below == null || !below.HasTile)
                        moveCost *= 1.5f; // No floor — might need to jump
                }

                float newG = current.G + moveCost;

                if (nodes.TryGetValue((nx, ny), out var existing))
                {
                    if (newG < existing.G)
                    {
                        existing.G = newG;
                        existing.Parent = current;
                        openSet.Enqueue(existing, existing.F);
                    }
                }
                else
                {
                    var newNode = new PathNode(nx, ny, newG, Heuristic(nx, ny, goalX, goalY))
                    {
                        Parent = current,
                    };
                    nodes[(nx, ny)] = newNode;
                    openSet.Enqueue(newNode, newNode.F);
                }
            }
        }

        return new List<(int x, int y)>();
    }

    private static float Heuristic(int x1, int y1, int x2, int y2)
    {
        float dx = Math.Abs(x1 - x2);
        float dy = Math.Abs(y1 - y2);
        return (float)(Math.Sqrt(dx * dx + dy * dy));
    }

    private static bool IsWalkable(int x, int y, bool allowMining)
    {
        var tile = Main.tile[x, y];
        if (tile == null) return false;

        // Solid tiles are not walkable (unless allowMining)
        if (tile.HasTile && Main.tileSolid[tile.TileType])
            return false;

        // Check if tile above is open (need 3 tiles of vertical clearance for player)
        var above = Main.tile[x, y - 1];
        if (above != null && above.HasTile && Main.tileSolid[above.TileType])
            return false;

        var above2 = Main.tile[x, y - 2];
        if (above2 != null && above2.HasTile && Main.tileSolid[above2.TileType])
            return false;

        return true;
    }

    private class PathNode
    {
        public int X, Y;
        public float G, H;
        public float F => G + H;
        public PathNode? Parent;

        public PathNode(int x, int y, float g, float h)
        {
            X = x; Y = y; G = g; H = h;
        }
    }
}
