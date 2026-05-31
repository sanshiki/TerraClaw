using Microsoft.Xna.Framework;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ModLoader;
using TerraClaw.LLM;

namespace TerraClaw.Agents.TerraClaw;

/// <summary>
/// Minimal self-contained ModNPC example for the C#-first LLM framework.
/// This class owns spawning, player instruction delivery, LLM requests, polling, and result display.
/// </summary>
public sealed class ExampleTerraClawAgent : ModNPC
{
    // Keep this example intentionally small: one /agent instruction triggers one LLM response.
    private const int PlayerInstructionCooldownTicks = 60 * 5;
    private const string SystemPrompt =
        "You are an AI controller inside Terraria. Return only JSON matching the requested output contract.\n\n" +
        "Terraria coordinate system:\n" +
        "- World positions are measured in pixels as [x,y].\n" +
        "- Tile positions are integer grid coordinates: tile_x = pixel_x / 16, tile_y = pixel_y / 16.\n" +
        "- X increases to the right.\n" +
        "- Y increases downward.\n" +
        "- This example only supports returning a short message.";

    private readonly string _llmAgentId = System.Guid.NewGuid().ToString();
    private LlmRequestHandle? _llm;
    private int _nextPlayerInstructionTick;
    private string _queuedInstruction = "";

    public override void SetStaticDefaults()
    {
        Main.npcFrameCount[Type] = 4;
    }

    public override void SetDefaults()
    {
        NPC.width = 18;
        NPC.height = 28;
        NPC.damage = 0;
        NPC.defense = 0;
        NPC.lifeMax = 9999;
        NPC.life = 9999;
        NPC.knockBackResist = 0f;
        NPC.dontTakeDamage = true;
        NPC.noTileCollide = true;
        NPC.noGravity = true;
        NPC.friendly = true;
        NPC.chaseable = false;
        NPC.hide = false;
        NPC.dontCountMe = true;
    }

    public override void AI()
    {
        PollLlm();

        if (_llm != null && _llm.IsPending)
            return;
        if (string.IsNullOrWhiteSpace(_queuedInstruction))
            return;
        if (Main.GameUpdateCount < _nextPlayerInstructionTick)
            return;
        if (LlmBridgeSystem.Instance == null)
            return;

        string instruction = _queuedInstruction;
        string requestInstruction = $"The player sent this /agent instruction: {instruction}. Reply with one short talk output.";
        _queuedInstruction = "";
        _llm = LlmBridgeSystem.Instance.Request(
            _llmAgentId,
            SystemPrompt,
            requestInstruction,
            BuildObservation(instruction),
            BuildOutputContract(),
            timeoutMs: 30000);
        _nextPlayerInstructionTick = (int)Main.GameUpdateCount + PlayerInstructionCooldownTicks;
    }

    public override void FindFrame(int frameHeight)
    {
        NPC.frameCounter++;
        if (NPC.frameCounter < 10)
            return;

        NPC.frameCounter = 0;
        NPC.frame.Y += frameHeight;
        if (NPC.frame.Y >= frameHeight * 4)
            NPC.frame.Y = 0;
    }

    public override bool CheckActive() => false;

    /// <summary>Stores the latest /agent instruction for this NPC.</summary>
    public void ReceivePlayerInstruction(string playerName, string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return;
        _queuedInstruction = $"{playerName}: {instruction}";
    }

    /// <summary>Spawns the example ModNPC directly, without a separate agent host or Bind step.</summary>
    public static int Spawn(Vector2 worldPos, Terraria.DataStructures.IEntitySource source)
    {
        return NPC.NewNPC(source, (int)worldPos.X, (int)worldPos.Y, ModContent.NPCType<ExampleTerraClawAgent>());
    }

    /// <summary>Delivers player text to every active example agent in the world.</summary>
    public static int DeliverPlayerInstruction(string playerName, string instruction)
    {
        int delivered = 0;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var npc = Main.npc[i];
            if (npc == null || !npc.active || npc.ModNPC is not ExampleTerraClawAgent agent)
                continue;

            agent.ReceivePlayerInstruction(playerName, instruction);
            delivered++;
        }
        return delivered;
    }

    /// <summary>Builds the smallest useful observation for this demo request.</summary>
    private LlmObservation BuildObservation(string instruction)
    {
        return LlmObservation.Create()
            .Use(TerrariaContext.Npc(NPC).Basic().Life())
            .Use(TerrariaContext.World().Time())
            .Use(TerrariaContext.Tiles(NPC.Center, radiusTiles: 24).Area(maxSpecials: 12))
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
        if (type == "talk")
            Say(obj["text"]?.GetValue<string>() ?? "");

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
