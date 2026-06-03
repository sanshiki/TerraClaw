#nullable enable

using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraClaw.LLM;
using TerraClaw.Util;

namespace TerraClaw.Agents.TerraClaw;

/// <summary>
/// Self-contained ModNPC example for the C#-first LLM framework.
/// This owns player instruction delivery, memory, request polling, and a small action loop.
/// </summary>
public sealed class ExampleTerraClawAgent : ModNPC
{
    private const int ShortTermMemoryLimit = 8;
    private const int ScanMemoryTicks = 60 * 30;
    private const int MoveTimeoutTicks = 60 * 20;
    private const int BreakTimeoutTicks = 60 * 12;
    private const int BreakTilesMaxRadius = 5;
    private const float MoveAcc = 0.5f;
    private const float MoveSpeed = 20f;
    private const float MoveInertia = 20f;
    private const string SystemPrompt =
        "You are an AI controller inside Terraria. Return only JSON matching the requested output contract.\n\n" +
        "Terraria coordinate system:\n" +
        "- All coordinates exposed to you are tile coordinates.\n" +
        "- Use integer tile coordinates [tile_x,tile_y] for movement, mining, and interpreting scans.\n" +
        "- X increases to the right. Y increases downward.\n" +
        "- You can fly through walls and ignore terrain blocking.\n" +
        "- Choose exactly one action each turn.\n" +
        "- While the task goal is not yet complete, prefer callback=true so the agent keeps acting. \n" +
        "- If you need to await player's input, set callback=false so the agent waits for the next instruction turn.\n" +
        "- If you believe the task is complete or unreachable, use plan to clear the goal memory, and inform the player. Remember NOT to use callback=true!\n" +
        "- Use plan to update goal memory/todo list without doing a physical action.\n" +
        "- Use scanarea when you need a larger tile observation before deciding.";

    private readonly string _llmAgentId = Guid.NewGuid().ToString();
    private readonly Queue<string> _shortTermMemory = new();
    private readonly List<string> _goalMemory = new();

    private LlmRequestHandle? _llm;
    private LlmRequestHandle? _compactLlm;
    private PendingAction? _action;
    private int _lastScanTick = -ScanMemoryTicks;
    private int _lastScanRadius = 24;
    private string _queuedInstruction = "";
    private string _pendingCallbackReason = "";
    private string _lastActionSummary = "none";
    private string _longTermMemory = "";
    private int _requestSequence;
    public override void SetStaticDefaults()
    {
        Main.npcFrameCount[Type] = 6;
    }

    public override void SetDefaults()
    {
        NPC.width = 36;
        NPC.height = 52;
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
        PollCompactLlm();
        PollLlm();
        UpdateAction();
        MaybeStartNextRequest();

        // apply deacc
        NPC.velocity *= 0.9f;

        UpdateAnimation();
    }

