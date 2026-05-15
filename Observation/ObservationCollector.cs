using System;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent.Events;
using Terraria.ID;
using TerraClaw.Network;

namespace TerraClaw.Observation;

public class ObservationCollector
{
    private readonly Core.BridgeConfig _config;
    private readonly SpatialWindowExtractor _spatialWindow;
    private readonly EntityExtractor _entityExtractor;

    private int _spatialRadius;

    public ObservationCollector(Core.BridgeConfig config, Event.EventBus eventBus)
    {
        _config = config;
        _spatialWindow = new SpatialWindowExtractor();
        _entityExtractor = new EntityExtractor();
        _spatialRadius = config.SpatialWindowRadius;
    }

    public string BuildAgentObservation(NPC npc, string agentId, int tick)
    {
        var observation = new
        {
            agent_id = agentId,
            sequence = tick / _config.TickSyncIntervalTicks,
            tick,
            timestamp_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            agent = new
            {
                position = new { x = npc.Center.X, y = npc.Center.Y },
                velocity = new { x = npc.velocity.X, y = npc.velocity.Y },
                tile_position = new { x = (int)(npc.Center.X / 16), y = (int)(npc.Center.Y / 16) },
                direction = npc.direction == 1 ? "right" : "left",
            },
            world = ExtractWorldState(),
            spatial_window = _spatialWindow.Extract(npc.Center, _spatialRadius),
            entities = _entityExtractor.Extract(npc.Center, npc.whoAmI),
        };

        return MessageSerializer.BuildMessage("agent.observation", observation);
    }

    private object ExtractWorldState()
    {
        return new
        {
            time = new
            {
                hour = Main.time / 3600.0,
                minute = (Main.time % 3600) / 60.0,
                is_day = Main.dayTime,
            },
            weather = GetWeather(),
            biome = GetPlayerBiome(),
            nearby_biomes = GetNearbyBiomes(),
            active_events = GetActiveEvents(),
            bosses_alive = GetBossesAlive(),
            invasion = GetInvasion(),
            hardmode = Main.hardMode,
            evil_type = WorldGen.crimson ? "crimson" : "corruption",
        };
    }

    private static string GetWeather()
    {
        if (Main.slimeRain) return "slime_rain";
        if (Sandstorm.Happening) return "sandstorm";
        if (Main.raining && Main.LocalPlayer.ZoneSnow) return "blizzard";
        if (Main.raining) return "rain";
        return "clear";
    }

    private static string GetPlayerBiome()
    {
        var player = Main.LocalPlayer;
        return player.ZoneCorrupt ? "corruption" :
               player.ZoneCrimson ? "crimson" :
               player.ZoneHallow ? "hallow" :
               player.ZoneDesert ? "desert" :
               player.ZoneSnow ? "snow" :
               player.ZoneJungle ? "jungle" :
               player.ZoneDungeon ? "dungeon" :
               player.ZoneBeach ? "ocean" :
               player.ZoneGlowshroom ? "mushroom" :
               player.ZoneUnderworldHeight ? "underworld" :
               player.ZoneSkyHeight ? "sky" :
               player.ZoneRockLayerHeight ? "underground" :
               "forest";
    }

    private static List<string> GetNearbyBiomes()
    {
        var biomes = new List<string>();
        var player = Main.LocalPlayer;
        if (player.ZoneCorrupt) biomes.Add("corruption");
        if (player.ZoneCrimson) biomes.Add("crimson");
        if (player.ZoneHallow) biomes.Add("hallow");
        if (player.ZoneDesert) biomes.Add("desert");
        if (player.ZoneSnow) biomes.Add("snow");
        if (player.ZoneJungle) biomes.Add("jungle");
        return biomes;
    }

    private static List<string> GetActiveEvents()
    {
        var events = new List<string>();
        if (Main.bloodMoon) events.Add("blood_moon");
        if (Main.eclipse) events.Add("eclipse");
        if (Main.pumpkinMoon) events.Add("pumpkin_moon");
        if (Main.snowMoon) events.Add("frost_moon");
        if (Main.invasionType > 0) events.Add("invasion");
        return events;
    }

    private static List<string> GetBossesAlive()
    {
        var bosses = new List<string>();
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var npc = Main.npc[i];
            if (npc.active && (npc.boss || npc.type == NPCID.EaterofWorldsHead
                || npc.type == NPCID.EaterofWorldsBody || npc.type == NPCID.EaterofWorldsTail))
                bosses.Add(npc.FullName);
        }
        return bosses;
    }

    private static string? GetInvasion()
    {
        if (Main.invasionType <= 0) return null;
        return Main.invasionType switch
        {
            1 => "goblin_army",
            2 => "frost_legion",
            3 => "pirate_invasion",
            4 => "martian_madness",
            _ => "unknown"
        };
    }
}
