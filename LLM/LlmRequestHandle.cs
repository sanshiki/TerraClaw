using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

public enum LlmRequestStatus
{
    Idle,
    Pending,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}

public sealed class LlmRequestHandle
{
    private JsonNode? _output;

    public string RequestId { get; }
    public string AgentId { get; }
    public int StartedTick { get; }
    public int TimeoutMs { get; }
    public LlmRequestStatus Status { get; private set; } = LlmRequestStatus.Pending;
    public string? Error { get; private set; }

    public bool IsPending => Status == LlmRequestStatus.Pending;
    public bool IsDone => Status is LlmRequestStatus.Completed or LlmRequestStatus.Failed
        or LlmRequestStatus.Cancelled or LlmRequestStatus.TimedOut;
    public bool IsCompleted => Status == LlmRequestStatus.Completed;

    internal LlmRequestHandle(string requestId, string agentId, int startedTick, int timeoutMs)
    {
        RequestId = requestId;
        AgentId = agentId;
        StartedTick = startedTick;
        TimeoutMs = timeoutMs;
    }

    public bool TryGetResult(out JsonNode? output)
    {
        output = _output;
        return Status == LlmRequestStatus.Completed && output != null;
    }

    public bool TryGetResult<T>(out T? result)
    {
        result = default;
        if (Status != LlmRequestStatus.Completed || _output == null)
            return false;
        result = _output.Deserialize<T>();
        return true;
    }

    public void Cancel()
    {
        if (IsDone)
            return;
        Status = LlmRequestStatus.Cancelled;
        LlmBridgeSystem.Instance?.Cancel(RequestId);
    }

    internal void Complete(JsonNode? output)
    {
        if (IsDone)
            return;
        _output = output;
        Status = LlmRequestStatus.Completed;
    }

    internal void Fail(string error)
    {
        if (IsDone)
            return;
        Error = error;
        Status = LlmRequestStatus.Failed;
    }

    internal void Timeout()
    {
        if (IsDone)
            return;
        Error = "LLM request timed out";
        Status = LlmRequestStatus.TimedOut;
    }
}
