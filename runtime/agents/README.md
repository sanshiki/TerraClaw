# Creating a Custom Agent

Each agent is a self-contained directory under `agents/<name>/`. To add a new agent, create its directory with the required files, then set `agent: "<name>"` in `config/config.yaml`.

## Directory Structure

```
runtime/
├── config/
│   ├── config.yaml          # Global config — set agent: here
│   ├── config.yaml.example
│   └── system.md            # Shared system prompt (coordinate system, etc.)
└── agents/
    ├── README.md
    └── <your-agent-name>/
        ├── identity.md      # Agent personality, capabilities, rules (optional)
        ├── actions.yaml     # Action definitions (tool schemas)
        └── skill/           # Lua skills (optional)
            ├── explore.lua
            └── ...
```

## Required Files

### `actions.yaml`

Defines the tools/actions your agent can perform. Each entry has a name, description, JSON Schema parameters, and the bridge action type it maps to:

```yaml
move_to:
  description: "Fly to a world coordinate (pixels)"
  enabled: true
  bridge_action: "move_to"
  parameters:
    type: object
    properties:
      x:
        type: number
        description: "Target X (pixels)"
      y:
        type: number
        description: "Target Y (pixels)"
      speed:
        type: number
        description: "Movement speed (px/tick)"
    required: [x, y]

talk:
  description: "Say something — prints chat text above your head"
  enabled: true
  bridge_action: "talk"
  parameters:
    type: object
    properties:
      text:
        type: string
        maxLength: 80
        description: "The text to say (max 80 characters)"
    required: [text]
```

All actions must be implemented on the C# side in `AI/BridgeAgent.cs` under the `ExecuteAction` switch statement. See `AI/BridgeAgent.cs` for existing action implementations.

## Optional Files

### `identity.md`

Appended to the shared system prompt to define your agent's personality, rules, and behavior. Example from the default terraclaw agent:

```markdown
## YOUR IDENTITY

You are a TerraClaw NPC assistant inside Terraria controlled by the player.
You float through the world with no collision or gravity...

## CAPABILITIES

You have access to tools that let you:
- Fly to any world coordinate (move_to)
- Break tiles and walls (break_tile, break_wall)
- Place tiles and walls (place_tile, place_wall)
- Wait / idle (wait)
- Talk (talk)

## IMPORTANT RULES
...
```

If no `identity.md` exists, only the shared `config/system.md` is used.

### `skill/` directory

Contains Lua scripts for multi-step skills. Loaded by `SkillRegistry` at startup. Each `.lua` file should implement a skill function that the agent can invoke.

## How to Add a New Agent

1. **Copy the default agent** as a starting point:
   ```bash
   cp -r agents/terraclaw agents/my-custom-agent
   ```

2. **Edit `agents/my-custom-agent/identity.md`** — change the personality, rules, and behavior description.

3. **Edit `agents/my-custom-agent/actions.yaml`** — add/remove actions as needed. Each action added here must also have a corresponding `case` in `AI/BridgeAgent.cs`.

4. **(Optional) Add Lua skills** — place `.lua` files in `agents/my-custom-agent/skill/`.

5. **Set the agent in config** — edit `config/config.yaml`:
   ```yaml
   agent: "my-custom-agent"
   ```

6. **Restart the runtime**. The orchestrator loads actions, identity, and skills from `agents/my-custom-agent/`.

## C# Side: Adding New Actions

Actions in `actions.yaml` need a handler in `AI/BridgeAgent.cs`:

```csharp
private AgentActionResult ExecuteMyAction(PendingAgentAction action)
{
    int param = (int)action.GetParam("param_name", 0.0);
    // ... implement logic ...
    return AgentActionResult.Done(new { result = "ok" });
}
```

Then add a `case` in the `ExecuteAction` switch:
```csharp
case "my_action":
    return ExecuteMyAction(action);
```

## How It All Connects

```
config/config.yaml → agent: "my-agent"
       ↓
RuntimeConfig.get_actions_path() → agents/my-agent/actions.yaml
RuntimeConfig.get_identity_path() → agents/my-agent/identity.md
RuntimeConfig.get_skills_dir() → agents/my-agent/skill/
       ↓
register_agent(agent_name="my-agent") → sent to C# via WebSocket
       ↓
BridgeAgent.AgentName = "my-agent"  → NPC.GivenName = "my-agent"
```
