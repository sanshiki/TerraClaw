using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;

namespace TerraClaw.LLM;

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

