using System;
using System.Collections.Generic;
using System.Linq;
using TerraClaw.Interfaces;

namespace TerraClaw.Models;

/// <summary>Immutable public snapshot of a registered LLM agent.</summary>
public sealed record LlmAgentInfo(
    string AgentId,
    string DisplayName,
    string Description,
    string OwnerModName,
    string Version,
    IReadOnlyList<string> Tags)
{
    public static LlmAgentInfo From(ILlmAgentDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));

        var tags = definition.Tags ?? Array.Empty<string>();

        return new LlmAgentInfo(
            definition.AgentId?.Trim() ?? "",
            definition.DisplayName?.Trim() ?? "",
            definition.Description?.Trim() ?? "",
            definition.OwnerModName?.Trim() ?? "",
            definition.Version?.Trim() ?? "",
            tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }
}
