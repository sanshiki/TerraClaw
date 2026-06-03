#nullable enable

using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraClaw.LLM;

/// <summary>Fluent builder for reusable nearby tile observation components.</summary>
public sealed class TileContextBuilder : ContextBundle
{
    private readonly Vector2 _center;
    private readonly int _radiusTiles;

    internal TileContextBuilder(Vector2 center, int radiusTiles)
    {
        _center = center;
        _radiusTiles = Math.Max(1, radiusTiles);
    }

    /// <summary>Adds compact topology labels and selected notable tiles near the center point.</summary>
    public TileContextBuilder Area(int maxSpecials = 12)
    {
        Add(new TileAreaComponent(_center, _radiusTiles, maxSpecials));
        return this;
    }
}


public sealed class TileAreaComponent : ISymbolicContextProvider
{
    private readonly Vector2 _center;
    private readonly int _radiusTiles;
    private readonly int _maxSpecials;
    private TileScanSnapshot? _snapshot;

    public TileAreaComponent(Vector2 center, int radiusTiles, int maxSpecials)
    {
        _center = center;
        _radiusTiles = Math.Max(1, radiusTiles);
        _maxSpecials = Math.Max(0, maxSpecials);
    }

    public string Symbol => "tiles";
    public string Description => "Nearby tile topology and notable tiles";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("center", "[x,y] center tile"),
        new SymbolicField("r", "scan radius in tiles"),
        new SymbolicField("tags", "compact topology and feature labels"),
        new SymbolicField("solid", "solid collision tile ratio"),
        new SymbolicField("wall", "wall coverage ratio"),
        new SymbolicField("liquid", "dominant liquid type and approximate fill"),
        new SymbolicField("floor", "[dx,dy] nearest floor below center, or null"),
        new SymbolicField("top_tiles", "most common tile types as [name,count,ratio]"),
        new SymbolicField("specials", "grouped notable tiles as [kind,name,count,dx,dy,flags]"),
    };

    public void AddContext(JsonObject target)
    {
        var scan = GetSnapshot();
        target["tile_area"] = new JsonObject
        {
            ["center"] = new JsonObject { ["x"] = scan.CenterX, ["y"] = scan.CenterY },
            ["radius_tiles"] = scan.RadiusTiles,
            ["sampled_tiles"] = scan.SampledTiles,
            ["ratios"] = new JsonObject
            {
                ["solid"] = scan.SolidRatio,
                ["platform"] = scan.PlatformRatio,
                ["wall"] = scan.WallRatio,
                ["open"] = scan.OpenRatio,
            },
            ["liquid"] = LiquidToJson(scan),
            ["floor"] = FloorToJson(scan),
            ["topology"] = TopologyToJson(scan),
            ["tags"] = StringListToJson(scan.Tags),
            ["top_tiles"] = TopTilesToVerboseJson(scan.TopTiles),
            ["specials"] = SpecialsToVerboseJson(scan.Specials),
        };
    }

    public JsonArray ToSymbolicValues()
    {
        var scan = GetSnapshot();
        return new JsonArray
        {
            new JsonArray(scan.CenterX, scan.CenterY),
            scan.RadiusTiles,
            StringListToJson(scan.Tags),
            scan.SolidRatio,
            scan.WallRatio,
            LiquidToSymbolicJson(scan),
            scan.FloorDx.HasValue && scan.FloorDy.HasValue
                ? new JsonArray(scan.FloorDx.Value, scan.FloorDy.Value)
                : null,
            TopTilesToSymbolicJson(scan.TopTiles),
            SpecialsToSymbolicJson(scan.Specials),
        };
    }

    private TileScanSnapshot GetSnapshot()
    {
        _snapshot ??= TileScanSnapshot.Build(_center, _radiusTiles, _maxSpecials);
        return _snapshot;
    }

    private static JsonArray StringListToJson(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        return array;
    }

    private static JsonObject? FloorToJson(TileScanSnapshot scan)
    {
        if (!scan.FloorDx.HasValue || !scan.FloorDy.HasValue)
            return null;

        return new JsonObject
        {
            ["dx"] = scan.FloorDx.Value,
            ["dy"] = scan.FloorDy.Value,
        };
    }

    private static JsonObject LiquidToJson(TileScanSnapshot scan)
    {
        return new JsonObject
        {
            ["kind"] = scan.DominantLiquidKind,
            ["tiles"] = scan.DominantLiquidTiles,
            ["fill"] = scan.DominantLiquidFill,
        };
    }

    private static JsonObject TopologyToJson(TileScanSnapshot scan)
    {
        return new JsonObject
        {
            ["region_tiles"] = scan.RegionTiles,
            ["width"] = scan.RegionWidth,
            ["height"] = scan.RegionHeight,
            ["aspect"] = scan.RegionAspectRatio,
            ["open_edges"] = scan.OpenToEdgeCount,
            ["blocked_boundary"] = scan.BlockedBoundaryRatio,
        };
    }

    private static JsonArray LiquidToSymbolicJson(TileScanSnapshot scan)
    {
        return new JsonArray
        {
            scan.DominantLiquidKind,
            scan.DominantLiquidTiles,
            scan.DominantLiquidFill,
        };
    }

    private static JsonArray SpecialsToVerboseJson(IReadOnlyList<TileSpecialEntry> specials)
    {
        var array = new JsonArray();
        foreach (var special in specials)
        {
            array.Add(new JsonObject
            {
                ["kind"] = special.Kind,
                ["name"] = special.Name,
                ["type"] = special.Type,
                ["x"] = special.X,
                ["y"] = special.Y,
                ["dx"] = special.Dx,
                ["dy"] = special.Dy,
                ["distance_tiles"] = special.Distance,
                ["count"] = special.Count,
                ["flags"] = StringListToJson(special.Flags),
            });
        }
        return array;
    }

    private static JsonArray TopTilesToVerboseJson(IReadOnlyList<TileCountEntry> topTiles)
    {
        var array = new JsonArray();
        foreach (var tile in topTiles)
        {
            array.Add(new JsonObject
            {
                ["name"] = tile.Name,
                ["type"] = tile.Type,
                ["count"] = tile.Count,
                ["ratio"] = tile.Ratio,
            });
        }

        return array;
    }

    private static JsonArray TopTilesToSymbolicJson(IReadOnlyList<TileCountEntry> topTiles)
    {
        var array = new JsonArray();
        foreach (var tile in topTiles)
            array.Add(new JsonArray { tile.Name, tile.Count, tile.Ratio });
        return array;
    }

    private static JsonArray SpecialsToSymbolicJson(IReadOnlyList<TileSpecialEntry> specials)
    {
        var array = new JsonArray();
        foreach (var special in specials)
        {
            array.Add(new JsonArray
            {
                special.Kind,
                special.Name,
                special.Count,
                special.Dx,
                special.Dy,
                StringListToJson(special.Flags),
            });
        }
        return array;
    }
}

