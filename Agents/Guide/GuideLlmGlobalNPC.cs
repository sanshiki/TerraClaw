using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraClaw.LLM;

namespace TerraClaw.Agents.Guide;

/// <summary>
/// Per-Guide local state owned by the C# agent layer.
/// This state is intentionally small and is sent to the LLM as the custom "mind" context block.
/// </summary>
public sealed class GuideAgentState
{
    public string AgentId { get; } = "vanilla-guide";
    public string Emotion { get; set; } = "neutral";
    public string CachedChatText { get; set; } = "";
    public string LastSignature { get; set; } = "";
    public int NextHeartbeatTick { get; set; }
    public int NextEventTick { get; set; }
    public LlmRequestHandle? Pending { get; set; }
}

/// <summary>
/// Reactive LLM layer for Terraria's vanilla Guide.
/// It observes the Guide and world state, asks the LLM for dialogue, but leaves vanilla NPC AI untouched.
/// </summary>
public sealed class GuideLlmGlobalNPC : GlobalNPC
{
    private const int HeartbeatTicks = 60 * 60;
    private const int EventCooldownTicks = 60 * 5;
    private const int TimeoutMs = 30000;
    private const string SystemPrompt =
        "You are the dialogue layer for Terraria's vanilla Guide NPC. " +
        "Personality: you are a warm, observant, slightly weary but kind town guide who has seen many new adventurers get hurt and still cares about each one. " +
        "Speak with human feeling: express worry, relief, curiosity, encouragement, mild frustration, or quiet humor when appropriate. " +
        "Do not sound like a neutral help menu or a task machine; let your current emotion influence word choice while staying concise. " +
        // "Do not control movement, combat, or vanilla NPC AI. " +
        "Return only JSON matching the output contract. " +
        "When the output contract allows multiple choices, return either one JSON object or an array of JSON objects. " +
        "combat_text is immediate overhead text. cached_text is saved for the next player right-click chat. Write the text in Simplified Chinese. " +
        "World positions are pixels [x,y], tile positions are integer [x,y], X increases right, Y increases downward.";

    private static readonly Dictionary<int, GuideAgentState> States = new();

    // A single GlobalNPC instance owns state for all Guide entities, indexed by npc.whoAmI.
    public override bool InstancePerEntity => false;

    public override void AI(NPC npc)
    {
        if (npc.type != NPCID.Guide || !npc.active)
            return;

        var state = GetState(npc);
        PollResult(npc, state);

        // Do not queue overlapping requests for the same Guide. The game loop keeps running while the LLM works.
        if (state.Pending != null && state.Pending.IsPending)
            return;

        int tick = (int)Main.GameUpdateCount;
        string signature = BuildSignature(npc);

        // State-signature changes are lightweight event triggers. They send compact event data plus current mind state.
        if (signature != state.LastSignature && tick >= state.NextEventTick)
        {
            Request(npc, state, "event", $"Guide observation changed from [{state.LastSignature}] to [{signature}]. Return combat_text if the change is noticeable, and optionally update cached_text or emotion.");
            state.LastSignature = signature;
            state.NextEventTick = tick + EventCooldownTicks;
            state.NextHeartbeatTick = tick + HeartbeatTicks;
            return;
        }

        // Heartbeat requests are low-frequency background updates so the Guide can react without player interaction.
        if (tick >= state.NextHeartbeatTick)
        {
            Request(npc, state, "heartbeat", "Low frequency heartbeat: return a brief combat_text so the player can see the Guide is thinking, and optionally prepare cached_text or emotion.");
            state.LastSignature = signature;
            state.NextHeartbeatTick = tick + HeartbeatTicks;
        }
    }

    public override void GetChat(NPC npc, ref string chat)
    {
        if (npc.type != NPCID.Guide)
            return;

        // cached_text is produced asynchronously by the LLM and shown only when the player opens Guide chat.
        var state = GetState(npc);
        if (!string.IsNullOrWhiteSpace(state.CachedChatText))
        {
            chat = state.CachedChatText;
            Main.NewText($"[Guide AI] Showing cached chat: {state.CachedChatText}", 180, 220, 255);
        }

    }

    private static GuideAgentState GetState(NPC npc)
    {
        if (!States.TryGetValue(npc.whoAmI, out var state))
        {
            state = new GuideAgentState
            {
                LastSignature = BuildSignature(npc),
                NextHeartbeatTick = (int)Main.GameUpdateCount + 60 * 3,
            };
            States[npc.whoAmI] = state;
        }
        return state;
    }

    private static void Request(NPC npc, GuideAgentState state, string trigger, string instruction)
    {
        if (LlmBridgeSystem.Instance == null)
            return;

        state.Pending = LlmBridgeSystem.Instance.Request(
            state.AgentId,
            SystemPrompt,
            instruction,
            BuildObservation(npc, state, trigger),
            BuildOutputContract(),
            TimeoutMs);
    }

    private static LlmObservation BuildObservation(NPC npc, GuideAgentState state, string trigger)
    {
        return trigger switch
        {
            "heartbeat" => BuildHeartbeatObservation(npc, state, trigger),
            "event" => BuildEventObservation(npc, state, trigger),
            _ => BuildDefaultObservation(npc, state, trigger),
        };
    }

