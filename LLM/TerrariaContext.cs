using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;

namespace TerraClaw.LLM;

/// <summary>
/// Entry point for reusable Terraria observation components.
/// Use these builders before writing custom providers for common NPC, world, and entity data.
/// </summary>
public static class TerrariaContext
{
    /// <summary>Starts an NPC context bundle for the given Terraria NPC.</summary>
    public static NpcContextBuilder Npc(NPC npc) => new(npc);

    /// <summary>Starts a world context bundle.</summary>
    public static WorldContextBuilder World() => new();

    /// <summary>Starts an entity context bundle.</summary>
    public static EntityContextBuilder Entities() => new();
}

/// <summary>Fluent builder for reusable NPC observation components.</summary>
public sealed class NpcContextBuilder : ContextBundle
{
    private readonly NPC _npc;

    internal NpcContextBuilder(NPC npc)
    {
        _npc = npc;
    }

    /// <summary>Adds identity, position, velocity, direction, and town-NPC status.</summary>
    public NpcContextBuilder Basic()
    {
        Add(new NpcBasicComponent(_npc));
        return this;
    }

    /// <summary>Adds current life, max life, and defense.</summary>
    public NpcContextBuilder Life()
    {
        Add(new NpcLifeComponent(_npc));
        return this;
    }

    /// <summary>Adds town-NPC home tile and homeless state.</summary>
    public NpcContextBuilder Home()
    {
        Add(new NpcHomeComponent(_npc));
        return this;
    }
}

/// <summary>Fluent builder for reusable world observation components.</summary>
public sealed class WorldContextBuilder : ContextBundle
{
    /// <summary>Adds world time and day/night state.</summary>
    public WorldContextBuilder Time()
    {
        Add(new WorldTimeComponent());
        return this;
    }

    /// <summary>Adds moon phase and blood moon state.</summary>
    public WorldContextBuilder Moon()
    {
        Add(new WorldMoonComponent());
        return this;
    }

    /// <summary>Adds broad world progression flags such as hardmode.</summary>
    public WorldContextBuilder Progression()
    {
        Add(new WorldProgressionComponent());
        return this;
    }
}

/// <summary>Fluent builder for reusable nearby entity observation components.</summary>
public sealed class EntityContextBuilder : ContextBundle
{
    /// <summary>Adds a compact summary and verbose list of NPCs near a world position using a custom filter.</summary>
    public EntityContextBuilder NPCNear(
        Vector2 center,
        float radius,
        int max,
        Func<NPC, bool> predicate,
        string symbol = "near_npc",
        string description = "Nearby NPCs")
    {
        Add(new NearbyNpcsComponent(center, radius, max, predicate, symbol, description));
        return this;
    }

    /// <summary>Adds a compact summary and verbose list of hostile NPCs near a world position.</summary>
    public EntityContextBuilder HostilesNear(Vector2 center, float radius, int max = 5)
    {
        return NPCNear(
            center,
            radius,
            max,
            npc => npc.CanBeChasedBy(),
            symbol: "hostile",
            description: "Nearby hostile NPCs");
    }

    /// <summary>Adds a compact summary and verbose list of town NPCs near a world position.</summary>
    public EntityContextBuilder TownNPCNear(Vector2 center, float radius, int max = 5)
    {
        return NPCNear(
            center,
            radius,
            max,
            npc => npc.townNPC,
            symbol: "town_npc",
            description: "Nearby town NPCs");
    }
}

/// <summary>Symbolic provider for core NPC identity and motion state.</summary>
public sealed class NpcBasicComponent : ISymbolicContextProvider
{
    private readonly NPC _npc;

    public NpcBasicComponent(NPC npc) => _npc = npc;

    public string Symbol => "npc";
    public string Description => "NPC basic state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("id", "NPC whoAmI"),
        new SymbolicField("name", "NPC display name"),
        new SymbolicField("pos", "[x,y] world position in pixels"),
        new SymbolicField("vel", "[x,y] velocity"),
        new SymbolicField("tile", "[x,y] tile position"),
        new SymbolicField("dir", "l=left, r=right"),
        new SymbolicField("town", "0=false, 1=true"),
    };

    public void AddContext(JsonObject target)
    {
        target["npc_basic"] = new JsonObject
        {
            ["id"] = _npc.whoAmI,
            ["name"] = _npc.FullName,
            ["position"] = new JsonObject { ["x"] = _npc.Center.X, ["y"] = _npc.Center.Y },
            ["velocity"] = new JsonObject { ["x"] = _npc.velocity.X, ["y"] = _npc.velocity.Y },
            ["tile_position"] = new JsonObject { ["x"] = (int)(_npc.Center.X / 16), ["y"] = (int)(_npc.Center.Y / 16) },
            ["direction"] = _npc.direction == 1 ? "right" : "left",
            ["town_npc"] = _npc.townNPC,
        };
    }

    public JsonArray ToSymbolicValues() => new()
    {
        _npc.whoAmI,
        _npc.FullName,
        new JsonArray(_npc.Center.X, _npc.Center.Y),
        new JsonArray(_npc.velocity.X, _npc.velocity.Y),
        new JsonArray((int)(_npc.Center.X / 16), (int)(_npc.Center.Y / 16)),
        _npc.direction == 1 ? "r" : "l",
        _npc.townNPC ? 1 : 0,
    };
}

