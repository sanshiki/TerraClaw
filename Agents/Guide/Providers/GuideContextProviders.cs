using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using TerraClaw.Agents.Guide;
using TerraClaw.LLM;

namespace TerraClaw.Agents.Guide.Providers;

public sealed class GuideNpcContextProvider : ISymbolicContextProvider
{
    private readonly NPC _npc;

    public GuideNpcContextProvider(NPC npc)
    {
        _npc = npc;
    }

    public void AddContext(JsonObject target)
    {
        target["guide"] = new JsonObject
        {
            ["who_am_i"] = _npc.whoAmI,
            ["name"] = _npc.FullName,
            ["position"] = new JsonObject { ["x"] = _npc.Center.X, ["y"] = _npc.Center.Y },
            ["tile_position"] = new JsonObject
            {
                ["x"] = (int)(_npc.Center.X / 16),
                ["y"] = (int)(_npc.Center.Y / 16),
            },
            ["life"] = _npc.life,
            ["life_max"] = _npc.lifeMax,
            ["defense"] = _npc.defense,
            ["home"] = new JsonObject { ["x"] = _npc.homeTileX, ["y"] = _npc.homeTileY },
            ["homeless"] = _npc.homeless,
            ["town_npc"] = _npc.townNPC,
        };
    }

    public string Symbol => "g";
    public string Description => "Guide town NPC state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("id", "NPC whoAmI"),
        new SymbolicField("name", "NPC display name"),
        new SymbolicField("pos", "[x,y] world position in pixels"),
        new SymbolicField("tilepos", "[x,y] tile position"),
        new SymbolicField("hp", "current life"),
        new SymbolicField("maxhp", "maximum life"),
        new SymbolicField("def", "defense"),
        new SymbolicField("home", "[x,y] home tile"),
        new SymbolicField("homeless", "0=false, 1=true"),
        new SymbolicField("town", "0=false, 1=true"),
    };

    public JsonArray ToSymbolicValues() => new()
    {
        _npc.whoAmI,
        _npc.FullName,
        new JsonArray(_npc.Center.X, _npc.Center.Y),
        new JsonArray((int)(_npc.Center.X / 16), (int)(_npc.Center.Y / 16)),
        _npc.life,
        _npc.lifeMax,
        _npc.defense,
        new JsonArray(_npc.homeTileX, _npc.homeTileY),
        _npc.homeless ? 1 : 0,
        _npc.townNPC ? 1 : 0,
    };
}

public sealed class GuideWorldContextProvider : ISymbolicContextProvider
{
    public void AddContext(JsonObject target)
    {
        target["guide_world"] = new JsonObject
        {
            ["time"] = Main.time / 3600.0,
            ["is_day"] = Main.dayTime,
            ["moon_phase"] = Main.moonPhase,
            ["blood_moon"] = Main.bloodMoon,
            ["hardmode"] = Main.hardMode,
        };
    }

    public string Symbol => "tw";
    public string Description => "Town NPC relevant world state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("time", "world time hour"),
        new SymbolicField("is_day", "0=false, 1=true"),
        new SymbolicField("moon", "moon phase integer"),
        new SymbolicField("blood", "0=false, 1=true"),
        new SymbolicField("hardmode", "0=false, 1=true"),
    };

    public JsonArray ToSymbolicValues() => new()
    {
        Main.time / 3600.0,
        Main.dayTime ? 1 : 0,
        Main.moonPhase,
        Main.bloodMoon ? 1 : 0,
        Main.hardMode ? 1 : 0,
    };
}

public sealed class GuideMindContextProvider : ISymbolicContextProvider
{
    private readonly GuideAgentState _state;
    private readonly string _trigger;

    public GuideMindContextProvider(GuideAgentState state, string trigger)
    {
        _state = state;
        _trigger = trigger;
    }

    public void AddContext(JsonObject target)
    {
        target["guide_mind"] = new JsonObject
        {
            ["emotion"] = _state.Emotion,
            ["cached_text"] = _state.CachedChatText,
            ["trigger"] = _trigger,
        };
    }

    public string Symbol => "gm";
    public string Description => "Guide LLM memory and trigger state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("emotion", "agent-maintained emotional state"),
        new SymbolicField("cached", "chat text shown on right-click interaction"),
        new SymbolicField("trigger", "why this LLM request was made"),
    };

    public JsonArray ToSymbolicValues() => new()
    {
        _state.Emotion,
        _state.CachedChatText,
        _trigger,
    };
}

public sealed class GuideHostileContextProvider : ISymbolicContextProvider
{
    private readonly Vector2 _center;
    private readonly float _radius;

    public GuideHostileContextProvider(Vector2 center, float radius)
    {
        _center = center;
        _radius = radius;
    }

    public void AddContext(JsonObject target)
    {
        var hostiles = BuildHostileList();
        target["guide_hostiles"] = new JsonObject
        {
            ["radius"] = _radius,
            ["count"] = hostiles.Count,
            ["nearby"] = hostiles,
        };
    }

    public string Symbol => "gh";
    public string Description => "Nearby hostile NPCs around the Guide";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("count", "number of hostile NPCs in radius"),
        new SymbolicField("nearest", "nearest hostile NPC type/name"),
        new SymbolicField("dist", "nearest hostile distance in pixels"),
    };

    public JsonArray ToSymbolicValues()
    {
        var hostiles = BuildHostileList();
        if (hostiles.Count == 0)
            return new JsonArray { 0, "", 0 };

        var nearest = hostiles[0] as JsonObject;
        return new JsonArray
        {
            hostiles.Count,
            nearest?["type"]?.GetValue<string>() ?? "",
            nearest?["distance"]?.GetValue<double>() ?? 0,
        };
    }

    private JsonArray BuildHostileList()
    {
        var result = new List<(double Distance, JsonObject Data)>();
        float radiusSq = _radius * _radius;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var npc = Main.npc[i];
            if (npc == null || !npc.active || npc.friendly || npc.townNPC || npc.dontTakeDamage)
                continue;
            float distSq = Vector2.DistanceSquared(_center, npc.Center);
            if (distSq > radiusSq)
                continue;

            double dist = System.Math.Sqrt(distSq);
            result.Add((dist, new JsonObject
            {
                ["id"] = npc.whoAmI,
                ["type"] = npc.FullName,
                ["position"] = new JsonObject { ["x"] = npc.Center.X, ["y"] = npc.Center.Y },
                ["distance"] = dist,
                ["life"] = npc.life,
                ["life_max"] = npc.lifeMax,
                ["boss"] = npc.boss,
            }));
        }

        result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var array = new JsonArray();
        for (int i = 0; i < result.Count && i < 5; i++)
            array.Add(result[i].Data);
        return array;
    }
}
