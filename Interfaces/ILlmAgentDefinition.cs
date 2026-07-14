using System.Collections.Generic;

namespace TerraClaw.Interfaces;

/// <summary>
/// Public metadata contract for a TerraClaw-compatible LLM agent supplied by any mod.
/// </summary>
public interface ILlmAgentDefinition
{
    string AgentId { get; }
    string DisplayName { get; }
    string Description { get; }
    string OwnerModName { get; }
    string Version { get; }
    IReadOnlyCollection<string> Tags { get; }
}
