#nullable enable

using System.Collections.Generic;

namespace TerraClaw.UI;

internal sealed class LlmDebugEntry
{
    public string RequestId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string Instruction { get; set; } = "";
    public int TimeoutMs { get; set; }
    public string Status { get; set; } = "pending";
    public double? DurationSeconds { get; set; }
    public string Error { get; set; } = "";
    public IReadOnlyList<string> ObservationKeys { get; set; } = [];
    public string SymbolicObservationJson { get; set; } = "";
    public string ObservationJson { get; set; } = "";
    public string OutputContractJson { get; set; } = "";
    public string OutputJson { get; set; } = "";
    public int CreatedTick { get; init; }
    public int UpdatedTick { get; set; }
}
