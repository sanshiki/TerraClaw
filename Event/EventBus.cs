using System;
using System.Collections.Generic;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using TerraClaw.Network;

namespace TerraClaw.Event;

public class EventBus
{
    private readonly List<GameEvent> _pendingEvents = new();
    private readonly HashSet<string> _knownEventTypes = new();
    private readonly object _lock = new();

    public EventBus()
    {
        RegisterAllEventTypes();
    }

    private void RegisterAllEventTypes()
    {
        var types = new[]
        {
            // Player events
            "player.damage", "player.heal", "player.death", "player.respawn",
            "player.buff_added", "player.buff_expired", "player.item_pickup",
            "player.inventory_full", "player.breath_low", "player.entering_water",
            "player.exiting_water", "player.teleported",
            // World events
            "world.time_change", "world.weather_change", "world.biome_change",
            "world.boss_spawn", "world.boss_defeated", "world.invasion_start",
            "world.invasion_end", "world.blood_moon_start", "world.blood_moon_end",
            "world.eclipse_start", "world.eclipse_end",
            // Entity events
            "entity.npc_spawn", "entity.npc_death", "entity.npc_transformed",
            "entity.item_drop", "entity.projectile_created", "entity.projectile_destroyed",
            // Tile events
            "tile.breaking", "tile.broken", "tile.placed", "tile.liquid_change",
            "tile.explosion",
            // UI events
            "ui.opened", "ui.closed", "ui.craft_available", "ui.shop_changed",
            "ui.dialogue_advanced",
            // System events
            "system.connection_lost", "system.connection_restored",
            "system.action_cancelled", "system.action_queued", "system.error",
            "system.warning"
        };

        foreach (var t in types)
            _knownEventTypes.Add(t);
    }

    public void Emit(string eventType, object data, int importance = 5, float? posX = null, float? posY = null, int tick = 0)
    {
        var evt = new GameEvent
        {
            EventType = eventType,
            Data = data,
            Importance = importance,
            Tick = tick > 0 ? tick : (int)(Main.GameUpdateCount),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PositionX = posX,
            PositionY = posY,
        };

        lock (_lock)
        {
            _pendingEvents.Add(evt);
        }

        _knownEventTypes.Add(eventType);
    }

    public void Emit(string eventType, int importance = 5, float? posX = null, float? posY = null, int tick = 0)
    {
        Emit(eventType, new { }, importance, posX, posY, tick);
    }

    public void Flush()
    {
        List<GameEvent> events;
        lock (_lock)
        {
            if (_pendingEvents.Count == 0) return;
            events = new List<GameEvent>(_pendingEvents);
            _pendingEvents.Clear();
        }

        var server = Terraria.ModLoader.ModContent.GetInstance<Core.BridgeModSystem>().WebSocketServer;
        foreach (var conn in server.Connections)
        {
            if (!conn.IsAuthenticated || !conn.IsOpen) continue;

            foreach (var evt in events)
            {
                if (!IsSubscribed(conn, evt.EventType)) continue;

                var message = MessageSerializer.BuildMessage("observation.event", new
                {
                    event_type = evt.EventType,
                    tick = evt.Tick,
                    timestamp_ms = evt.TimestampMs,
                    importance = evt.Importance,
                    position = evt.PositionX.HasValue ? new { x = evt.PositionX.Value, y = evt.PositionY!.Value } : null,
                    data = evt.Data,
                }, sessionId: conn.Id);

                conn.OutgoingQueue.Enqueue(message);
            }
        }
    }

    private static bool IsSubscribed(BridgeConnection conn, string eventType)
    {
        foreach (var sub in conn.SubscribedEventTypes)
        {
            // Support wildcard matching: "player.*" matches "player.damage"
            if (sub.EndsWith(".*"))
            {
                var prefix = sub[..^2];
                if (eventType.StartsWith(prefix)) return true;
            }
            else if (sub == eventType)
            {
                return true;
            }
        }
        return false;
    }

    public IReadOnlyCollection<string> GetAllEventTypes() => _knownEventTypes;
}

public class GameEvent
{
    public string EventType { get; init; } = "";
    public object Data { get; init; } = new();
    public int Importance { get; init; } = 5;
    public int Tick { get; init; }
    public long TimestampMs { get; init; }
    public float? PositionX { get; init; }
    public float? PositionY { get; init; }
}
