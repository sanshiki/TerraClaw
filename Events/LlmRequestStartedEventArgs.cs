using System;

namespace TerraClaw.Events;

public sealed class LlmRequestStartedEventArgs : EventArgs
{
    public LlmRequestStartedEventArgs(string requestId, string agentId, int timeoutMs)
    {
        RequestId = requestId;
        AgentId = agentId;
        TimeoutMs = timeoutMs;
    }

    public string RequestId { get; }
    public string AgentId { get; }
    public int TimeoutMs { get; }
}
