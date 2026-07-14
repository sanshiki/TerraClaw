using System;
using TerraClaw.LLM;

namespace TerraClaw.Events;

public sealed class LlmRequestFinishedEventArgs : EventArgs
{
    public LlmRequestFinishedEventArgs(
        string requestId,
        string agentId,
        LlmRequestStatus status,
        string? error)
    {
        RequestId = requestId;
        AgentId = agentId;
        Status = status;
        Error = error;
    }

    public string RequestId { get; }
    public string AgentId { get; }
    public LlmRequestStatus Status { get; }
    public string? Error { get; }
}