    private static LlmObservation BuildHeartbeatObservation(NPC npc, GuideAgentState state, string trigger)
    {
        // Heartbeat requests get the broadest context because they are low frequency.
        return LlmObservation.Create()
            .Use(TerrariaContext.Npc(npc).Basic().Life().Home())
            .Use(TerrariaContext.World().Time().Moon().Progression())
            .Use(TerrariaContext.Entities().HostilesNear(npc.Center, 500f, max: 5))
            .Use(TerrariaContext.Entities().TownNPCNear(npc.Center, 600f, max: 5))
            .Use(BuildMindContext(state, trigger));
    }

    private static LlmObservation BuildEventObservation(NPC npc, GuideAgentState state, string trigger)
    {
        // Event requests are smaller and focus on the state that caused the trigger.
        return LlmObservation.Create()
            .Use(TerrariaContext.Npc(npc).Basic().Home())
            .Use(TerrariaContext.World().Time().Moon().Progression())
            .Use(TerrariaContext.Entities().HostilesNear(npc.Center, 500f, max: 5))
            .Use(BuildMindContext(state, trigger))
            .Custom("event_signature", BuildSignature(npc), "compact state signature for event trigger")
            .Custom("event_trigger", trigger, "event trigger type");
    }

    private static LlmObservation BuildDefaultObservation(NPC npc, GuideAgentState state, string trigger)
    {
        return LlmObservation.Create()
            .Use(TerrariaContext.Npc(npc).Basic().Life())
            .Use(TerrariaContext.World().Time())
            .Use(BuildMindContext(state, trigger));
    }

    private static CustomContextBuilder BuildMindContext(GuideAgentState state, string trigger)
    {
        return Context.Custom("mind", "Guide LLM memory and trigger state")
            .Field("emotion", state.Emotion, "agent-maintained emotional state")
            .Field("cached", state.CachedChatText, "chat text shown on right-click interaction")
            .Field("trigger", trigger, "why this LLM request was made");
    }

    private static LlmOutput BuildOutputContract()
    {
        // The parser below accepts both flat typed objects and aggregate forms returned for allOf/anyOf contracts.
        return LlmOutput.AnyOf(
            LlmOutput.Object("combat_text", "Show immediate overhead text above the Guide.")
                .String("text", "short overhead text", maxLength: 80),
            LlmOutput.Object("cached_text", "Save text for right-click Guide chat.")
                .String("text", "chat text", maxLength: 240),
            LlmOutput.Object("set_emotion", "Update the Guide's internal emotion state.")
                .String("emotion", "emotion label", maxLength: 40)
        );
    }

    private static void PollResult(NPC npc, GuideAgentState state)
    {
        if (state.Pending == null || !state.Pending.TryGetResult(out LlmResult result))
            return;

        ApplyOutput(npc, state, result);
        state.Pending = null;
    }

    private static void ApplyOutput(NPC npc, GuideAgentState state, LlmResult result)
    {
        if (result.TryGetBranch("combat_text", out var combatText))
            ShowCombatText(npc, combatText.String("text"));

        if (result.TryGetBranch("cached_text", out var cachedText))
            SetCachedText(state, cachedText.String("text"));

        if (result.TryGetBranch("set_emotion", out var emotionOutput))
            state.Emotion = Truncate(emotionOutput.String("emotion", emotionOutput.String("text", state.Emotion)), 40);
        else
        {
            string emotion = result.String("emotion");
            if (!string.IsNullOrWhiteSpace(emotion))
                state.Emotion = Truncate(emotion, 40);
        }
    }

    private static void ShowCombatText(NPC npc, string text)
    {
        text = Truncate(text, 80);
        if (string.IsNullOrWhiteSpace(text))
            return;
        CombatText.NewText(npc.Hitbox, Color.Gold, text);
    }

    private static void SetCachedText(GuideAgentState state, string text)
    {
        state.CachedChatText = Truncate(text, 240);
        if (!string.IsNullOrWhiteSpace(state.CachedChatText))
            Main.NewText($"[Guide AI] Cached chat updated: {state.CachedChatText}", 180, 220, 255);
    }

    private static string BuildSignature(NPC npc)
    {
        // Keep this signature compact and stable; changes here directly affect event-trigger frequency.
        return string.Join("|",
            Main.dayTime ? "day" : "night",
            Main.moonPhase,
            Main.bloodMoon ? "blood" : "normal",
            Main.hardMode ? "hard" : "prehard",
            npc.homeless ? "homeless" : "housed",
            npc.homeTileX,
            npc.homeTileY,
            CountNearbyHostiles(npc.Center, 800f));
    }

    private static int CountNearbyHostiles(Vector2 center, float radius)
    {
        int count = 0;
        float radiusSq = radius * radius;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var other = Main.npc[i];
            if (other == null || !other.active || other.CanBeChasedBy() == false)
                continue;
            if (Vector2.DistanceSquared(center, other.Center) <= radiusSq)
                count++;
        }
        return count;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}




