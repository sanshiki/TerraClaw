using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TerraClaw.Interfaces;
using TerraClaw.Models;

namespace TerraClaw.Registries;

/// <summary>Validated registry for third-party LLM agent metadata.</summary>
public sealed class LlmAgentRegistry
{
    private static readonly Regex AgentIdPattern = new("^[a-z0-9_.:-]{3,96}$", RegexOptions.Compiled);

    private readonly Dictionary<string, LlmAgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public LlmAgentInfo Register(ILlmAgentDefinition definition)
    {
        var info = LlmAgentInfo.From(definition);
        Validate(info);

        lock (_sync)
        {
            if (_agents.ContainsKey(info.AgentId))
                throw new InvalidOperationException($"An LLM agent with id '{info.AgentId}' is already registered.");

            _agents.Add(info.AgentId, info);
        }

        return info;
    }

    public bool Has(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return false;

        lock (_sync)
            return _agents.ContainsKey(agentId);
    }

    public LlmAgentInfo? Get(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        lock (_sync)
            return _agents.TryGetValue(agentId, out var info) ? info : null;
    }

    public IReadOnlyCollection<LlmAgentInfo> GetAll()
    {
        lock (_sync)
            return _agents.Values
                .OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public void Clear()
    {
        lock (_sync)
            _agents.Clear();
    }

    private static void Validate(LlmAgentInfo info)
    {
        if (!AgentIdPattern.IsMatch(info.AgentId))
        {
            throw new ArgumentException(
                "Agent id must be 3-96 characters and contain only lowercase letters, numbers, '.', ':', '_' or '-'.",
                nameof(info));
        }

        if (string.IsNullOrWhiteSpace(info.DisplayName))
            throw new ArgumentException("Agent display name is required.", nameof(info));
        if (string.IsNullOrWhiteSpace(info.OwnerModName))
            throw new ArgumentException("Agent owner mod name is required.", nameof(info));
        if (string.IsNullOrWhiteSpace(info.Version))
            throw new ArgumentException("Agent version is required.", nameof(info));
    }
}