internal sealed class TileScanSnapshot
{
    private const int LiquidKinds = 4;
    private const int TopTileKinds = 8;

    private TileScanSnapshot(
        int centerX,
        int centerY,
        int radiusTiles,
        int sampledTiles,
        int solidTiles,
        int platformTiles,
        int wallTiles,
        int openTiles,
        int[] liquidTiles,
        int[] liquidFill,
        int? floorDx,
        int? floorDy,
        TileTopology topology,
        IReadOnlyList<string> tags,
        IReadOnlyList<TileCountEntry> topTiles,
        IReadOnlyList<TileSpecialEntry> specials)
    {
        CenterX = centerX;
        CenterY = centerY;
        RadiusTiles = radiusTiles;
        SampledTiles = sampledTiles;
        SolidRatio = Ratio(solidTiles, sampledTiles);
        PlatformRatio = Ratio(platformTiles, sampledTiles);
        WallRatio = Ratio(wallTiles, sampledTiles);
        OpenRatio = Ratio(openTiles, sampledTiles);
        FloorDx = floorDx;
        FloorDy = floorDy;
        RegionTiles = topology.RegionTiles;
        RegionWidth = topology.Width;
        RegionHeight = topology.Height;
        RegionAspectRatio = Math.Round(topology.AspectRatio, 2);
        OpenToEdgeCount = topology.OpenToEdgeCount;
        BlockedBoundaryRatio = Math.Round(topology.BlockedBoundaryRatio, 2);
        Tags = tags;
        TopTiles = topTiles;
        Specials = specials;

        int dominantLiquid = 0;
        for (int i = 1; i < liquidTiles.Length; i++)
        {
            if (liquidTiles[i] > liquidTiles[dominantLiquid])
                dominantLiquid = i;
        }

        DominantLiquidKind = liquidTiles[dominantLiquid] > 0 ? LiquidKindName(dominantLiquid) : "none";
        DominantLiquidTiles = liquidTiles[dominantLiquid];
        DominantLiquidFill = liquidTiles[dominantLiquid] > 0
            ? Math.Round(liquidFill[dominantLiquid] / (liquidTiles[dominantLiquid] * 255.0), 2)
            : 0;
    }

