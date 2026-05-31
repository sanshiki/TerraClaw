using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;

namespace TerraClaw.LLM;

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

