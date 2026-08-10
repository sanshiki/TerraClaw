using System;
using System.Collections.Generic;
using System.Linq;
using TerraClaw.Events;
using TerraClaw.Interfaces;
using TerraClaw.Knowledge;
using TerraClaw.LLM;
using TerraClaw.Models;
using TerraClaw.Registries;

namespace TerraClaw.API;

/// <summary>
/// Stable public entry point for other mods that build on TerraClaw.
/// Obtain this through <c>Mod.Call("GetAPI")</c> or <see cref="TerraClaw.Api"/>.
/// </summary>
public sealed class TerraClawApi
{
    public const string ApiVersion = "1.0.0";

    private static readonly IReadOnlySet<string> FeatureSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "api.v1",
        "modcall.v1",
        "llm.requests.v1",
        "llm.validation.v1",
        "llm.result.v1",
        "agent.registry.v1",
        "events.v1",
        "knowledge.query.v1",
        "knowledge.sources.v1",
    };

    private readonly LlmAgentRegistry _agentRegistry = new();
    private readonly Queue<LlmRequestFinishedEventArgs> _finishedRequests = new();
    private readonly object _eventSync = new();

    public event EventHandler<LlmAgentRegisteredEventArgs>? AgentRegistered;
    public event EventHandler<LlmRequestStartedEventArgs>? LlmRequestStarted;
    public event EventHandler<LlmRequestFinishedEventArgs>? LlmRequestFinished;

    public string Version => ApiVersion;

    public IReadOnlyCollection<string> Features => FeatureSet;

    public bool HasFeature(string feature)
    {
        return !string.IsNullOrWhiteSpace(feature) && FeatureSet.Contains(feature);
    }

    public void RegisterAgent(ILlmAgentDefinition definition)
    {
        var info = _agentRegistry.Register(definition);
        InvokeHandlers(AgentRegistered, this, new LlmAgentRegisteredEventArgs(info));
    }

    public void RegisterAgent(
        string agentId,
        string displayName,
        string description,
        string ownerModName,
        string version,
        IEnumerable<string>? tags = null)
    {
        RegisterAgent(new InlineLlmAgentDefinition(
            agentId,
            displayName,
            description,
            ownerModName,
            version,
            tags?.ToArray() ?? Array.Empty<string>()));
    }

    public bool HasAgent(string agentId)
    {
        return _agentRegistry.Has(agentId);
    }

    public LlmAgentInfo? GetAgent(string agentId)
    {
        return _agentRegistry.Get(agentId);
    }

    public IReadOnlyCollection<LlmAgentInfo> GetAgents()
    {
        return _agentRegistry.GetAll();
    }

    /// <summary>
    /// Sends a non-blocking LLM request through the framework bridge.
    /// Callers must poll the returned handle from their own tModLoader hooks.
    /// </summary>
    public LlmRequestHandle RequestLlm(
        string agentId,
        string systemPrompt,
        string instruction,
        LlmObservation observation,
        LlmOutput output,
        int timeoutMs = 30000)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException("System prompt is required.", nameof(systemPrompt));
        if (string.IsNullOrWhiteSpace(instruction))
            throw new ArgumentException("Instruction is required.", nameof(instruction));
        if (observation == null)
            throw new ArgumentNullException(nameof(observation));
        if (output == null)
            throw new ArgumentNullException(nameof(output));
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, "Timeout must be positive.");

        var bridge = LlmBridgeSystem.Instance
            ?? throw new InvalidOperationException("TerraClaw LLM bridge is not loaded.");

        return bridge.Request(agentId, systemPrompt, instruction, observation, output, timeoutMs);
    }

    /// <summary>Returns the configured and enabled wiki knowledge sources.</summary>
    public IReadOnlyList<KnowledgeSourceInfo> GetKnowledgeSources()
    {
        var knowledge = TerraClawKnowledgeSystem.Instance
            ?? throw new InvalidOperationException("TerraClaw knowledge system is not loaded.");

        return knowledge.GetSources();
    }

    /// <summary>Returns true when a configured and enabled wiki knowledge source id exists.</summary>
    public bool HasKnowledgeSource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return false;

        var knowledge = TerraClawKnowledgeSystem.Instance
            ?? throw new InvalidOperationException("TerraClaw knowledge system is not loaded.");

        return knowledge.HasSource(sourceId);
    }

    /// <summary>
    /// Sends a non-blocking configured wiki knowledge query through the framework service.
    /// Callers must poll the returned handle from their own tModLoader hooks.
    /// </summary>
    public KnowledgeRequestHandle RequestKnowledge(
        string agentId,
        string query,
        int limit = 0,
        int extractChars = 0,
        int timeoutMs = 0)
    {
        ValidateKnowledgeRequest(agentId, query, limit, extractChars, timeoutMs);

        var knowledge = TerraClawKnowledgeSystem.Instance
            ?? throw new InvalidOperationException("TerraClaw knowledge system is not loaded.");

        return knowledge.Request(agentId, query, limit, extractChars, timeoutMs);
    }

    /// <summary>
    /// Sends a non-blocking configured wiki knowledge query against the requested source ids.
    /// Callers must poll the returned handle from their own tModLoader hooks.
    /// </summary>
    public KnowledgeRequestHandle RequestKnowledgeFromSources(
        string agentId,
        string query,
        IEnumerable<string> sourceIds,
        int limit = 0,
        int extractChars = 0,
        int timeoutMs = 0)
    {
        ValidateKnowledgeRequest(agentId, query, limit, extractChars, timeoutMs);
        if (sourceIds == null)
            throw new ArgumentNullException(nameof(sourceIds));

        var knowledge = TerraClawKnowledgeSystem.Instance
            ?? throw new InvalidOperationException("TerraClaw knowledge system is not loaded.");

        return knowledge.Request(agentId, query, sourceIds, limit, extractChars, timeoutMs);
    }

    private static void ValidateKnowledgeRequest(string agentId, string query, int limit, int extractChars, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Knowledge query is required.", nameof(query));
        if (limit < 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit cannot be negative.");
        if (extractChars < 0)
            throw new ArgumentOutOfRangeException(nameof(extractChars), extractChars, "Extract character limit cannot be negative.");
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, "Timeout cannot be negative.");
    }

    internal void NotifyLlmRequestStarted(LlmRequestHandle handle)
    {
        InvokeHandlers(LlmRequestStarted, this, new LlmRequestStartedEventArgs(
            handle.RequestId,
            handle.AgentId,
            handle.TimeoutMs));
    }

    internal void NotifyLlmRequestFinished(LlmRequestHandle handle)
    {
        lock (_eventSync)
        {
            _finishedRequests.Enqueue(new LlmRequestFinishedEventArgs(
                handle.RequestId,
                handle.AgentId,
                handle.Status,
                handle.Error));
        }
    }

    internal void FlushQueuedEvents()
    {
        LlmRequestFinishedEventArgs[] finished;
        lock (_eventSync)
        {
            if (_finishedRequests.Count == 0)
                return;

            finished = _finishedRequests.ToArray();
            _finishedRequests.Clear();
        }

        foreach (var args in finished)
            InvokeHandlers(LlmRequestFinished, this, args);
    }

    internal void Clear()
    {
        AgentRegistered = null;
        LlmRequestStarted = null;
        LlmRequestFinished = null;
        lock (_eventSync)
            _finishedRequests.Clear();
        _agentRegistry.Clear();
    }

    private static void InvokeHandlers<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        object sender,
        TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers == null)
            return;

        foreach (var handlerDelegate in handlers.GetInvocationList())
        {
            if (handlerDelegate is not EventHandler<TEventArgs> handler)
                continue;

            try
            {
                handler(sender, args);
            }
            catch (Exception ex)
            {
                LogEventHandlerError(ex);
            }
        }
    }

    private static void LogEventHandlerError(Exception ex)
    {
        try
        {
            global::TerraClaw.TerraClaw.Instance?.Logger.Warn($"TerraClaw API event handler failed: {ex}");
        }
        catch
        {
        }
    }

    private sealed class InlineLlmAgentDefinition : ILlmAgentDefinition
    {
        public InlineLlmAgentDefinition(
            string agentId,
            string displayName,
            string description,
            string ownerModName,
            string version,
            IReadOnlyCollection<string> tags)
        {
            AgentId = agentId;
            DisplayName = displayName;
            Description = description;
            OwnerModName = ownerModName;
            Version = version;
            Tags = tags;
        }

        public string AgentId { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string OwnerModName { get; }
        public string Version { get; }
        public IReadOnlyCollection<string> Tags { get; }
    }
}

