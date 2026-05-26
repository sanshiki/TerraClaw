using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;
using TerraClaw.LLM;
using TerraClaw.Util;

namespace TerraClaw.AI;

/// <summary>
/// Example agent using the C#-first LLM framework.
/// The LLM returns structured JSON; local C# AI decides how to apply it.
/// </summary>
public class ExampleTerraClawAgent : TerraClawAgent, IPlayerInstructionReceiver
{
    private const float Inertia = 20f;
    private const int HeartbeatCooldownTicks = 60 * 20;
    private const int PlayerInstructionCooldownTicks = 60 * 5;
    private LlmRequestHandle? _llm;
    private Vector2? _moveTarget;
    private string _state = "idle";
    private int _nextRequestTick;
    private int _nextPlayerInstructionTick;
    private readonly Queue<string> _playerInstructions = new();
    private string _activePlayerInstruction = "";

    public ExampleTerraClawAgent(string agentId = "", string connectionId = "", string agentName = "terraclaw")
    {
        if (!string.IsNullOrWhiteSpace(agentId))
            LlmAgentId = agentId;
    }

    public override void Initialize()
    {
        NPC.noTileCollide = true;
        NPC.noGravity = true;
        NPC.damage = 0;
        NPC.friendly = true;
        NPC.life = 9999;
        NPC.lifeMax = 9999;
        NPC.hide = false;
        NPC.chaseable = true;
    }

    public override void AI()
    {
        if (!IsActive)
            return;

        ApplyMovement();
        PollLlm();

        if (_llm == null || _llm.IsDone)
        {
            if (_playerInstructions.Count > 0 && Main.GameUpdateCount >= _nextPlayerInstructionTick)
            {
                _activePlayerInstruction = _playerInstructions.Dequeue();
                _llm = RequestLlm(
                    BuildObservation(),
                    BuildOutputContract(),
                    $"The player sent this /agent instruction: {_activePlayerInstruction}. Respond or act on it.",
                    timeoutMs: 30000);
                _nextPlayerInstructionTick = (int)Main.GameUpdateCount + PlayerInstructionCooldownTicks;
                _nextRequestTick = (int)Main.GameUpdateCount + HeartbeatCooldownTicks;
            }
            else if (Main.GameUpdateCount >= _nextRequestTick)
            {
                _activePlayerInstruction = "";
                _llm = RequestLlm(
                    BuildObservation(),
                    BuildOutputContract(),
                    "Heartbeat check: choose the NPC assistant's next small behavior. Prefer talk for status, move_to for repositioning, or set_state for local state.",
                    timeoutMs: 30000);
                _nextRequestTick = (int)Main.GameUpdateCount + HeartbeatCooldownTicks;
            }
        }
    }

    public void ReceivePlayerInstruction(string playerName, string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return;
        _playerInstructions.Enqueue($"{playerName}: {instruction}");
    }

    private LlmObservation BuildObservation()
    {
        return LlmObservation.Create()
            .With(NpcContexts.Basic(NPC))
            .With(WorldContexts.Basic(NPC.Center))
            .With(WorldContexts.Spatial(NPC.Center, 10))
            .With("state", _state)
            .With("has_move_target", _moveTarget.HasValue)
            .With("active_player_instruction", _activePlayerInstruction)
            .With("queued_player_instruction_count", _playerInstructions.Count);
    }

    private static LlmOutput BuildOutputContract()
    {
        return LlmOutput.OneOf(
            LlmOutput.Object("talk", "Say a short in-game message.")
                .String("text", required: true, maxLength: 80),
            LlmOutput.Object("move_to", "Move toward a world-space target.")
                .Number("x", required: true)
                .Number("y", required: true)
                .Number("speed", defaultValue: 4.0),
            LlmOutput.Object("set_state", "Update the local behavior state.")
                .String("state", required: true, maxLength: 40)
        );
    }

    private void PollLlm()
    {
        if (_llm == null || !_llm.TryGetResult(out JsonNode? output) || output is not JsonObject obj)
            return;

        string type = obj["type"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "talk":
                Say(obj["text"]?.GetValue<string>() ?? "");
                break;
            case "move_to":
                float x = (float)(obj["x"]?.GetValue<double>() ?? NPC.Center.X);
                float y = (float)(obj["y"]?.GetValue<double>() ?? NPC.Center.Y);
                _moveTarget = new Vector2(x, y);
                _state = "moving";
                break;
            case "set_state":
                _state = obj["state"]?.GetValue<string>() ?? _state;
                break;
        }

        _llm = null;
    }

    private void ApplyMovement()
    {
        if (!_moveTarget.HasValue)
            return;

        var target = _moveTarget.Value;
        float dist = Vector2.Distance(NPC.Center, target);
        if (dist <= 16f)
        {
            NPC.velocity = Vector2.Zero;
            _moveTarget = null;
            _state = "idle";
            return;
        }

        NPC.velocity = AIHelper.HomeinToTarget(NPC.Center, NPC.velocity, target, 4f, Inertia);
        NPC.direction = target.X > NPC.Center.X ? 1 : -1;
    }

    private void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (text.Length > 80)
            text = text[..80];
        Main.NewText($"<{NPC.FullName}> {text}", 200, 200, 100);
        CombatText.NewText(NPC.Hitbox, Color.Gold, text);
    }
}