    public override void FindFrame(int frameHeight)
    {
        NPC.frameCounter++;
        if (NPC.frameCounter < 10)
            return;

        NPC.frameCounter = 0;
        NPC.frame.Y += frameHeight;
        if (NPC.frame.Y >= frameHeight * Main.npcFrameCount[Type])
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

    private void MaybeStartNextRequest()
    {
        if (_llm != null && _llm.IsPending)
            return;
        if (_compactLlm != null && _compactLlm.IsPending)
            return;
        if (_action != null)
            return;
        if (LlmBridgeSystem.Instance == null)
            return;

        // New player commands can interrupt callback chains. Memory compact runs before callbacks
        // so STM does not grow indefinitely during long tasks.
        if (TryStartPlayerInstructionRequest())
            return;
        if (TryStartCompactRequest())
            return;
        TryStartCallbackRequest();
    }

    private bool TryStartPlayerInstructionRequest()
    {
        if (string.IsNullOrWhiteSpace(_queuedInstruction))
            return false;
        string playerInstruction = _queuedInstruction;
        string trigger = $"The player sent this /agent instruction: {playerInstruction}. Choose one action.";
        _queuedInstruction = "";
        StartMainRequest(trigger, playerInstruction);
        return true;
    }

    private bool TryStartCallbackRequest()
    {
        if (string.IsNullOrWhiteSpace(_pendingCallbackReason))
            return false;

        string trigger = $"Action completed: {_pendingCallbackReason}. Choose the next action.";
        _pendingCallbackReason = "";
        StartMainRequest(trigger, playerInstruction: "");
        return true;
    }

    private void StartMainRequest(string trigger, string playerInstruction)
    {
        if (LlmBridgeSystem.Instance == null)
            return;

        _requestSequence++;
        _llm = LlmBridgeSystem.Instance.Request(
            _llmAgentId,
            SystemPrompt,
            trigger,
            BuildObservation(playerInstruction, trigger),
            BuildOutputContract(),
            timeoutMs: 30000);
    }

    private bool TryStartCompactRequest()
    {
        if (_shortTermMemory.Count < ShortTermMemoryLimit)
            return false;
        if (LlmBridgeSystem.Instance == null)
            return false;

        var batch = _shortTermMemory.ToArray();

        _compactLlm = LlmBridgeSystem.Instance.Request(
            _llmAgentId + ":memory",
            "You update long-term memory for a Terraria agent. Return only JSON matching the output contract.",
            "Merge the short-term memories into the existing long-term memory. Keep durable facts, goals, preferences, and useful world state. Drop transient chatter. Do not issue game actions.",
            LlmObservation.Create()
                .Use(Context.Custom("memory_compact", "long-term memory update")
                    .Field("ltm", _longTermMemory, "existing long-term memory")
                    .Field("stm", string.Join(" | ", batch), "short-term summaries to merge")),
            LlmOutput.Object("memory", "Updated long-term memory.")
                .String("updated_ltm", required: true, maxLength: 1000, description: "complete updated long-term memory string"),
            timeoutMs: 20000);
        return true;
    }

    private LlmObservation BuildObservation(string playerInstruction, string trigger)
    {
        int radius = IsRecentScanAvailable() ? _lastScanRadius : 24;
        return LlmObservation.Create()
            .Use(BuildAgentTileContext())
            .Use(TerrariaContext.Npc(NPC).Life())
            .Use(TerrariaContext.World().Time())
            .Use(TerrariaContext.Tiles(NPC.Center, radiusTiles: radius).Area(maxSpecials: radius >= 48 ? 32 : 16))
            .Use(Context.Custom("input", "request trigger")
                .Field("seq", _requestSequence, "request sequence number")
                .Field("text", playerInstruction, "latest /agent command text, empty for callback turns")
                .Field("trigger", trigger, "why this request was made")
                .Field("last_action", _lastActionSummary, "last action result"))
            .Use(Context.Custom("mem", "example-only agent memory")
                .Field("goal", string.Join(" | ", _goalMemory), "goal memory / todo list")
                .Field("stm", string.Join(" | ", _shortTermMemory), "short term memory queue")
                .Field("ltm", _longTermMemory, "long term memory")
                .Field("scan", IsRecentScanAvailable() ? $"large scan radius {_lastScanRadius}" : "none", "recent scanarea result"));
    }

    private CustomContextBuilder BuildAgentTileContext()
    {
        return Context.Custom("agent", "agent state using tile coordinates only")
            .Field("name", NPC.FullName, "agent display name")
            .Field("tile_x", (int)(NPC.Center.X / 16f), "current tile x")
            .Field("tile_y", (int)(NPC.Center.Y / 16f), "current tile y")
            .Field("dir", NPC.direction == 1 ? "right" : "left", "facing direction");
    }

    private static LlmOutput BuildOutputContract()
    {
        string callback_description = "true if the task is not yet complete and should continue";
        return LlmOutput.OneOf(
            WithMemoryFields(
                LlmOutput.Object("say", "Say a short in-game message.")
                    .String("text", required: true, maxLength: 100, description: "message to show above the NPC"),
                callback_description),
            WithMemoryFields(
                LlmOutput.Object("moveto", "Move toward a target tile.")
                    .Number("tile_x", required: true, description: "target tile x")
                    .Number("tile_y", required: true, description: "target tile y"),
                callback_description),
            WithMemoryFields(
                LlmOutput.Object("breaktiles", "Break nearby solid tiles in a small circle.")
                    .Number("tile_x", required: true, description: "center tile x")
                    .Number("tile_y", required: true, description: "center tile y")
                    .Number("radius", required: true, defaultValue: 1, description: "tile radius, clamped to 0..5"),
                callback_description),
            WithMemoryFields(
                LlmOutput.Object("scanarea", "Request a larger tile observation for the next LLM turn.")
                    .Number("radius", required: true, defaultValue: 48, description: "scan radius in tiles, clamped to 24..80"),
                callback_description),
            WithMemoryFields(
                LlmOutput.Object("plan", "Update goal memory / todo list.")
                    .String("todo", required: true, maxLength: 240, description: "replacement todo list or concise plan"),
                callback_description));
    }

    private static LlmObjectBuilder WithMemoryFields(LlmObjectBuilder output, string callbackDescription)
    {
        return output
            .String("summary", required: true, maxLength: 320, description: "short summary of this request including what you observe, what you do and why")
            .Boolean("callback", required: true, description: callbackDescription);
    }

    private void PollLlm()
    {
        if (_llm == null)
            return;
        if (_llm.IsPending)
            return;

        if (_llm.TryGetResult(out JsonNode? output) && output is JsonObject obj)
        {
            ApplyLlmOutput(obj);
            EnqueueTurnSummary(obj);
        }
        else if (_llm.IsDone)
            EnqueueTurnSummary(new JsonObject
            {
                ["type"] = "failed",
                ["summary"] = $"request failed: {_llm.Error ?? _llm.Status.ToString()}",
            });

        _llm = null;
    }

    private void UpdateAnimation()
    {
        NPC.rotation = NPC.velocity.X * 0.02f;

        if (_llm == null)
            return;
        if (_llm.IsPending)
            return;

        if (_llm.TryGetResult(out JsonNode? output) && output is JsonObject obj)
        {
            string type = ReadString(obj, "type");

            switch (type)
            {
                case "say":
                    break;
                case "moveto":
                    break;
                case "breaktiles":
                    break;
                case "scanarea":
                    break;
                case "plan":
                    break;
                default:
                    break;
            }
        }
            
    }

    private void ApplyLlmOutput(JsonObject obj)
    {
        string type = ReadString(obj, "type");
        bool callback = ReadBool(obj, "callback");

        switch (type)
        {
            case "say":
                string text = ReadString(obj, "text");
                Say(text);
                FinishImmediateAction("say", string.IsNullOrWhiteSpace(text) ? "empty" : text, callback);
                break;

            case "moveto":
                StartMove(
                    ReadInt(obj, "tile_x", (int)(NPC.Center.X / 16f)),
                    ReadInt(obj, "tile_y", (int)(NPC.Center.Y / 16f)),
                    callback);
                break;

            case "breaktiles":
                StartBreakTiles(
                    ReadInt(obj, "tile_x", (int)(NPC.Center.X / 16f)),
                    ReadInt(obj, "tile_y", (int)(NPC.Center.Y / 16f)),
                    ReadInt(obj, "radius", 1),
                    callback);
                break;

            case "scanarea":
                StartScanArea(ReadInt(obj, "radius", 48), callback);
                break;

            case "plan":
                UpdatePlan(ReadString(obj, "todo"), callback);
                break;

            default:
                FinishImmediateAction("unknown", $"unsupported output type '{type}'", callback: false);
                break;
        }
    }

    private void StartMove(int tileX, int tileY, bool callback)
    {
        var target = new Vector2(tileX * 16f + 8f, tileY * 16f + 8f);
        _action = new PendingAction(ActionKind.MoveTo, callback)
        {
            TargetWorld = target,
            StartedTick = (int)Main.GameUpdateCount,
            TimeoutTicks = MoveTimeoutTicks,
        };
        _lastActionSummary = $"moving to tile [{tileX},{tileY}]";
    }

    private void StartBreakTiles(int tileX, int tileY, int radius, bool callback)
    {
        radius = Math.Clamp(radius, 0, BreakTilesMaxRadius);
        var targets = new Queue<Point>();
        for (int y = tileY - radius; y <= tileY + radius; y++)
        {
            for (int x = tileX - radius; x <= tileX + radius; x++)
            {
                if (Vector2.Distance(new Vector2(x, y), new Vector2(tileX, tileY)) > radius + 0.01f)
                    continue;
                if (!WorldGen.InWorld(x, y, 10))
                    continue;
                Tile tile = Main.tile[x, y];
                if (tile.HasTile && Main.tileSolid[tile.TileType])
                    targets.Enqueue(new Point(x, y));
            }
        }

        _action = new PendingAction(ActionKind.BreakTiles, callback)
        {
            TileTargets = targets,
            StartedTick = (int)Main.GameUpdateCount,
            TimeoutTicks = BreakTimeoutTicks,
        };
        _lastActionSummary = $"breaking {targets.Count} tiles near [{tileX},{tileY}]";
    }

    private void StartScanArea(int radius, bool callback)
    {
        _lastScanRadius = Math.Clamp(radius, 24, 80);
        _lastScanTick = (int)Main.GameUpdateCount;
        FinishImmediateAction("scanarea", $"large scan prepared with radius {_lastScanRadius}", callback);
    }

    private void UpdatePlan(string todo, bool callback)
    {
        _goalMemory.Clear();
        foreach (string item in SplitMemoryItems(todo).Take(8))
            _goalMemory.Add(item);
        FinishImmediateAction("plan", $"goal memory updated: {string.Join(" | ", _goalMemory)}", callback);
    }

    private void UpdateAction()
    {
        if (_action == null)
            return;

        if (Main.GameUpdateCount - _action.StartedTick > _action.TimeoutTicks)
        {
            FinishAction(_action, $"{_action.Kind} timed out");
            return;
        }

        switch (_action.Kind)
        {
            case ActionKind.MoveTo:
                UpdateMove(_action);
                break;
            case ActionKind.BreakTiles:
                UpdateBreakTiles(_action);
                break;
        }
    }

    private void UpdateMove(PendingAction action)
    {
        Vector2 delta = action.TargetWorld - NPC.Center;
        if (delta.Length() <= 12f)
        {
            FinishAction(action, $"arrived at tile [{(int)(NPC.Center.X / 16f)},{(int)(NPC.Center.Y / 16f)}]");
            return;
        }

        var target = action.TargetWorld;
        float dist = Vector2.Distance(NPC.Center, target);

        var dir = target - NPC.Center;
        
        NPC.velocity = AIHelper.HomeinToTarget(NPC.Center, NPC.velocity, target, MoveSpeed, MoveInertia);

        NPC.direction = dir.X > 0 ? 1 : -1;
    }

    private void UpdateBreakTiles(PendingAction action)
    {
        int perTick = 2;
        int broken = 0;
        while (perTick-- > 0 && action.TileTargets.Count > 0)
        {
            Point point = action.TileTargets.Dequeue();
            if (!WorldGen.InWorld(point.X, point.Y, 10))
                continue;
            Tile tile = Main.tile[point.X, point.Y];
            if (!tile.HasTile || !Main.tileSolid[tile.TileType])
                continue;

            WorldGen.KillTile(point.X, point.Y, fail: false, effectOnly: false, noItem: false);
            if (Main.netMode == NetmodeID.Server)
                NetMessage.SendData(MessageID.TileManipulation, -1, -1, null, 0, point.X, point.Y);
            Dust.QuickDust(new Vector2(point.X * 16f + 8f, point.Y * 16f + 8f), Color.OrangeRed);
            broken++;
        }

        action.BrokenTiles += broken;
        if (action.TileTargets.Count == 0)
            FinishAction(action, $"breaktiles completed, broke {action.BrokenTiles} tiles");
    }

    private void FinishImmediateAction(string action, string summary, bool callback)
    {
        _lastActionSummary = $"{action}: {summary}";
        if (callback)
            _pendingCallbackReason = _lastActionSummary;
    }

    private void FinishAction(PendingAction action, string summary)
    {
        _action = null;
        _lastActionSummary = summary;
        if (action.Callback)
            _pendingCallbackReason = summary;
    }

    private void EnqueueTurnSummary(JsonObject obj)
    {
        // STM stores one compact model-authored note per normal request. Raw observation
        // remains request-local and is never copied into memory.
        string summary = ReadString(obj, "summary");
        if (string.IsNullOrWhiteSpace(summary))
        {
            string type = ReadString(obj, "type");
            summary = $"{type}: {_lastActionSummary}";
        }

        if (string.IsNullOrWhiteSpace(summary))
            return;

        _shortTermMemory.Enqueue(summary.Trim());
        while (_shortTermMemory.Count > ShortTermMemoryLimit)
            _shortTermMemory.Dequeue();
    }

    private void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (text.Length > 100)
            text = text[..100];
        Main.NewText($"<{NPC.FullName}> {text}", 200, 200, 100);
        CombatText.NewText(NPC.Hitbox, Color.Gold, text);
    }

