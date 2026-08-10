using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ModLoader;

namespace TerraClaw.LLM;

/// <summary>Fluent builder for reusable player observation components.</summary>
public sealed class PlayerContextBuilder : ContextBundle
{
    private readonly Player _player;

    internal PlayerContextBuilder(Player player)
    {
        _player = player;
    }

    /// <summary>Adds identity, life, mana, and death state.</summary>
    public PlayerContextBuilder Basic()
    {
        Add(new PlayerBasicComponent(_player));
        return this;
    }

    /// <summary>Adds position, movement, height layer, vanilla biome tags, and active mod biomes.</summary>
    public PlayerContextBuilder Location()
    {
        Add(new PlayerLocationComponent(_player));
        return this;
    }

    /// <summary>Adds active buffs.</summary>
    public PlayerContextBuilder Buffs()
    {
        Add(new PlayerBuffsComponent(_player));
        return this;
    }

    /// <summary>Adds armor and functional accessory slots.</summary>
    public PlayerContextBuilder Equipment()
    {
        Add(new PlayerEquipmentComponent(_player));
        return this;
    }

    /// <summary>Adds all non-empty inventory items using compact item fields.</summary>
    public PlayerContextBuilder Inventory()
    {
        Add(new PlayerInventoryComponent(_player));
        return this;
    }
}

/// <summary>Symbolic provider for core player identity and resources.</summary>
public sealed class PlayerBasicComponent : ISymbolicContextProvider
{
    private readonly Player _player;

    public PlayerBasicComponent(Player player) => _player = player;

    public string Symbol => "player";
    public string Description => "Player identity, life, and mana";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("id", "player whoAmI"),
        new SymbolicField("name", "player name"),
        new SymbolicField("hp", "current life"),
        new SymbolicField("maxhp", "maximum life including bonuses"),
        new SymbolicField("mp", "current mana"),
        new SymbolicField("maxmp", "maximum mana including bonuses"),
        new SymbolicField("dead", "0=false, 1=true"),
    };

    public void AddContext(JsonObject target)
    {
        target["player_basic"] = new JsonObject
        {
            ["id"] = _player.whoAmI,
            ["name"] = _player.name,
            ["life"] = _player.statLife,
            ["life_max"] = _player.statLifeMax2,
            ["mana"] = _player.statMana,
            ["mana_max"] = _player.statManaMax2,
            ["dead"] = _player.dead,
        };
    }

    public JsonArray ToSymbolicValues() => new()
    {
        _player.whoAmI,
        _player.name,
        _player.statLife,
        _player.statLifeMax2,
        _player.statMana,
        _player.statManaMax2,
        _player.dead ? 1 : 0,
    };
}

/// <summary>Symbolic provider for player position, movement, and active biomes.</summary>
public sealed class PlayerLocationComponent : ISymbolicContextProvider
{
    private readonly Player _player;

    public PlayerLocationComponent(Player player) => _player = player;

