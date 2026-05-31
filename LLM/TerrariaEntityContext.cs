using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;

namespace TerraClaw.LLM;

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