    public int CenterX { get; }
    public int CenterY { get; }
    public int RadiusTiles { get; }
    public int SampledTiles { get; }
    public double SolidRatio { get; }
    public double PlatformRatio { get; }
    public double WallRatio { get; }
    public double OpenRatio { get; }
    public string DominantLiquidKind { get; }
    public int DominantLiquidTiles { get; }
    public double DominantLiquidFill { get; }
    public int? FloorDx { get; }
    public int? FloorDy { get; }
    public int RegionTiles { get; }
    public int RegionWidth { get; }
    public int RegionHeight { get; }
    public double RegionAspectRatio { get; }
    public int OpenToEdgeCount { get; }
    public double BlockedBoundaryRatio { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<TileCountEntry> TopTiles { get; }
    public IReadOnlyList<TileSpecialEntry> Specials { get; }

    public static TileScanSnapshot Build(Vector2 center, int radiusTiles, int maxSpecials)
    {
        int centerX = (int)(center.X / 16f);
        int centerY = (int)(center.Y / 16f);
        int radiusSq = radiusTiles * radiusTiles;
        int sampledTiles = 0;
        int solidTiles = 0;
        int platformTiles = 0;
        int wallTiles = 0;
        int openTiles = 0;
        int wiredTiles = 0;
        int hazardTiles = 0;
        int containerTiles = 0;
        int doorTiles = 0;
        int craftingTiles = 0;
        int lightTiles = 0;
        int oreTiles = 0;
        int ropeTiles = 0;
        int signTiles = 0;
        int size = radiusTiles * 2 + 1;
        var passable = new bool[size, size];
        var standable = new bool[size, size];
        var liquidTiles = new int[LiquidKinds];
        var liquidFill = new int[LiquidKinds];
        var candidates = new List<TileSpecialEntry>();
        var tileCounts = new Dictionary<int, int>();

        for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
        {
            int y = centerY + dy;
            if (y < 0 || y >= Main.maxTilesY)
                continue;

            for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
            {
                if (dx * dx + dy * dy > radiusSq)
                    continue;

                int x = centerX + dx;
                if (x < 0 || x >= Main.maxTilesX)
                    continue;

                var tile = Framing.GetTileSafely(x, y);
                sampledTiles++;

                bool hasTile = tile.HasTile;
                bool solid = hasTile && tile.HasUnactuatedTile && IsSolid(tile.TileType);
                bool platform = hasTile && tile.HasUnactuatedTile && IsPlatform(tile.TileType);
                bool floor = hasTile && tile.HasUnactuatedTile && IsStandable(tile.TileType);
                bool wall = tile.WallType != 0;
                bool liquid = tile.LiquidAmount > 0;
                bool open = !solid && !liquid;
                int localX = dx + radiusTiles;
                int localY = dy + radiusTiles;
                passable[localX, localY] = open;
                standable[localX, localY] = floor;

                if (solid)
                    solidTiles++;
                if (platform)
                    platformTiles++;
                if (wall)
                    wallTiles++;
                if (open)
                    openTiles++;
                if (HasWireOrActuator(tile))
                    wiredTiles++;

                if (liquid)
                {
                    int liquidKind = Math.Clamp((int)tile.LiquidType, 0, LiquidKinds - 1);
                    liquidTiles[liquidKind]++;
                    liquidFill[liquidKind] += tile.LiquidAmount;
                    candidates.Add(TileSpecialEntry.Liquid(LiquidKindName(liquidKind), x, y, dx, dy, tile.LiquidAmount));
                }

                if (!hasTile)
                    continue;

                int tileType = tile.TileType;
                tileCounts[tileType] = tileCounts.TryGetValue(tileType, out int count) ? count + 1 : 1;

                var special = ClassifySpecialTile(tile, x, y, dx, dy);
                if (special == null)
                    continue;

                candidates.Add(special);
                if (special.Kind == "hazard")
                    hazardTiles++;
                if (special.Kind == "container")
                    containerTiles++;
                if (special.Kind == "door")
                    doorTiles++;
                if (special.Kind == "crafting")
                    craftingTiles++;
                if (special.Kind == "light")
                    lightTiles++;
                if (special.Kind == "ore")
                    oreTiles++;
                if (special.Kind == "rope")
                    ropeTiles++;
                if (special.Kind == "sign")
                    signTiles++;
            }
        }

        var topology = AnalyzeTopology(passable, standable, centerX, centerY, radiusTiles);

        var tags = BuildTags(
            sampledTiles,
            solidTiles,
            wallTiles,
            openTiles,
            wiredTiles,
            hazardTiles,
            containerTiles,
            doorTiles,
            craftingTiles,
            lightTiles,
            oreTiles,
            ropeTiles,
            signTiles,
            liquidTiles,
            topology);

        return new TileScanSnapshot(
            centerX,
            centerY,
            radiusTiles,
            sampledTiles,
            solidTiles,
            platformTiles,
            wallTiles,
            openTiles,
            liquidTiles,
            liquidFill,
            topology.FloorDx,
            topology.FloorDy,
            topology,
            tags,
            SelectTopTiles(tileCounts, sampledTiles, TopTileKinds),
            SelectSpecials(candidates, maxSpecials));
    }

    private static IReadOnlyList<TileCountEntry> SelectTopTiles(Dictionary<int, int> tileCounts, int sampledTiles, int maxTiles)
    {
        if (maxTiles <= 0 || sampledTiles <= 0 || tileCounts.Count == 0)
            return Array.Empty<TileCountEntry>();

        var topTiles = new List<TileCountEntry>(tileCounts.Count);
        foreach (var pair in tileCounts)
        {
            topTiles.Add(new TileCountEntry(
                pair.Key,
                GetTileName(pair.Key),
                pair.Value,
                Ratio(pair.Value, sampledTiles)));
        }

        topTiles.Sort((a, b) =>
        {
            int count = b.Count.CompareTo(a.Count);
            return count != 0 ? count : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        if (topTiles.Count <= maxTiles)
            return topTiles;

        return topTiles.GetRange(0, maxTiles);
    }

    private static IReadOnlyList<string> BuildTags(
        int sampledTiles,
        int solidTiles,
        int wallTiles,
        int openTiles,
        int wiredTiles,
        int hazardTiles,
        int containerTiles,
        int doorTiles,
        int craftingTiles,
        int lightTiles,
        int oreTiles,
        int ropeTiles,
        int signTiles,
        int[] liquidTiles,
        TileTopology topology)
    {
        var tags = new List<string>();
        double solidRatio = Ratio(solidTiles, sampledTiles);
        double wallRatio = Ratio(wallTiles, sampledTiles);
        double openRatio = Ratio(openTiles, sampledTiles);

        if (topology.FoundRegion && topology.OpenToEdgeCount >= 3 && openRatio >= 0.55 && wallRatio < 0.3)
            tags.Add("open");
        if (topology.FoundRegion && topology.OpenToEdgeCount == 0 && topology.BlockedBoundaryRatio >= 0.7)
            tags.Add("enclosed");
        if (topology.FoundRegion &&
            topology.Width >= 6 &&
            topology.Height >= 3 &&
            topology.AspectRatio >= 1.8 &&
            topology.OpenToEdgeCount <= 2)
        {
            tags.Add("tunnel");
        }
        if (topology.FoundRegion &&
            topology.Height >= 8 &&
            topology.AspectRatio <= 0.65 &&
            topology.OpenTop &&
            topology.OpenBottom)
        {
            if(topology.OpenBottom)
                tags.Add("shaft");
            else
                tags.Add("pit");
        }
        if (topology.FloorDy.HasValue)
            tags.Add("floor_near");
        if (liquidTiles[0] > 0)
            tags.Add("water");
        if (liquidTiles[1] > 0)
            tags.Add("lava");
        if (liquidTiles[2] > 0)
            tags.Add("honey");
        if (liquidTiles[3] > 0)
            tags.Add("shimmer");
        if (wiredTiles > 0)
            tags.Add("wired");
        if (hazardTiles > 0)
            tags.Add("hazard");
        if (containerTiles > 0)
            tags.Add("container");
        if (doorTiles > 0)
            tags.Add("door");
        if (craftingTiles > 0)
            tags.Add("crafting");
        if (lightTiles > 0)
            tags.Add("light");
        if (oreTiles > 0)
            tags.Add("ore");
        if (ropeTiles > 0)
            tags.Add("rope");
        if (signTiles > 0)
            tags.Add("sign");

        return tags;
    }

    private static IReadOnlyList<TileSpecialEntry> SelectSpecials(List<TileSpecialEntry> candidates, int maxSpecials)
    {
        if (maxSpecials <= 0 || candidates.Count == 0)
            return Array.Empty<TileSpecialEntry>();

        var groups = new Dictionary<string, List<TileSpecialEntry>>();
        foreach (var candidate in candidates)
        {
            string key = SpecialGroupKey(candidate);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new List<TileSpecialEntry>();
                groups[key] = group;
            }

            group.Add(candidate);
        }

        var merged = new List<TileSpecialEntry>(groups.Count);
        foreach (var group in groups.Values)
            merged.Add(MergeSpecialGroup(group));

        merged.Sort((a, b) =>
        {
            int priority = b.Priority.CompareTo(a.Priority);
            return priority != 0 ? priority : a.Distance.CompareTo(b.Distance);
        });

        var result = new List<TileSpecialEntry>(maxSpecials);
        for (int i = 0; i < merged.Count && result.Count < maxSpecials; i++)
            result.Add(merged[i]);

        return result;
    }

    private static string SpecialGroupKey(TileSpecialEntry special)
    {
        return special.Kind == "liquid" ? $"{special.Kind}:{special.Name}" : special.Kind;
    }

    private static TileSpecialEntry MergeSpecialGroup(List<TileSpecialEntry> group)
    {
        TileSpecialEntry nearest = group[0];
        int priority = group[0].Priority;
        bool liquidGroup = group[0].Kind == "liquid";
        var names = new List<string>();
        var flags = new List<string>();

        foreach (var special in group)
        {
            if (special.Distance < nearest.Distance)
                nearest = special;
            if (special.Priority > priority)
                priority = special.Priority;

            if (!names.Contains(special.Name))
                names.Add(special.Name);
            foreach (string flag in special.Flags)
            {
                if (liquidGroup && flag.StartsWith("fill:", StringComparison.Ordinal))
                    continue;
                if (!flags.Contains(flag))
                    flags.Add(flag);
            }
        }

        if (liquidGroup)
        {
            foreach (string flag in nearest.Flags)
            {
                if (flag.StartsWith("fill:", StringComparison.Ordinal) && !flags.Contains(flag))
                    flags.Add(flag);
            }
        }

        if (names.Count > 1)
        {
            int take = Math.Min(names.Count, 3);
            flags.Add($"variants:{string.Join("/", names.GetRange(0, take))}");
            if (names.Count > take)
                flags.Add($"more_variants:{names.Count - take}");
        }

        string name = names.Count <= 1 ? nearest.Name : $"{nearest.Name}+{names.Count - 1}";
        return new TileSpecialEntry(
            nearest.Kind,
            name,
            nearest.Type,
            nearest.X,
            nearest.Y,
            nearest.Dx,
            nearest.Dy,
            nearest.Distance,
            priority,
            group.Count,
            flags);
    }

    private static TileTopology AnalyzeTopology(bool[,] passable, bool[,] standable, int centerX, int centerY, int radiusTiles)
    {
        const int maxDebugDust = 120;
        int size = radiusTiles * 2 + 1;
        if (!TryFindRegionStart(passable, radiusTiles, out int startX, out int startY))
            return TileTopology.Empty;

        var visited = new bool[size, size];
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((startX, startY));
        visited[startX, startY] = true;
        int debugDustCount = 0;

        int count = 0;
        int minX = startX;
        int maxX = startX;
        int minY = startY;
        int maxY = startY;
        bool openLeft = false;
        bool openRight = false;
        bool openTop = false;
        bool openBottom = false;

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            count++;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);

            if (x == 0)
                openLeft = true;
            if (x == size - 1)
                openRight = true;
            if (y == 0)
                openTop = true;
            if (y == size - 1)
                openBottom = true;

            bool debug = false;
            if (debug && debugDustCount < maxDebugDust && count % 4 == 0)
            {
                Dust.QuickDust(new Vector2((centerX + x - radiusTiles) * 16f + 8f, (centerY + y - radiusTiles) * 16f + 8f), Color.Cyan);
                debugDustCount++;
            }

            TryVisit(x - 1, y);
            TryVisit(x + 1, y);
            TryVisit(x, y - 1);
            TryVisit(x, y + 1);
        }

        FindRegionFloor(visited, standable, radiusTiles, out int? floorDx, out int? floorDy);

        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        double aspectRatio = height > 0 ? width / (double)height : 0;
        int openToEdgeCount = (openLeft ? 1 : 0) + (openRight ? 1 : 0) + (openTop ? 1 : 0) + (openBottom ? 1 : 0);
        double blockedBoundaryRatio = CountBlockedBoundaryRatio(visited, passable, radiusTiles);
        return new TileTopology(
            true,
            count,
            width,
            height,
            aspectRatio,
            openLeft,
            openRight,
            openTop,
            openBottom,
            openToEdgeCount,
            blockedBoundaryRatio,
            floorDx,
            floorDy);

        void TryVisit(int x, int y)
        {
            if (x < 0 || x >= size || y < 0 || y >= size || visited[x, y] || !passable[x, y])
                return;

            visited[x, y] = true;
            queue.Enqueue((x, y));
        }
    }