    public string Symbol => "ploc";
    public string Description => "Player position and active nearby biomes";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("pos", "[x,y] world position in pixels"),
        new SymbolicField("vel", "[x,y] velocity"),
        new SymbolicField("tile", "[x,y] tile position"),
        new SymbolicField("dir", "l=left, r=right"),
        new SymbolicField("height", "active world height layer tags"),
        new SymbolicField("biomes", "active vanilla biome tags"),
        new SymbolicField("mod_biomes", "active mod biomes as mod:name"),
    };

    public void AddContext(JsonObject target)
    {
        target["player_location"] = new JsonObject
        {
            ["position"] = new JsonObject { ["x"] = _player.Center.X, ["y"] = _player.Center.Y },
            ["velocity"] = new JsonObject { ["x"] = _player.velocity.X, ["y"] = _player.velocity.Y },
            ["tile_position"] = new JsonObject { ["x"] = (int)(_player.Center.X / 16), ["y"] = (int)(_player.Center.Y / 16) },
            ["direction"] = _player.direction == 1 ? "right" : "left",
            ["height_layers"] = StringListToJson(BuildHeightLayers(_player)),
            ["biomes"] = StringListToJson(BuildVanillaBiomes(_player)),
            ["mod_biomes"] = StringListToJson(BuildModBiomes(_player)),
        };
    }

    public JsonArray ToSymbolicValues() => new()
    {
        new JsonArray(_player.Center.X, _player.Center.Y),
        new JsonArray(_player.velocity.X, _player.velocity.Y),
        new JsonArray((int)(_player.Center.X / 16), (int)(_player.Center.Y / 16)),
        _player.direction == 1 ? "r" : "l",
        StringListToJson(BuildHeightLayers(_player)),
        StringListToJson(BuildVanillaBiomes(_player)),
        StringListToJson(BuildModBiomes(_player)),
    };

    private static IReadOnlyList<string> BuildHeightLayers(Player player)
    {
        var tags = new List<string>();
        AddIf(tags, player.ZoneSkyHeight, "sky");
        AddIf(tags, player.ZoneOverworldHeight, "surface");
        AddIf(tags, player.ZoneDirtLayerHeight, "underground");
        AddIf(tags, player.ZoneRockLayerHeight, "cavern");
        AddIf(tags, player.ZoneUnderworldHeight, "underworld");
        return tags;
    }

    private static IReadOnlyList<string> BuildVanillaBiomes(Player player)
    {
        var tags = new List<string>();
        AddIf(tags, player.ZoneDungeon, "dungeon");
        AddIf(tags, player.ZoneCorrupt, "corruption");
        AddIf(tags, player.ZoneCrimson, "crimson");
        AddIf(tags, player.ZoneHallow, "hallow");
        AddIf(tags, player.ZoneMeteor, "meteor");
        AddIf(tags, player.ZoneJungle, "jungle");
        AddIf(tags, player.ZoneSnow, "snow");
        AddIf(tags, player.ZoneDesert, "desert");
        AddIf(tags, player.ZoneUndergroundDesert, "underground_desert");
        AddIf(tags, player.ZoneGlowshroom, "glowing_mushroom");
        AddIf(tags, player.ZoneBeach, "beach");
        AddIf(tags, player.ZoneForest, "forest");
        AddIf(tags, player.ZoneGranite, "granite");
        AddIf(tags, player.ZoneMarble, "marble");
        AddIf(tags, player.ZoneHive, "hive");
        AddIf(tags, player.ZoneGemCave, "gem_cave");
        AddIf(tags, player.ZoneLihzhardTemple, "lihzahrd_temple");
        AddIf(tags, player.ZoneGraveyard, "graveyard");
        AddIf(tags, player.ZoneShimmer, "shimmer");
        AddIf(tags, player.ZoneRain, "rain");
        AddIf(tags, player.ZoneSandstorm, "sandstorm");
        return tags;
    }

    private static IReadOnlyList<string> BuildModBiomes(Player player)
    {
        var tags = new List<string>();
        foreach (ModBiome biome in ModContent.GetContent<ModBiome>())
        {
            try
            {
                if (biome.IsBiomeActive(player))
                    tags.Add($"{biome.Mod.Name}:{biome.Name}");
            }
            catch
            {
            }
        }
        return tags;
    }

    private static void AddIf(List<string> tags, bool condition, string tag)
    {
        if (condition)
            tags.Add(tag);
    }

    private static JsonArray StringListToJson(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        return array;
    }
}

/// <summary>Symbolic provider for active player buffs.</summary>
public sealed class PlayerBuffsComponent : ISymbolicContextProvider
{
    private readonly Player _player;

    public PlayerBuffsComponent(Player player) => _player = player;

    public string Symbol => "pbuff";
    public string Description => "Active player buffs";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("count", "active buff count"),
        new SymbolicField("buffs", "active buffs as [slot,type,name,time]"),
    };

    public void AddContext(JsonObject target)
    {
        JsonArray buffs = BuildBuffs(_player, verbose: true);
        target["player_buffs"] = new JsonObject
        {
            ["count"] = buffs.Count,
            ["buffs"] = buffs,
        };
    }

    public JsonArray ToSymbolicValues()
    {
        JsonArray buffs = BuildBuffs(_player, verbose: false);
        return new JsonArray { buffs.Count, buffs };
    }

    private static JsonArray BuildBuffs(Player player, bool verbose)
    {
        var buffs = new JsonArray();
        int count = Math.Min(player.buffType.Length, player.buffTime.Length);
        for (int i = 0; i < count; i++)
        {
            int type = player.buffType[i];
            int time = player.buffTime[i];
            if (type <= 0 || time <= 0)
                continue;

            buffs.Add(verbose
                ? new JsonObject
                {
                    ["slot"] = i,
                    ["type"] = type,
                    ["name"] = BuffName(type),
                    ["time"] = time,
                }
                : new JsonArray(i, type, BuffName(type), time));
        }
        return buffs;
    }

    private static string BuffName(int type)
    {
        try
        {
            string name = Lang.GetBuffName(type);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        return type.ToString();
    }
}

/// <summary>Symbolic provider for player armor and functional accessories.</summary>
public sealed class PlayerEquipmentComponent : ISymbolicContextProvider
{
    private readonly Player _player;

    public PlayerEquipmentComponent(Player player) => _player = player;

