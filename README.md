# TerraClaw

**Embodied AI runtime for Terraria** — connect an LLM agent to Terraria via tModLoader.

The LLM handles high-level planning and reasoning. Low-level realtime control is executed locally by skills, state machines, pathfinding, and hardcoded executors. Inspired by [Voyager](https://github.com/MineDojo/Voyager), [OSWorld](https://github.com/xlang-ai/OSWorld), and Claude computer-use agents.

## Architecture

```
[LLM Agent Runtime (Python)]
        ↕  WebSocket / JSON
[tModLoader Bridge Mod (C#)]
        ↕
[Terraria Game]
```

The runtime runs in Python (locally or Docker). The bridge mod runs inside Terraria as a tModLoader mod. Communication is over WebSocket on `ws://127.0.0.1:9777/bridge`.

Two control modes are supported:
- **Player control** — the LLM controls the player character via simulated inputs
- **NPC agent control** — the LLM controls a floating, no-clip NPC agent with direct world manipulation primitives (move, place/break tiles and walls)

## Project Structure

```
terraclaw/
├── TerraClaw.cs                  # Mod entry point
├── Core/                         # BridgeModSystem, BridgeConfig, BridgeModPlayer
├── Network/                      # WebSocket server, message serializer, connection mgmt
├── Observation/                  # Game state extraction (player, world, spatial, entities, UI)
├── Action/                       # Action queue + executors (player control)
├── AI/                           # NPC agent system
│   ├── TerraClawAgent.cs         # Abstract base class (bind to NPC, action primitives)
│   ├── BridgeAgent.cs            # LLM-driven agent (WebSocket → action queue → primitives)
│   ├── ExampleTerraClawAgent.cs  # Hardcoded demo agent (random movement + tile actions)
│   └── AgentNPC.cs               # ModNPC host for agents (immortal, no-clip)
├── Items/                        # Debug spawner item
├── Event/                        # Event bus (40+ event types)
├── Skill/                        # Built-in C# skills
├── Util/                         # A* pathfinding, nav mesh, geometry
├── protocol/schemas/             # JSON Schema for all messages
├── runtime/                      # Python agent runtime
│   └── src/terraclaw_runtime/
│       ├── bridge/               # WebSocket client, message types
│       ├── agent/                # LLM client, tool registry, agent loop
│       ├── memory/               # Spatial, episodic, working memory
│       ├── skills/               # Skill registry + 4 library skills
│       ├── recovery/             # Stuck/loop/health/timeout monitors
│       ├── replay/               # Session recorder (SQLite)
│       └── sandbox/              # Docker sandbox for code generation
├── docker/                       # Dockerfiles + compose
├── config/                       # Example config (YAML)
└── tools/                        # Debug WebSocket client
```

## Quick Start

### Prerequisites

- **Terraria** with **[tModLoader](https://github.com/tModLoader/tModLoader)** (1.4.4+)
- **.NET 8 SDK** (to build the mod)
- **Python 3.12+** (for the runtime)
- **Anthropic API key** or **OpenAI API key**

### 1. Build the Bridge Mod

Build + Reload inside tModLoader, or:

```bash
dotnet build TerraClaw.csproj -c Release
```

Copy the built `.tmod` to your tModLoader `Mods/` folder.

### 2. Install Python Dependencies

```bash
cd runtime
pip install -e .
```

### 3. Configure

```bash
cp config/config.example.yaml config/config.yaml
# Edit config.yaml or set environment variables:
export ANTHROPIC_API_KEY="sk-ant-..."
export BRIDGE_URL="ws://127.0.0.1:9777/bridge"
```

### 4. Launch

Terminal 1 — Terraria with the mod loaded. The bridge starts a WebSocket server on port 9777 automatically.

Terminal 2:
```bash
cd runtime
python -m terraclaw_runtime.orchestrator
```

### Docker (Alternative)

```bash
docker compose -f docker/docker-compose.yml up
```

## NPC Agent Control

As an alternative to player control, the mod supports **NPC-based agents** that the LLM controls directly.

### Spawning an Agent

**Via debug item:** Use the `Agent Spawner` item (added by the mod) to spawn an `ExampleTerraClawAgent` at the cursor. This agent floats through walls and randomly places/breaks tiles — useful for testing without a running LLM.

**Via WebSocket:** The Python runtime sends `agent.register` to spawn a `BridgeAgent` connected to the LLM:

```json
{"type": "agent.register", "payload": {"position": {"x": 1000, "y": 500}}}
```

### Agent Actions

| Action | Params | Behavior |
|--------|--------|----------|
| `move_to` | `x, y, speed (default 4), arrival_radius (default 16)` | Move toward world coordinates |
| `place_tile` | `tx, ty, tile_type (int), style (default 0)` | Place a tile |
| `place_wall` | `tx, ty, wall_type (int)` | Place a wall |
| `break_tile` | `tx, ty` | Break a tile |
| `break_wall` | `tx, ty` | Break a wall |
| `teleport` | `x, y` | Instantly move to position |
| `wait` | `duration_ms (default 1000)` | Idle for N milliseconds |

### Agent Protocol

Additional message types for NPC agent control:

| Direction | Type | Purpose |
|-----------|------|---------|
| Runtime → Game | `agent.register` | Spawn an agent NPC |
| Runtime → Game | `agent.action` | Execute an action |
| Runtime → Game | `agent.action.cancel` | Cancel pending action |
| Runtime → Game | `agent.unregister` | Despawn agent |
| Game → Runtime | `agent.registered` | Confirm spawn (agent_id, entity_index) |
| Game → Runtime | `agent.observation` | Periodic NPC-centric observation |
| Game → Runtime | `agent.action.queued` | Action accepted |
| Game → Runtime | `agent.action.result` | Action completed/failed |

### Creating Custom Agents

Subclass `TerraClawAgent` and override `Initialize()` and `AI()`:

```csharp
public class MyAgent : TerraClawAgent
{
    public override void Initialize()
    {
        NPC.noTileCollide = true;
        NPC.noGravity = true;
    }

    public override void AI()
    {
        // Your behavior here — use MoveToward(), PlaceTile(), BreakTile(), etc.
    }
}
```

Bind the agent to an NPC via `AgentNPC.Spawn()` or by setting the `Agent` property.

## How It Works

### Observation → Think → Act Loop

```
1. Bridge mod extracts game state every N ticks (configurable, default 6 ticks)
2. Observation sent over WebSocket as JSON
3. Runtime updates memory (spatial map, episodic log, working context)
4. Recovery monitors check for stuck/loop/health anomalies
5. If conditions warrant, LLM turn is triggered:
   a. Prompt assembled: system prompt + current obs + relevant memories + plan
   b. LLM responds with text analysis + tool calls
   c. Tool calls are dispatched: skills run locally, actions sent to game
   d. Results are fed back; LLM may continue with follow-up calls
6. Actions execute in-game at 60 Hz without further LLM involvement
```

### LLM Not in the Hot Path

The LLM is called only when:
- A new goal/plan is needed
- The current plan step fails or times out
- A significant event occurs (damage, death, discovery, boss spawn)
- A periodic replanning interval elapses (default: every 5 seconds)

Skills like `navigate_to` or `mine_vein` run entirely locally once started — zero LLM latency.

### Observation Design (Token Budget)

Observations use a **hierarchical + spatial windowing** strategy:
- `minimal`: Player state only (position, HP, mana)
- `normal`: + world state, nearby entities
- `detailed`: + compressed spatial window (30-tile radius default)
- `diagnostic`: everything for debugging

Tile data is **run-length encoded** and Base64 packed. Only "interesting" tiles (ores, chests, doors, pressure plates) are labeled as structured data.

### Recovery System

Five severity levels, with automatic escalation:

| Level | Trigger | Response |
|-------|---------|----------|
| 1 RETRY | Minor action failure | Retry with slight variation |
| 2 INTERRUPT | Repeated failures, stuck | Cancel current action, try alternative |
| 3 ESCAPE | Low HP in combat | Clear queue, flee, heal |
| 4 REPLAN | Plan unsalvageable | Full replanning by LLM |
| 5 EMERGENCY | Death, critical danger | Teleport home, heal, wait |

Monitors: stuck detection (displacement < threshold), loop detection (repeated action patterns), health monitor (HP ratio + enemy proximity), timeout monitor.

## Available Tools (LLM-callable, Player Control)

| Tool | Description |
|------|-------------|
| `move` | Move left/right for a duration |
| `jump` | Jump, optionally while moving |
| `mine_tile` | Mine a tile at coordinates |
| `place_tile` | Place a tile/wall |
| `use_item` | Use a held item |
| `interact` | Talk to NPCs, open chests, pull levers |
| `select_item` | Switch hotbar slot |
| `inventory_operation` | Drop, equip, use items |
| `use_consumable` | Drink potions, eat food |
| `craft` | Craft items at stations |
| `navigate_to` | A* pathfinding to a destination |
| `combat` | Fight enemies (melee/ranged/flee) |
| `query_game` | Query tile info, distances |
| `wait` | Pause for a duration |

## Available Skills

| Skill | Description |
|-------|-------------|
| `navigate` | Navigate to a tile using pathfinding |
| `mine_vein` | Mine an ore vein (navigate + mine connected tiles) |
| `fight_enemy` | Combat with automatic low-HP flee |
| `explore` | Systematic exploration in a direction |

## Configuration Reference

See `config/config.example.yaml` for all options. Key settings:

| Setting | Default | Description |
|---------|---------|-------------|
| `bridge.url` | `ws://127.0.0.1:9777/bridge` | WebSocket URL |
| `bridge.shared_secret` | `terraclaw-dev` | Authentication secret |
| `llm.provider` | `anthropic` | `anthropic` or `openai` |
| `llm.model` | `claude-sonnet-4-6` | Model ID |
| `llm.temperature` | `0.3` | LLM temperature |
| `tick_rate_hz` | `10.0` | Runtime loop frequency |
| `llm_call_interval_s` | `5.0` | Minimum seconds between LLM calls |
| `recovery.stuck_movement_threshold_px` | `30.0` | Pixels moved in window to not be "stuck" |
| `recovery.health_ratio_threshold` | `0.3` | HP ratio that triggers recovery |

## Protocol

All messages follow a standard envelope:

```json
{
  "id": "uuid-v4",
  "type": "observation.state",
  "timestamp": 1715700000000,
  "session_id": "uuid",
  "payload": { ... }
}
```

Full JSON schemas are in `protocol/schemas/`. The protocol supports:
- `handshake` / `handshake_ok` — authentication
- `observation.state` — periodic game state
- `observation.event` — async event stream
- `action.execute` / `action.result` / `action.progress` — player action lifecycle
- `action.cancel` / `action.queue` — action management
- `agent.register` / `agent.observation` / `agent.action` / `agent.action.result` — NPC agent control
- `event.subscribe` / `event.unsubscribe` — event filtering
- `skill.execute` / `skill.result` — skill lifecycle
- `ping` / `pong` — heartbeat
- `error` — error responses

## Quick Test (No LLM Required)

```bash
# Test connection
python tools/debug_client/debug_client.py ping

# Move the player right
python tools/debug_client/debug_client.py move right

# Request an observation
python tools/debug_client/debug_client.py observe
```

In-game, use the `Agent Spawner` item to spawn a demo NPC agent that floats around and interacts with the world.

## Roadmap

### Done
- [x] WebSocket bridge between Python and Terraria
- [x] Full observation pipeline (player, world, spatial, entities, inventory, UI)
- [x] 14 action executors with priority queue
- [x] NPC agent system (TerraClawAgent base, BridgeAgent LLM control, ExampleTerraClawAgent)
- [x] Agent protocol (register, observation, action, result)
- [x] LLM agent loop (observe → think → act)
- [x] Memory system (spatial, episodic, working)
- [x] 4 skills (navigate, mine_vein, fight_enemy, explore)
- [x] Recovery system with 4 monitors
- [x] Replay recording (SQLite)
- [x] Docker sandbox + code validator
- [x] Docker Compose deployment

### Next
- [ ] Complete built-in C# skills (MineVeinSkill, FightEnemySkill, etc.)
- [ ] Observation differ (delta-only updates)
- [ ] Agent spatial memory in Python runtime
- [ ] Web dashboard for real-time monitoring
- [ ] Advanced navigation (liquids, teleporters, minecarts)

### Future
- [ ] Multi-agent support (multiple AI-controlled NPCs in one world)
- [ ] RL-based skills (trained for combat, platforming)
- [ ] Skill marketplace / community contributions
- [ ] Cross-world memory transfer
- [ ] General game protocol (other games beyond Terraria)
- [ ] Visual grounding (screenshot analysis)

## Development

```bash
# Lint Python
cd runtime && ruff check src/

# Type check
cd runtime && mypy src/

# Run tests
cd runtime && pytest
```

## License

MIT