    private static bool TryFindRegionStart(bool[,] passable, int radiusTiles, out int startX, out int startY)
    {
        int size = radiusTiles * 2 + 1;
        int center = radiusTiles;
        if (passable[center, center])
        {
            startX = center;
            startY = center;
            return true;
        }

        for (int r = 1; r <= radiusTiles; r++)
        {
            int rSq = r * r;
            for (int y = Math.Max(0, center - r); y <= Math.Min(size - 1, center + r); y++)
            {
                for (int x = Math.Max(0, center - r); x <= Math.Min(size - 1, center + r); x++)
                {
                    int dx = x - center;
                    int dy = y - center;
                    if (dx * dx + dy * dy > rSq || !passable[x, y])
                        continue;

                    startX = x;
                    startY = y;
                    return true;
                }
            }
        }

        startX = center;
        startY = center;
        return false;
    }

    private static double CountBlockedBoundaryRatio(bool[,] region, bool[,] passable, int radiusTiles)
    {
        int size = radiusTiles * 2 + 1;
        int blocked = 0;
        int total = 0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!region[x, y])
                    continue;

                CheckNeighbor(x - 1, y);
                CheckNeighbor(x + 1, y);
                CheckNeighbor(x, y - 1);
                CheckNeighbor(x, y + 1);
            }
        }

        return total > 0 ? blocked / (double)total : 0;

        void CheckNeighbor(int x, int y)
        {
            if (x >= 0 && x < size && y >= 0 && y < size && region[x, y])
                return;

            total++;
            if (x < 0 || x >= size || y < 0 || y >= size || !passable[x, y])
                blocked++;
        }
    }

    private static void FindRegionFloor(bool[,] region, bool[,] standable, int radiusTiles, out int? floorDx, out int? floorDy)
    {
        int size = radiusTiles * 2 + 1;
        int center = radiusTiles;
        floorDx = null;
        floorDy = null;
        double bestDistance = double.MaxValue;

        for (int y = center; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!region[x, y])
                    continue;

                int belowY = y + 1;
                if (belowY >= size || !standable[x, belowY])
                    continue;

                int dx = x - center;
                int dy = belowY - center;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance >= bestDistance)
                    continue;

                floorDx = dx;
                floorDy = dy;
                bestDistance = distance;
            }

            if (floorDy == y + 1 - center)
                return;
        }
    }

    private static TileSpecialEntry? ClassifySpecialTile(Tile tile, int x, int y, int dx, int dy)
    {
        int type = tile.TileType;
        string name = GetTileName(type);
        var flags = new List<string>();
        if (HasWireOrActuator(tile))
            flags.Add("wired");
        if (tile.IsActuated)
            flags.Add("actuated");
        if (tile.IsHalfBlock)
            flags.Add("half");
        if (tile.Slope != SlopeType.Solid)
            flags.Add("slope");

        string? kind = GetKnownSpecialKind(type);
        int priority = 1;

        if (kind == null && IsOre(type))
            kind = "ore";
        if (kind == null && IsLighted(type))
            kind = "light";
        if (kind == null && IsPlatform(type))
            kind = "platform";

        switch (kind)
        {
            case "hazard":
                priority = 100;
                break;
            case "container":
            case "sign":
            case "switch":
            case "door":
                priority = 90;
                break;
            case "crafting":
                priority = 70;
                break;
            case "ore":
                priority = 55;
                break;
            case "light":
            case "rope":
            case "platform":
                priority = 40;
                break;
            default:
                if (flags.Count == 0)
                    return null;
                kind = "mechanism";
                priority = 35;
                break;
        }

        return new TileSpecialEntry(kind, name, type, x, y, dx, dy, Math.Round(Math.Sqrt(dx * dx + dy * dy), 1), priority, 1, flags);
    }

    private static string? GetKnownSpecialKind(int type)
    {
        if (IsTileSet(type, TileID.Sets.IsAContainer) || type == TileID.Containers || type == TileID.Containers2)
            return "container";
        if (type == TileID.ClosedDoor || type == TileID.OpenDoor)
            return "door";
        if (type == TileID.Signs)
            return "sign";
        if (IsTileSet(type, TileID.Sets.IsAMechanism) ||
            type == TileID.PressurePlates ||
            type == TileID.Switches ||
            type == TileID.Timers)
        {
            return "switch";
        }
        if (IsHazard(type) ||
            type == TileID.Boulder ||
            type == TileID.Spikes ||
            type == TileID.WoodenSpikes ||
            type == TileID.Cactus)
        {
            return "hazard";
        }
        if (type == TileID.Rope)
            return "rope";
        if (type == TileID.Beds ||
            type == TileID.Anvils ||
            type == TileID.Furnaces ||
            type == TileID.WorkBenches ||
            type == TileID.TinkerersWorkbench ||
            type == TileID.AlchemyTable ||
            type == TileID.Loom ||
            type == TileID.Sawmill)
        {
            return "crafting";
        }
        if (type == TileID.Heart)
            return "life_crystal";
        if (type == TileID.ShadowOrbs)
            return "shadow_orb";
        if (type == TileID.Pots)
            return "pot";

        return null;
    }

    private static bool IsOre(int type)
    {
        return IsTileSet(type, TileID.Sets.Ore) ||
            type == TileID.Copper ||
            type == TileID.Iron ||
            type == TileID.Silver ||
            type == TileID.Gold ||
            type == TileID.Demonite ||
            type == TileID.Meteorite ||
            type == TileID.Hellstone ||
            type == TileID.Cobalt ||
            type == TileID.Mythril ||
            type == TileID.Adamantite ||
            IsTileSet(type, Main.tileSpelunker);
    }

    private static bool IsHazard(int type)
    {
        return IsIntSetPositive(type, TileID.Sets.TouchDamageImmediate) ||
            IsTileSet(type, TileID.Sets.TouchDamageBleeding) ||
            IsTileSet(type, TileID.Sets.TouchDamageHot);
    }

    private static bool IsSolid(int type) => IsTileSet(type, Main.tileSolid) && !IsTileSet(type, Main.tileSolidTop);

    private static bool IsPlatform(int type) => IsTileSet(type, Main.tileSolidTop);

    private static bool IsStandable(int type) => IsTileSet(type, Main.tileSolid) || IsTileSet(type, Main.tileSolidTop);

    private static bool IsLighted(int type) => IsTileSet(type, Main.tileLighted);

    private static bool IsTileSet(int type, bool[] values) => type >= 0 && type < values.Length && values[type];

    private static bool IsIntSetPositive(int type, int[] values) => type >= 0 && type < values.Length && values[type] > 0;

    private static bool HasWireOrActuator(Tile tile)
    {
        return tile.RedWire || tile.BlueWire || tile.GreenWire || tile.YellowWire || tile.HasActuator;
    }

    private static string GetTileName(int type)
    {
        var modTile = TileLoader.GetTile(type);
        if (modTile != null)
            return modTile.Name;

        try
        {
            return TileID.Search.GetName(type);
        }
        catch
        {
            return type.ToString();
        }
    }

    private static string LiquidKindName(int liquidType)
    {
        return liquidType switch
        {
            1 => "lava",
            2 => "honey",
            3 => "shimmer",
            _ => "water",
        };
    }

    private static double Ratio(int value, int total)
    {
        return total > 0 ? Math.Round(value / (double)total, 2) : 0;
    }
}

