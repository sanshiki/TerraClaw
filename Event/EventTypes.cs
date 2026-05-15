using System;
namespace TerraClaw.Event;

public static class EventTypes
{
    // Player
    public const string PlayerDamage = "player.damage";
    public const string PlayerHeal = "player.heal";
    public const string PlayerDeath = "player.death";
    public const string PlayerRespawn = "player.respawn";
    public const string PlayerBuffAdded = "player.buff_added";
    public const string PlayerBuffExpired = "player.buff_expired";
    public const string PlayerItemPickup = "player.item_pickup";
    public const string PlayerInventoryFull = "player.inventory_full";
    public const string PlayerBreathLow = "player.breath_low";
    public const string PlayerEnteringWater = "player.entering_water";
    public const string PlayerExitingWater = "player.exiting_water";
    public const string PlayerTeleported = "player.teleported";

    // World
    public const string WorldTimeChange = "world.time_change";
    public const string WorldWeatherChange = "world.weather_change";
    public const string WorldBiomeChange = "world.biome_change";
    public const string WorldBossSpawn = "world.boss_spawn";
    public const string WorldBossDefeated = "world.boss_defeated";
    public const string WorldInvasionStart = "world.invasion_start";
    public const string WorldInvasionEnd = "world.invasion_end";
    public const string WorldBloodMoonStart = "world.blood_moon_start";
    public const string WorldBloodMoonEnd = "world.blood_moon_end";
    public const string WorldEclipseStart = "world.eclipse_start";
    public const string WorldEclipseEnd = "world.eclipse_end";

    // Entity
    public const string EntityNpcSpawn = "entity.npc_spawn";
    public const string EntityNpcDeath = "entity.npc_death";
    public const string EntityNpcTransformed = "entity.npc_transformed";
    public const string EntityItemDrop = "entity.item_drop";
    public const string EntityProjectileCreated = "entity.projectile_created";
    public const string EntityProjectileDestroyed = "entity.projectile_destroyed";

    // Tile
    public const string TileBreaking = "tile.breaking";
    public const string TileBroken = "tile.broken";
    public const string TilePlaced = "tile.placed";
    public const string TileLiquidChange = "tile.liquid_change";
    public const string TileExplosion = "tile.explosion";

    // UI
    public const string UIOpened = "ui.opened";
    public const string UIClosed = "ui.closed";
    public const string UICraftAvailable = "ui.craft_available";
    public const string UIShopChanged = "ui.shop_changed";
    public const string UIDialogueAdvanced = "ui.dialogue_advanced";

    // System
    public const string SystemConnectionLost = "system.connection_lost";
    public const string SystemConnectionRestored = "system.connection_restored";
    public const string SystemActionCancelled = "system.action_cancelled";
    public const string SystemActionQueued = "system.action_queued";
    public const string SystemError = "system.error";
    public const string SystemWarning = "system.warning";
}