    private void PollCompactLlm()
    {
        if (_compactLlm == null)
            return;
        if (_compactLlm.IsPending)
            return;

        if (_compactLlm.TryGetResult(out JsonNode? output) && output is JsonObject obj)
        {
            string updated = ReadString(obj, "updated_ltm");
            if (!string.IsNullOrWhiteSpace(updated))
                _longTermMemory = updated.Trim();
        }

        _shortTermMemory.Clear();
        _compactLlm = null;
    }

    private bool IsRecentScanAvailable()
    {
        return Main.GameUpdateCount - _lastScanTick <= ScanMemoryTicks;
    }

    private static IEnumerable<string> SplitMemoryItems(string text)
    {
        return (text ?? "")
            .Split(new[] { '\n', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);
    }

    private static string ReadString(JsonObject obj, string key)
    {
        return obj[key]?.GetValue<string>() ?? "";
    }

    private static bool ReadBool(JsonObject obj, string key)
    {
        return obj[key]?.GetValue<bool>() ?? false;
    }

    private static int ReadInt(JsonObject obj, string key, int fallback)
    {
        JsonNode? node = obj[key];
        if (node == null)
            return fallback;
        try
        {
            return node.GetValueKind() == JsonValueKind.Number
                ? (int)Math.Round(node.GetValue<double>())
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private enum ActionKind
    {
        MoveTo,
        BreakTiles,
    }

    private sealed class PendingAction
    {
        public PendingAction(ActionKind kind, bool callback)
        {
            Kind = kind;
            Callback = callback;
        }

        public ActionKind Kind { get; }
        public bool Callback { get; }
        public int StartedTick { get; set; }
        public int TimeoutTicks { get; set; }
        public Vector2 TargetWorld { get; set; }
        public Queue<Point> TileTargets { get; set; } = new();
        public int BrokenTiles { get; set; }
    }
}


public class ExampleTerraClawAgentClaw : ModProjectile
{
    
}