internal sealed record TileSpecialEntry(
    string Kind,
    string Name,
    int Type,
    int X,
    int Y,
    int Dx,
    int Dy,
    double Distance,
    int Priority,
    int Count,
    IReadOnlyList<string> Flags)
{
    public static TileSpecialEntry Liquid(string kind, int x, int y, int dx, int dy, int fill)
    {
        return new TileSpecialEntry(
            "liquid",
            kind,
            -1,
            x,
            y,
            dx,
            dy,
            Math.Round(Math.Sqrt(dx * dx + dy * dy), 1),
            kind is "lava" or "shimmer" ? 95 : 30,
            1,
            new[] { $"fill:{fill}" });
    }
}

internal sealed record TileCountEntry(
    int Type,
    string Name,
    int Count,
    double Ratio);

internal sealed record TileTopology(
    bool FoundRegion,
    int RegionTiles,
    int Width,
    int Height,
    double AspectRatio,
    bool OpenLeft,
    bool OpenRight,
    bool OpenTop,
    bool OpenBottom,
    int OpenToEdgeCount,
    double BlockedBoundaryRatio,
    int? FloorDx,
    int? FloorDy)
{
    public static TileTopology Empty { get; } = new(
        false,
        0,
        0,
        0,
        0,
        false,
        false,
        false,
        false,
        0,
        0,
        null,
        null);
}
