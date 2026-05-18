using System;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent.Events;
using Terraria.ID;
using TerraClaw.Network;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;
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
            world = ExtractWorldState(npc.Center),
            spatial_window = _spatialWindow.Extract(npc.Center, _spatialRadius),
            entities = _entityExtractor.Extract(npc.Center, npc.whoAmI),
        };

        return MessageSerializer.BuildMessage("agent.observation", observation);
    }

    private object ExtractWorldState(Vector2 center)
    {
        return new
        {
            time = new
            {
                hour = Main.time / 3600.0,
                minute = (Main.time % 3600) / 60.0,
                is_day = Main.dayTime,
            },
            // weather = GetWeather(),
            biome = GetBiomeAt(center),
            active_events = GetActiveEvents(),
            bosses_alive = GetBossesAlive(),
            invasion = GetInvasion(),
            hardmode = Main.hardMode,
            depth_layer = GetDepthLayer(center),
        };
    }

    private static string GetBiomeAt(Vector2 center)
    {
        int tx = (int)(center.X / 16);
        int ty = (int)(center.Y / 16);

        // Check tile types at position for biome detection
        if (tx >= 0 && tx < Main.maxTilesX && ty >= 0 && ty < Main.maxTilesY)
        {
            var tile = Main.tile[tx, ty];
            if (tile != null && tile.HasTile)
            {
                int t = tile.TileType;
                if (t == TileID.Sand || t == TileID.HardenedSand || t == TileID.Sandstone)
                    return "desert";
                if (t == TileID.SnowBlock || t == TileID.IceBlock)
                    return "snow";
                if (t == TileID.Mud || t == TileID.JungleGrass)
                    return "jungle";
                if (t == TileID.Grass || t == TileID.HallowedGrass)
                    return "surface";
            }
        }

        if (ty > Main.maxTilesY - 200) return "underworld";
        if (ty >= Main.rockLayer) return "underground";
        if (ty < Main.worldSurface * 0.2) return "sky";
        return "forest";
    }

    private static string GetDepthLayer(Vector2 center)
    {
        int ty = (int)(center.Y / 16);
        if (ty > Main.maxTilesY - 200) return "underworld";
        if (ty >= Main.rockLayer) return "cavern";
        if (ty >= Main.worldSurface) return "underground";
        if (ty < Main.worldSurface * 0.2) return "sky";
        return "surface";
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
