using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
namespace TerraClaw.Observation;

public class EntityExtractor
{
    private const float MaxEntityDistance = 2000f; // pixels

    public object Extract(Player player)
    {
        var npcs = new List<object>();
        var items = new List<object>();
        var projectiles = new List<object>();
        var players = new List<object>();

        var playerCenter = player.Center;

        // Extract NPCs
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var npc = Main.npc[i];
            if (!npc.active) continue;

            var dist = Vector2.Distance(playerCenter, npc.Center);
            if (dist > MaxEntityDistance) continue;

            npcs.Add(new
            {
                id = npc.whoAmI,
                type = npc.FullName,
                position = new { x = (double)npc.position.X, y = (double)npc.position.Y },
                health = new { current = npc.life, max = npc.lifeMax },
                is_hostile = !npc.friendly && npc.damage > 0,
                is_boss = npc.boss,
                ai_state = npc.aiStyle.ToString(),
                distance_to_player = (double)dist,
            });
        }

        // Extract items on ground
        for (int i = 0; i < Main.maxItems; i++)
        {
            var item = Main.item[i];
            if (!item.active || item.IsAir) continue;

            var dist = Vector2.Distance(playerCenter, item.Center);
            if (dist > MaxEntityDistance) continue;

            items.Add(new
            {
                id = item.whoAmI,
                type = item.Name,
                stack = item.stack,
                position = new { x = (double)item.position.X, y = (double)item.position.Y },
                distance_to_player = (double)dist,
            });
        }

        // Extract projectiles
        for (int i = 0; i < Main.maxProjectiles; i++)
        {
            var proj = Main.projectile[i];
            if (!proj.active) continue;

            var dist = Vector2.Distance(playerCenter, proj.Center);
            if (dist > MaxEntityDistance) continue;

            projectiles.Add(new
            {
                type = proj.Name,
                position = new { x = (double)proj.position.X, y = (double)proj.position.Y },
                velocity = new { x = (double)proj.velocity.X, y = (double)proj.velocity.Y },
                is_hostile = proj.hostile,
                damage = proj.damage,
            });
        }

        // Extract other players (multiplayer)
        for (int i = 0; i < Main.maxPlayers; i++)
        {
            var other = Main.player[i];
            if (!other.active || other.whoAmI == player.whoAmI) continue;

            var dist = Vector2.Distance(playerCenter, other.Center);
            if (dist > MaxEntityDistance) continue;

            players.Add(new
            {
                index = other.whoAmI,
                name = other.name,
                position = new { x = (double)other.position.X, y = (double)other.position.Y },
            });
        }

        return new { npcs, items_on_ground = items, projectiles, players };
    }

    /// <summary>
    /// Extract entities around any world position, optionally excluding a specific NPC index.
    /// </summary>
    public object Extract(Vector2 center, int excludeNpcIndex)
    {
        var npcs = new List<object>();
        var items = new List<object>();
        var projectiles = new List<object>();

        // Extract NPCs
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var npc = Main.npc[i];
            if (!npc.active) continue;
            if (i == excludeNpcIndex) continue;

            var dist = Vector2.Distance(center, npc.Center);
            if (dist > MaxEntityDistance) continue;

            npcs.Add(new
            {
                id = npc.whoAmI,
                type = npc.FullName,
                position = new { x = (double)npc.position.X, y = (double)npc.position.Y },
                health = new { current = npc.life, max = npc.lifeMax },
                is_hostile = !npc.friendly && npc.damage > 0,
                is_boss = npc.boss,
                ai_state = npc.aiStyle.ToString(),
                distance_to_center = (double)dist,
            });
        }

        // Extract items on ground
        for (int i = 0; i < Main.maxItems; i++)
        {
            var item = Main.item[i];
            if (!item.active || item.IsAir) continue;

            var dist = Vector2.Distance(center, item.Center);
            if (dist > MaxEntityDistance) continue;

            items.Add(new
            {
                id = item.whoAmI,
                type = item.Name,
                stack = item.stack,
                position = new { x = (double)item.position.X, y = (double)item.position.Y },
                distance_to_center = (double)dist,
            });
        }

        // Extract projectiles
        for (int i = 0; i < Main.maxProjectiles; i++)
        {
            var proj = Main.projectile[i];
            if (!proj.active) continue;

            var dist = Vector2.Distance(center, proj.Center);
            if (dist > MaxEntityDistance) continue;

            projectiles.Add(new
            {
                type = proj.Name,
                position = new { x = (double)proj.position.X, y = (double)proj.position.Y },
                velocity = new { x = (double)proj.velocity.X, y = (double)proj.velocity.Y },
                is_hostile = proj.hostile,
                damage = proj.damage,
            });
        }

        return new { npcs, items_on_ground = items, projectiles };
    }
}
