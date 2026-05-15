using System;
using System.Collections.Generic;
namespace TerraClaw.Util.Pathfinding;

/// <summary>
/// Placeholder for a simplified navigation mesh representation.
/// Will be used for more efficient long-distance pathfinding by
/// grouping walkable regions and precomputing connections.
/// </summary>
public class NavigationMesh
{
    public record NavRegion(int MinX, int MinY, int MaxX, int MaxY, string Label);
    public record NavEdge(string From, string To, float Cost, bool RequiresJump, bool RequiresMining);

    public List<NavRegion> Regions { get; } = new();
    public List<NavEdge> Edges { get; } = new();

    public void BuildFromTiles(int tileRadiusAroundPlayer)
    {
        // MVP: Not yet implemented.
        // Future: Flood-fill walkable regions, detect connections between them,
        // and use region graph for faster pathfinding over long distances.
    }

    public List<(int x, int y)> FindPathViaRegions(int sx, int sy, int gx, int gy)
    {
        // MVP: Falls back to A* directly
        return AStarPathfinder.FindPath(sx, sy, gx, gy);
    }
}
