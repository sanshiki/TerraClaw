using Microsoft.Xna.Framework;
using Terraria;
using TerraClaw.Observation;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

public sealed class NpcBasicContextProvider : IContextProvider
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
}

public sealed class WorldContextProvider : IContextProvider
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
}

public sealed class SpatialContextProvider : IContextProvider
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
