using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

/// <summary>Lifecycle state for a non-blocking LLM request.</summary>
public enum LlmRequestStatus
{
    Idle,
    Pending,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}

/// <summary>
/// Pollable handle returned by <see cref="LlmBridgeSystem.Request"/>.
/// Agents keep this handle while the LLM response is in flight and read the result later.
/// </summary>
public sealed class LlmRequestHandle
{
    private JsonNode? _output;

    /// <summary>Unique request id used to match runtime responses.</summary>
    public string RequestId { get; }

    /// <summary>Logical agent id that owns this request.</summary>
    public string AgentId { get; }

    /// <summary>Game tick when the request was created.</summary>
    public int StartedTick { get; }

    /// <summary>Timeout in milliseconds before the request is marked timed out.</summary>
    public int TimeoutMs { get; }

    /// <summary>Current request status.</summary>
    public LlmRequestStatus Status { get; private set; } = LlmRequestStatus.Pending;

    /// <summary>Failure or timeout text when the request did not complete successfully.</summary>
    public string? Error { get; private set; }

    /// <summary>True while the runtime response has not arrived and timeout has not fired.</summary>
    public bool IsPending => Status == LlmRequestStatus.Pending;

    /// <summary>True once the request reached any terminal state.</summary>
    public bool IsDone => Status is LlmRequestStatus.Completed or LlmRequestStatus.Failed
        or LlmRequestStatus.Cancelled or LlmRequestStatus.TimedOut;

    /// <summary>True only when the runtime returned a completed response.</summary>
    public bool IsCompleted => Status == LlmRequestStatus.Completed;

    internal LlmRequestHandle(string requestId, string agentId, int startedTick, int timeoutMs)
    {
        RequestId = requestId;
        AgentId = agentId;
        StartedTick = startedTick;
        TimeoutMs = timeoutMs;
    }

    /// <summary>Attempts to read the raw JSON output from a completed request.</summary>
    public bool TryGetResult(out JsonNode? output)
    {
        output = _output;
        return Status == LlmRequestStatus.Completed && output != null;
    }

    /// <summary>Attempts to deserialize the raw JSON output into a typed model.</summary>
    public bool TryGetResult<T>(out T? result)
    {
        result = default;
        if (Status != LlmRequestStatus.Completed || _output == null)
            return false;
        result = _output.Deserialize<T>();
        return true;
    }

    /// <summary>Cancels this request if it is still pending.</summary>
    public void Cancel()
    {
        if (IsDone)
            return;
        MarkCancelled();
        LlmBridgeSystem.Instance?.Cancel(RequestId);
    }

    internal void MarkCancelled()
    {
        if (IsDone)
            return;
        Status = LlmRequestStatus.Cancelled;
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