/// <summary>Symbolic provider for NPC combat-relevant stats.</summary>
public sealed class NpcLifeComponent : ISymbolicContextProvider
{
    private readonly NPC _npc;

    public NpcLifeComponent(NPC npc) => _npc = npc;

    public string Symbol => "life";
    public string Description => "NPC life and defense";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("hp", "current life"),
        new SymbolicField("maxhp", "maximum life"),
        new SymbolicField("def", "defense"),
    };

    public void AddContext(JsonObject target)
    {
        target["npc_life"] = new JsonObject
        {
            ["life"] = _npc.life,
            ["life_max"] = _npc.lifeMax,
            ["defense"] = _npc.defense,
        };
    }

    public JsonArray ToSymbolicValues() => new() { _npc.life, _npc.lifeMax, _npc.defense };
}

/// <summary>Symbolic provider for town-NPC housing state.</summary>
public sealed class NpcHomeComponent : ISymbolicContextProvider
{
    private readonly NPC _npc;

    public NpcHomeComponent(NPC npc) => _npc = npc;

    public string Symbol => "home";
    public string Description => "Town NPC home state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("tile", "[x,y] home tile"),
        new SymbolicField("homeless", "0=false, 1=true"),
    };

    public void AddContext(JsonObject target)
    {
        target["npc_home"] = new JsonObject
        {
            ["tile"] = new JsonObject { ["x"] = _npc.homeTileX, ["y"] = _npc.homeTileY },
            ["homeless"] = _npc.homeless,
        };
    }

    public JsonArray ToSymbolicValues() => new()
    {
        new JsonArray(_npc.homeTileX, _npc.homeTileY),
        _npc.homeless ? 1 : 0,
    };
}

/// <summary>Symbolic provider for day/night and time information.</summary>
public sealed class WorldTimeComponent : ISymbolicContextProvider
{
    public string Symbol => "time";
    public string Description => "World time state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("hour", "world time hour"),
        new SymbolicField("day", "0=false, 1=true"),
    };

    public void AddContext(JsonObject target)
    {
        target["world_time"] = new JsonObject
        {
            ["hour"] = Main.time / 3600.0,
            ["is_day"] = Main.dayTime,
        };
    }

    public JsonArray ToSymbolicValues() => new() { Main.time / 3600.0, Main.dayTime ? 1 : 0 };
}

/// <summary>Symbolic provider for moon phase and blood moon state.</summary>
public sealed class WorldMoonComponent : ISymbolicContextProvider
{
    public string Symbol => "moon";
    public string Description => "Moon and blood moon state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("phase", "moon phase integer"),
        new SymbolicField("blood", "0=false, 1=true"),
    };

    public void AddContext(JsonObject target)
    {
        target["world_moon"] = new JsonObject
        {
            ["phase"] = Main.moonPhase,
            ["blood_moon"] = Main.bloodMoon,
        };
    }

    public JsonArray ToSymbolicValues() => new() { Main.moonPhase, Main.bloodMoon ? 1 : 0 };
}

/// <summary>Symbolic provider for broad world progression flags.</summary>
public sealed class WorldProgressionComponent : ISymbolicContextProvider
{
    public string Symbol => "prog";
    public string Description => "World progression state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("hardmode", "0=false, 1=true"),
    };

    public void AddContext(JsonObject target)
    {
        target["world_progression"] = new JsonObject { ["hardmode"] = Main.hardMode };
    }

    public JsonArray ToSymbolicValues() => new() { Main.hardMode ? 1 : 0 };
}

/// <summary>Symbolic provider for filtered NPCs within a radius of a world position.</summary>
public sealed class NearbyNpcsComponent : ISymbolicContextProvider
{
    private readonly Vector2 _center;
    private readonly float _radius;
    private readonly int _max;
    private readonly Func<NPC, bool> _predicate;

    public NearbyNpcsComponent(
        Vector2 center,
        float radius,
        int max,
        Func<NPC, bool> predicate,
        string symbol,
        string description)
    {
        _center = center;
        _radius = radius;
        _max = max;
        _predicate = predicate;
        Symbol = symbol;
        Description = description;
    }

    public string Symbol { get; }
    public string Description { get; }
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("count", "matching NPC count in radius"),
        new SymbolicField("nearest", "nearest matching NPC type/name"),
        new SymbolicField("dist", "nearest matching NPC distance in pixels"),
    };

    public void AddContext(JsonObject target)
    {
        var npcs = BuildNpcList();
        target[$"nearby_{Symbol}"] = new JsonObject
        {
            ["radius"] = _radius,
            ["count"] = npcs.Count,
            ["nearby"] = npcs,
        };
    }

    public JsonArray ToSymbolicValues()
    {
        var npcs = BuildNpcList();
        if (npcs.Count == 0)
            return new JsonArray { 0, "", 0 };

        var nearest = npcs[0] as JsonObject;
        return new JsonArray
        {
            npcs.Count,
            nearest?["type"]?.GetValue<string>() ?? "",
            nearest?["distance"]?.GetValue<double>() ?? 0,
        };
    }

    private JsonArray BuildNpcList()
    {
        var result = new List<(double Distance, JsonObject Data)>();
        float radiusSq = _radius * _radius;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var npc = Main.npc[i];
            if (npc == null || !npc.active || !_predicate(npc))
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
        for (int i = 0; i < result.Count && i < _max; i++)
            array.Add(result[i].Data);
        return array;
    }
}
