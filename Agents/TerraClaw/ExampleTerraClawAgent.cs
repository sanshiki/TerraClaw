#nullable enable

using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.UI;
using Terraria.ID;
using Terraria.ModLoader;
using TerraClaw.LLM;
using TerraClaw.Knowledge;
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
    private const int ClawEnsureIntervalTicks = 30;
    private const int ActionEmoteDurationTicks = 90;
    private const int ActionEmoteCooldownTicks = 30;
    private static readonly string[] PreferredKnowledgeSourceIds = { "terraria-zh", "terraria-calamity-zh"/* , "terraria-homewardjourney-zh" */ };

    // Movement constants
    private const float MovingSpeed = 30f;
    private const float LingeringSpeed = 2f;
    private const float MovingInertia = 30f;
    private const float LingeringInertia = 150f;
    private const float LingeringRadius = 16f * 5f;
    private const int LingeringUpdateTicks = 60;
    private const float DampFactor = 0.91f;
    private const string SystemPrompt =
        "You are an AI controller inside Terraria. Return only JSON matching the requested output contract.\n\n" +
        "Terraria coordinate system:\n" +
        "- All coordinates exposed to you are tile coordinates.\n" +
        "- Use integer tile coordinates [tile_x,tile_y] for movement, mining, and interpreting scans.\n" +
        "- X increases to the right. Y increases downward.\n" +
        "- You can fly through walls and ignore terrain blocking.\n" +
        "- Your observation can pass through all terrain blocks and walls without any collision obstruction.\n" +
        "- Choose exactly one action each turn.\n" +
        "- While the task goal is not yet complete, prefer callback=true so the agent keeps acting. \n" +
        "- If you need to await player's input, set callback=false so the agent waits for the next instruction turn.\n" +
        "- If you believe the task is complete or unreachable, use plan to clear the goal memory, and inform the player. Remember NOT to use callback=true!\n" +
        "- Use plan to update goal memory/todo list without doing a physical action.\n" +
        "- Use scanarea when you need a larger tile observation before deciding.\n" +
        "- Use knowledge_query when you need configured Terraria/Mod wiki facts about items, NPCs, bosses, recipes, drops, biomes, or progression; do not guess missing game facts.\n" +
        "- When the know observation is present, use that wiki knowledge to answer or plan the next action.\n" +
        "- For better search results, the query should be a short keyword-only string, not a full sentence.";

    private readonly string _llmAgentId = Guid.NewGuid().ToString();
    private readonly Queue<string> _shortTermMemory = new();
    private readonly List<string> _goalMemory = new();

    private LlmRequestHandle? _llm;
    private LlmRequestHandle? _compactLlm;
    private KnowledgeRequestHandle? _knowledge;
    private PendingAction? _action;
    private int _lastScanTick = -ScanMemoryTicks;
    private int _lastScanRadius = 24;
    private string _queuedInstruction = "";
    private string _pendingCallbackReason = "";
    private string _lastActionSummary = "none";
    private string _longTermMemory = "";
    private string _lastKnowledgeQuery = "";
    private string _lastKnowledgeContext = "";
    private string[]? _knowledgeSourceIds;
    private bool _knowledgeCallback;
    private int _requestSequence;
    private Vector2 _target;
    private Vector2 _lingerOffset;
    private float _moveSpeed;
    private float _moveInertia;
    private int _lingerCnt = 60;
    private int _nextClawEnsureTick;
    private int _nextActionEmoteTick;
    private ProjectileReference _leftClawReference;
    private ProjectileReference _rightClawReference;

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
        _leftClawReference.Clear();
        _rightClawReference.Clear();
    }

    public override void AI()
    {
        Init();

        PollKnowledge();
        PollCompactLlm();
        PollLlm();
        UpdateAction();
        MaybeStartNextRequest();

        EnsureClaws();
        UpdateMovement();
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
        if (_knowledge != null && _knowledge.IsPending)
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
        _pendingCallbackReason = "";
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
        var observation = LlmObservation.Create()
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

        if (!string.IsNullOrWhiteSpace(_lastKnowledgeContext))
            observation.Use(BuildKnowledgeContext());

        return observation;
    }

    private CustomContextBuilder BuildKnowledgeContext()
    {
        return Context.Custom("know", "latest configured wiki query result")
            .Field("query", _lastKnowledgeQuery, "wiki search query")
            .Field("results", _lastKnowledgeContext, "compact wiki result summaries with source urls");
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
                LlmOutput.Object("knowledge_query", "Query configured Terraria/Mod wiki sources for game knowledge before answering or planning using only keywords.")
                    .String("query", required: true, maxLength: 120, description: "short wiki search query with keywords only")
                    .String("reason", required: true, maxLength: 160, description: "why wiki knowledge is needed"),
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

    private void PollKnowledge()
    {
        if (_knowledge == null)
            return;
        if (_knowledge.IsPending)
            return;

        bool callback = _knowledgeCallback;
        if (_knowledge.TryGetResult(out KnowledgeQueryResult result))
        {
            _lastKnowledgeQuery = result.Query;
            _lastKnowledgeContext = FormatKnowledgeResult(result);
            string source = result.FromCache ? "cached wiki" : "wiki";
            FinishImmediateAction("knowledge_query", $"{source} returned {result.Results.Count} results for '{Truncate(result.Query, 80)}'", callback);
        }
        else if (_knowledge.IsDone)
        {
            _lastKnowledgeQuery = _knowledge.Query;
            _lastKnowledgeContext = $"Knowledge query failed for '{_knowledge.Query}': {_knowledge.Error ?? _knowledge.Status.ToString()}";
            FinishImmediateAction("knowledge_query", _lastKnowledgeContext, callback);
        }

        _knowledgeCallback = false;
        _knowledge = null;
    }

    private void PollLlm()
    {
        if (_llm == null)
            return;
        if (_llm.IsPending)
            return;

        if (_llm.TryGetResult(out LlmResult result))
        {
            ApplyLlmOutput(result);
            EnqueueTurnSummary(result);
        }
        else if (_llm.IsDone)
            EnqueueTurnSummary($"request failed: {_llm.Error ?? _llm.Status.ToString()}");

        _llm = null;
    }
    public override void OnSpawn(IEntitySource source)
    {
        _target = NPC.Center;
        _leftClawReference.Clear();
        _rightClawReference.Clear();
    }

    private void Init()
    {
        _moveSpeed = LingeringSpeed;
        _moveInertia = LingeringInertia;
    }

    private void UpdateMovement()
    {
        Vector2 _curr_target = _target;
        if (_action?.Kind != ActionKind.MoveTo)
        {
            if(_lingerCnt >= LingeringUpdateTicks)
            {
                _lingerOffset = new Vector2(LingeringRadius, 0)
                    .RotatedBy(AIHelper.RandomFloat(-MathHelper.Pi, MathHelper.Pi));
                _lingerCnt = 0;
            }
            _lingerCnt++;
            _curr_target = _target+_lingerOffset;
        }

        NPC.velocity = AIHelper.HomeinToTarget(NPC.Center, NPC.velocity, _curr_target, _moveSpeed, _moveInertia);

        // Main.NewText($"vel: {NPC.velocity}, dir: {NPC.direction}, target: {_target}, linger: {_lingerOffset}");

        NPC.velocity *= DampFactor;
    }

    private void UpdateAnimation()
    {
        var dir = _target - NPC.Center;
        NPC.rotation = NPC.velocity.X * 0.03f;
        NPC.direction = dir.X > 0 ? 1 : -1;
        NPC.spriteDirection = -NPC.direction;

        // add light
        Lighting.AddLight(NPC.Center, 0.3f, 0.3f, 1f);
    }

    private void ApplyLlmOutput(LlmResult result)
    {
        string type = result.Type;
        bool callback = result.Bool("callback");

        switch (type)
        {
            case "say":
                string text = result.String("text");
                Say(text);
                ShowActionEmote(EmoteID.EmoteNote);
                FinishImmediateAction("say", string.IsNullOrWhiteSpace(text) ? "empty" : text, callback);
                break;

            case "moveto":
                StartMove(
                    result.Int("tile_x", (int)(NPC.Center.X / 16f)),
                    result.Int("tile_y", (int)(NPC.Center.Y / 16f)),
                    callback);
                break;

            case "breaktiles":
                StartBreakTiles(
                    result.Int("tile_x", (int)(NPC.Center.X / 16f)),
                    result.Int("tile_y", (int)(NPC.Center.Y / 16f)),
                    result.Int("radius", 1),
                    callback);
                break;

            case "scanarea":
                StartScanArea(result.Int("radius", 48), callback);
                break;

            case "knowledge_query":
                StartKnowledgeQuery(result.String("query"), callback);
                break;

            case "plan":
                UpdatePlan(result.String("todo"), callback);
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
        ShowActionEmote(EmoteID.EmoteRun);
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
        ShowActionEmote(targets.Count > 0 ? EmoteID.ItemPickaxe : EmoteID.EmoteConfused);
        _lastActionSummary = $"breaking {targets.Count} tiles near [{tileX},{tileY}]";
    }

    private void StartScanArea(int radius, bool callback)
    {
        _lastScanRadius = Math.Clamp(radius, 24, 80);
        _lastScanTick = (int)Main.GameUpdateCount;
        ShowActionEmote(EmoteID.EmotionAlert);
        FinishImmediateAction("scanarea", $"large scan prepared with radius {_lastScanRadius}", callback);
    }

    private void StartKnowledgeQuery(string query, bool callback)
    {
        query = Truncate(query, 120);
        if (string.IsNullOrWhiteSpace(query))
        {
            FinishImmediateAction("knowledge_query", "empty wiki query", callback);
            return;
        }

        _knowledgeCallback = callback;
        try
        {
            var api = global::TerraClaw.TerraClaw.Api;
            if (api == null || !api.HasFeature("knowledge.query.v1"))
            {
                _knowledgeCallback = false;
                FinishImmediateAction("knowledge_query", "knowledge API unavailable", callback);
                return;
            }

            string[] sourceIds = ResolveKnowledgeSourceIds(api);
            _knowledge = sourceIds.Length > 0
                ? api.RequestKnowledgeFromSources(_llmAgentId + ":knowledge", query, sourceIds, limit: 3, extractChars: 650, timeoutMs: 20000)
                : api.RequestKnowledge(_llmAgentId + ":knowledge", query, limit: 3, extractChars: 650, timeoutMs: 20000);
            string sourceSummary = sourceIds.Length > 0 ? string.Join(",", sourceIds) : "all enabled sources";
            _lastActionSummary = $"querying {sourceSummary}: {query}";
            ShowActionEmote(EmoteID.EmotionAlert);
        }
        catch (Exception ex)
        {
            _knowledge = null;
            _knowledgeCallback = false;
            _lastKnowledgeQuery = query;
            _lastKnowledgeContext = $"Knowledge query failed for '{query}': {ex.Message}";
            FinishImmediateAction("knowledge_query", _lastKnowledgeContext, callback);
        }
    }

    private string[] ResolveKnowledgeSourceIds(global::TerraClaw.API.TerraClawApi api)
    {
        if (_knowledgeSourceIds != null)
            return _knowledgeSourceIds;

        // Example: an agent can lock itself to a preferred wiki source during initialization.
        // Change PreferredKnowledgeSourceIds to { "terraria-zh" }, { "calamity-en" }, or a custom source id from TerraClawConfig.json.
        if (!api.HasFeature("knowledge.sources.v1"))
        {
            _knowledgeSourceIds = Array.Empty<string>();
            return _knowledgeSourceIds;
        }

        HashSet<string> available = api.GetKnowledgeSources()
            .Select(source => source.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _knowledgeSourceIds = PreferredKnowledgeSourceIds
            .Where(available.Contains)
            .ToArray();
        return _knowledgeSourceIds;
    }

    private void UpdatePlan(string todo, bool callback)
    {
        _goalMemory.Clear();
        foreach (string item in SplitMemoryItems(todo).Take(8))
            _goalMemory.Add(item);
        ShowActionEmote(EmoteID.ItemCog);
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

        _target = action.TargetWorld;
        _moveSpeed = MovingSpeed;
        _moveInertia = MovingInertia;
    }

    private void UpdateBreakTiles(PendingAction action)
    {
        DispatchBreakTargets(action);

        if (action.TileTargets.Count == 0 && action.InflightClawBreaks == 0)
            FinishAction(action, $"breaktiles completed, broke {action.BrokenTiles} tiles");
    }

    private void DispatchBreakTargets(PendingAction action)
    {
        while (action.TileTargets.Count > 0)
        {
            if (!TryGetIdleClaw(out ExampleTerraClawAgentClaw? claw))
                return;

            Point point = action.TileTargets.Peek();
            if (!IsBreakableTile(point))
            {
                action.TileTargets.Dequeue();
                continue;
            }

            action.TileTargets.Dequeue();
            claw.BeginBreakTile(point);
            action.InflightClawBreaks++;
        }
    }

    internal void NotifyClawBreakComplete(bool brokeTile)
    {
        if (_action?.Kind != ActionKind.BreakTiles)
            return;
        if (brokeTile)
            _action.BrokenTiles++;
    }

    internal void NotifyClawReturned()
    {
        if (_action?.Kind != ActionKind.BreakTiles)
            return;

        if (_action.InflightClawBreaks > 0)
            _action.InflightClawBreaks--;

        if (_action.TileTargets.Count == 0 && _action.InflightClawBreaks == 0)
            FinishAction(_action, $"breaktiles completed, broke {_action.BrokenTiles} tiles");
    }

    private void EnsureClaws()
    {
        int tick = (int)Main.GameUpdateCount;
        if (tick < _nextClawEnsureTick)
            return;

        _nextClawEnsureTick = tick + ClawEnsureIntervalTicks;
        EnsureClaw(side: -1);
        EnsureClaw(side: 1);
    }

    private void EnsureClaw(int side)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;
        if (FindClaw(side) != null)
            return;

        int projectileIndex = Projectile.NewProjectile(
            NPC.GetSource_FromAI(),
            NPC.Center + new Vector2(side * 34f, 30f),
            Vector2.Zero,
            ModContent.ProjectileType<ExampleTerraClawAgentClaw>(),
            0,
            0f,
            Main.myPlayer,
            NPC.whoAmI,
            side);

        if (projectileIndex >= 0 && projectileIndex < Main.maxProjectiles)
        {
            ref ProjectileReference reference = ref GetClawReference(side);
            reference.Set(Main.projectile[projectileIndex]);
        }
    }

    private bool TryGetIdleClaw(out ExampleTerraClawAgentClaw? claw)
    {
        claw = FindClaw(side: -1);
        if (claw?.CanAcceptBreakTarget == true)
            return true;

        claw = FindClaw(side: 1);
        if (claw?.CanAcceptBreakTarget == true)
            return true;

        claw = null;
        return false;
    }

    private ExampleTerraClawAgentClaw? FindClaw(int side)
    {
        int clawType = ModContent.ProjectileType<ExampleTerraClawAgentClaw>();
        ref ProjectileReference reference = ref GetClawReference(side);
        Projectile? cached = reference.IsValid() ? reference.Get() : null;
        if (IsOwnedClaw(cached, clawType, side) && cached!.ModProjectile is ExampleTerraClawAgentClaw cachedClaw)
            return cachedClaw;

        reference.Clear();
        for (int i = 0; i < Main.maxProjectiles; i++)
        {
            Projectile projectile = Main.projectile[i];
            if (IsOwnedClaw(projectile, clawType, side) && projectile.ModProjectile is ExampleTerraClawAgentClaw claw)
            {
                reference.Set(projectile);
                return claw;
            }
        }

        return null;
    }

    private bool IsOwnedClaw(Projectile? projectile, int clawType, int side)
    {
        if (projectile == null || !projectile.active || projectile.type != clawType)
            return false;
        if ((int)projectile.ai[0] != NPC.whoAmI)
            return false;
        return (projectile.ai[1] < 0f ? -1 : 1) == side;
    }

    private ref ProjectileReference GetClawReference(int side)
    {
        if (side < 0)
            return ref _leftClawReference;
        return ref _rightClawReference;
    }

    private static bool IsBreakableTile(Point point)
    {
        if (!WorldGen.InWorld(point.X, point.Y, 10))
            return false;
        Tile tile = Main.tile[point.X, point.Y];
        return tile.HasTile && Main.tileSolid[tile.TileType];
    }

    private void FinishImmediateAction(string action, string summary, bool callback)
    {
        _lastActionSummary = $"{action}: {summary}";
        if (callback)
            _pendingCallbackReason = _lastActionSummary;
    }

    private void FinishAction(PendingAction action, string summary)
    {
        if (action.Kind == ActionKind.BreakTiles)
            RecallClaws();

        _action = null;
        _lastActionSummary = summary;
        if (action.Callback)
            _pendingCallbackReason = summary;
    }

    private void ShowActionEmote(int emoteId)
    {
        int tick = (int)Main.GameUpdateCount;
        if (tick < _nextActionEmoteTick)
            return;

        EmoteBubble.NewBubble(emoteId, new WorldUIAnchor(NPC), ActionEmoteDurationTicks);
        _nextActionEmoteTick = tick + ActionEmoteCooldownTicks;
    }

    private void RecallClaws()
    {
        FindClaw(side: -1)?.Recall();
        FindClaw(side: 1)?.Recall();
    }

    private void EnqueueTurnSummary(LlmResult result)
    {
        // STM stores one compact model-authored note per normal request. Raw observation
        // remains request-local and is never copied into memory.
        string summary = result.String("summary");
        if (string.IsNullOrWhiteSpace(summary))
            summary = $"{result.Type}: {_lastActionSummary}";

        EnqueueTurnSummary(summary);
    }

    private void EnqueueTurnSummary(string summary)
    {
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

        if (_compactLlm.TryGetResult(out LlmResult result))
        {
            string updated = result.String("updated_ltm");
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

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Length <= maxLength ? value : value[..maxLength];
    }
    private static string FormatKnowledgeResult(KnowledgeQueryResult result)
    {
        if (result.Results.Count == 0)
            return $"No configured wiki results for '{Truncate(result.Query, 80)}'.";

        return string.Join(" | ", result.Results.Take(3).Select((item, index) =>
        {
            string text = !string.IsNullOrWhiteSpace(item.Extract) ? item.Extract : item.Snippet;
            if (string.IsNullOrWhiteSpace(text))
                text = "No summary available.";
            return $"{index + 1}. {item.Title}: {Truncate(text, 520)} Source: {item.Url}";
        }));
    }

    private static IEnumerable<string> SplitMemoryItems(string text)
    {
        return (text ?? "")
            .Split(new[] { '\n', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);
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
        public int InflightClawBreaks { get; set; }
    }
}