    public string Symbol => "peq";
    public string Description => "Player armor and functional accessories";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("armor", "armor items as [slot,type,name,stack,prefix]"),
        new SymbolicField("accessories", "vanilla functional accessory items as [slot,type,name,stack,prefix]"),
        new SymbolicField("mod_accessories", "mod functional accessory items as [slot,type,name,stack,prefix]"),
    };

    public void AddContext(JsonObject target)
    {
        target["player_equipment"] = new JsonObject
        {
            ["armor"] = BuildEquipmentItems(_player, 0, 3, verbose: true),
            ["accessories"] = BuildEquipmentItems(_player, 3, 10, verbose: true),
            ["mod_accessories"] = BuildModAccessoryItems(_player, verbose: true),
        };
    }

    public JsonArray ToSymbolicValues() => new()
    {
        BuildEquipmentItems(_player, 0, 3, verbose: false),
        BuildEquipmentItems(_player, 3, 10, verbose: false),
        BuildModAccessoryItems(_player, verbose: false),
    };

    private static JsonArray BuildEquipmentItems(Player player, int startSlot, int endSlot, bool verbose)
    {
        var items = new JsonArray();
        int end = Math.Min(endSlot, player.armor.Length);
        for (int i = startSlot; i < end; i++)
        {
            Item item = player.armor[i];
            if (item == null || item.IsAir)
                continue;

            items.Add(verbose
                ? PlayerItemJson.ToVerbose(item, i, EquipmentSlotName(i), includeFavorited: false)
                : PlayerItemJson.ToSymbolic(item, i, includeFavorited: false));
        }
        return items;
    }

    private static JsonArray BuildModAccessoryItems(Player player, bool verbose)
    {
        var items = new JsonArray();
        using var currentPlayer = new Main.CurrentPlayerOverride(player);
        foreach (ModAccessorySlot slot in ModContent.GetContent<ModAccessorySlot>())
        {
            try
            {
                Item item = slot.FunctionalItem;
                if (item == null || item.IsAir)
                    continue;

                items.Add(verbose
                    ? PlayerItemJson.ToVerbose(item, slot.Type, slot.FullName, includeFavorited: false)
                    : PlayerItemJson.ToSymbolic(item, slot.Type, includeFavorited: false));
            }
            catch
            {
            }
        }
        return items;
    }

    private static string EquipmentSlotName(int slot) => slot switch
    {
        0 => "head",
        1 => "body",
        2 => "legs",
        >= 3 and <= 9 => $"accessory_{slot - 2}",
        _ => $"slot_{slot}",
    };
}

/// <summary>Symbolic provider for all non-empty player inventory items.</summary>
public sealed class PlayerInventoryComponent : ISymbolicContextProvider
{
    private readonly Player _player;

    public PlayerInventoryComponent(Player player) => _player = player;

    public string Symbol => "pinv";
    public string Description => "Non-empty player inventory items";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[]
    {
        new SymbolicField("count", "non-empty inventory item count"),
        new SymbolicField("items", "items as [slot,type,name,stack,prefix,fav]"),
    };

    public void AddContext(JsonObject target)
    {
        JsonArray items = BuildInventoryItems(_player, verbose: true);
        target["player_inventory"] = new JsonObject
        {
            ["count"] = items.Count,
            ["items"] = items,
        };
    }

    public JsonArray ToSymbolicValues()
    {
        JsonArray items = BuildInventoryItems(_player, verbose: false);
        return new JsonArray { items.Count, items };
    }

    private static JsonArray BuildInventoryItems(Player player, bool verbose)
    {
        var items = new JsonArray();
        for (int i = 0; i < player.inventory.Length; i++)
        {
            Item item = player.inventory[i];
            if (item == null || item.IsAir)
                continue;

            items.Add(verbose
                ? PlayerItemJson.ToVerbose(item, i, InventorySlotName(i), includeFavorited: true)
                : PlayerItemJson.ToSymbolic(item, i, includeFavorited: true));
        }
        return items;
    }

    private static string InventorySlotName(int slot) => slot switch
    {
        >= 0 and <= 9 => $"hotbar_{slot + 1}",
        >= 10 and <= 49 => $"inventory_{slot - 9}",
        >= 50 and <= 53 => $"coin_{slot - 49}",
        >= 54 and <= 57 => $"ammo_{slot - 53}",
        _ => $"slot_{slot}",
    };
}

internal static class PlayerItemJson
{
    public static JsonObject ToVerbose(Item item, int slot, string slotName, bool includeFavorited)
    {
        var result = new JsonObject
        {
            ["slot"] = slot,
            ["slot_name"] = slotName,
            ["type"] = item.type,
            ["name"] = ItemName(item),
            ["stack"] = item.stack,
            ["prefix"] = item.prefix,
        };

        if (includeFavorited)
            result["favorited"] = item.favorited;

        return result;
    }

    public static JsonArray ToSymbolic(Item item, int slot, bool includeFavorited)
    {
        var result = new JsonArray
        {
            slot,
            item.type,
            ItemName(item),
            item.stack,
            item.prefix,
        };

        if (includeFavorited)
            result.Add(item.favorited ? 1 : 0);

        return result;
    }

    private static string ItemName(Item item)
    {
        try
        {
            string name = Lang.GetItemNameValue(item.type);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        return item.type.ToString();
    }
}
