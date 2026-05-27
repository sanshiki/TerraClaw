using Microsoft.Xna.Framework;
using Terraria;
using TerraClaw.Observation;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

public sealed class NpcBasicContextProvider : ISymbolicContextProvider
{
    private readonly NPC _npc;

    public NpcBasicContextProvider(NPC npc)
    {
        _npc = npc;
    }

    public void AddContext(JsonObject target)
    {
        target["npc"] = new JsonObject
        {
            ["who_am_i"] = _npc.whoAmI,
            ["name"] = _npc.FullName,
            ["position"] = new JsonObject { ["x"] = _npc.Center.X, ["y"] = _npc.Center.Y },
            ["velocity"] = new JsonObject { ["x"] = _npc.velocity.X, ["y"] = _npc.velocity.Y },
            ["tile_position"] = new JsonObject
            {
                ["x"] = (int)(_npc.Center.X / 16),
                ["y"] = (int)(_npc.Center.Y / 16),
            },
            ["direction"] = _npc.direction == 1 ? "right" : "left",
            ["life"] = _npc.life,
            ["life_max"] = _npc.lifeMax,
            ["defense"] = _npc.defense,
        };
    }

    public string Symbol => "n";
    public string Description => "NPC basic state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("id", "NPC whoAmI"),
        new SymbolicField("name", "NPC display name"),
        new SymbolicField("pos", "[x,y] world position in pixels"),
        new SymbolicField("vel", "[x,y] velocity"),
        new SymbolicField("tilepos", "[x,y] tile position"),
        new SymbolicField("dir", "l=left, r=right"),
        new SymbolicField("hp", "current life"),
        new SymbolicField("maxhp", "maximum life"),
        new SymbolicField("def", "defense"),
    };

    public JsonArray ToSymbolicValues() => new()
    {
        _npc.whoAmI,
        _npc.FullName,
        new JsonArray(_npc.Center.X, _npc.Center.Y),
        new JsonArray(_npc.velocity.X, _npc.velocity.Y),
        new JsonArray((int)(_npc.Center.X / 16), (int)(_npc.Center.Y / 16)),
        _npc.direction == 1 ? "r" : "l",
        _npc.life,
        _npc.lifeMax,
        _npc.defense,
    };
}

public sealed class WorldContextProvider : ISymbolicContextProvider
{
    private readonly Vector2 _center;

    public WorldContextProvider(Vector2 center)
    {
        _center = center;
    }

    public void AddContext(JsonObject target)
    {
        target["world"] = new JsonObject
        {
            ["time"] = new JsonObject
            {
                ["hour"] = Main.time / 3600.0,
                ["minute"] = (Main.time % 3600) / 60.0,
                ["is_day"] = Main.dayTime,
            },
            ["hardmode"] = Main.hardMode,
            ["position"] = new JsonObject { ["x"] = _center.X, ["y"] = _center.Y },
        };
    }

    public string Symbol => "w";
    public string Description => "World state near the agent";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("time", "world time hour"),
        new SymbolicField("is_day", "0=false, 1=true"),
        new SymbolicField("hardmode", "0=false, 1=true"),
    };

    public JsonArray ToSymbolicValues() => new()
    {
        Main.time / 3600.0,
        Main.dayTime ? 1 : 0,
        Main.hardMode ? 1 : 0,
    };
}

public sealed class SpatialContextProvider : ISymbolicContextProvider
{
    private readonly SpatialWindowExtractor _extractor = new();
    private readonly Vector2 _center;
    private readonly int _radius;

    public SpatialContextProvider(Vector2 center, int radius)
    {
        _center = center;
        _radius = radius;
    }

    public void AddContext(JsonObject target)
    {
        target["spatial_window"] = JsonSerializerHelper.ToNode(_extractor.Extract(_center, _radius));
    }

    public string Symbol => "m";
    public string Description => "Compressed spatial tile maps";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("solid", "base64/RLE solid tiles"),
        new SymbolicField("wall", "base64/RLE walls"),
        new SymbolicField("liquid", "base64/RLE liquids"),
    };

    public JsonArray ToSymbolicValues()
    {
        var spatial = _extractor.Extract(_center, _radius);
        var node = JsonSerializerHelper.ToNode(spatial) as JsonObject;
        var tiles = node?["tiles"] as JsonObject;
        return new JsonArray
        {
            tiles?["solids"]?.GetValue<string>() ?? "",
            tiles?["walls"]?.GetValue<string>() ?? "",
            tiles?["liquids"]?.GetValue<string>() ?? "",
        };
    }
}

public static class NpcContexts
{
    public static IContextProvider Basic(NPC npc) => new NpcBasicContextProvider(npc);
}

public static class WorldContexts
{
    public static IContextProvider Basic(Vector2 center) => new WorldContextProvider(center);
    public static IContextProvider Spatial(Vector2 center, int radius) => new SpatialContextProvider(center, radius);
}

internal static class JsonSerializerHelper
{
    public static JsonNode? ToNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value);
}
