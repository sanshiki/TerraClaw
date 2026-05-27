using Microsoft.Xna.Framework;
using System.Text.Json.Nodes;
using Terraria;
using TerraClaw.AI;
using TerraClaw.LLM;

namespace TerraClaw.Agents.TerraClaw;

/// <summary>
/// Minimal spawned NPC example for the C#-first LLM framework.
/// It waits for /agent text, sends one LLM request, then displays the returned text.
/// </summary>
public class ExampleTerraClawAgent : TerraClawAgent, IPlayerInstructionReceiver
{
    // Keep this example intentionally small: one player instruction triggers one LLM response.
    private const int PlayerInstructionCooldownTicks = 60 * 5;
    private const string SystemPrompt =
        "You are an AI controller inside Terraria. Return only JSON matching the requested output contract.\n\n" +
        "Terraria coordinate system:\n" +
        "- World positions are measured in pixels as [x,y].\n" +
        "- Tile positions are integer grid coordinates: tile_x = pixel_x / 16, tile_y = pixel_y / 16.\n" +
        "- X increases to the right.\n" +
        "- Y increases downward.\n" +
        "- This example only supports returning a short message.";

    private LlmRequestHandle? _llm;
    private int _nextPlayerInstructionTick;
    private string _queuedInstruction = "";

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

        PollLlm();

        if (_llm != null && _llm.IsPending)
            return;
        if (string.IsNullOrWhiteSpace(_queuedInstruction))
            return;
        if (Main.GameUpdateCount < _nextPlayerInstructionTick)
            return;

        string instruction = _queuedInstruction;
        _queuedInstruction = "";
        _llm = RequestLlm(
            BuildObservation(instruction),
            BuildOutputContract(),
            $"The player sent this /agent instruction: {instruction}. Reply with one short talk output.",
            system: SystemPrompt,
            timeoutMs: 30000);
        _nextPlayerInstructionTick = (int)Main.GameUpdateCount + PlayerInstructionCooldownTicks;
    }

    /// <summary>Receives text from the /agent command and stores the latest instruction.</summary>
    public void ReceivePlayerInstruction(string playerName, string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return;
        _queuedInstruction = $"{playerName}: {instruction}";
    }

    /// <summary>Builds the smallest useful observation for this demo request.</summary>
    private LlmObservation BuildObservation(string instruction)
    {
        return LlmObservation.Create()
            .Use(TerrariaContext.Npc(NPC).Basic().Life())
            .Use(TerrariaContext.World().Time())
            .Use(Context.Custom("input", "player instruction for this request")
                .Field("text", instruction, "latest /agent command text"));
    }

    /// <summary>Declares the only output shape this minimal example understands.</summary>
    private static LlmOutput BuildOutputContract()
    {
        return LlmOutput.Object("talk", "Say a short in-game message.")
            .String("text", required: true, maxLength: 80, description: "message to show above the NPC");
    }

    /// <summary>Polls the non-blocking request handle and applies the completed result.</summary>
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
        }

        _llm = null;
    }

    /// <summary>Displays the LLM response in chat and as overhead combat text.</summary>
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
