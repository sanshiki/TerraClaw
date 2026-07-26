#nullable enable

using System;
using System.Collections.Generic;

namespace TerraClaw.Knowledge;

/// <summary>Lifecycle state for a non-blocking Terraria wiki knowledge request.</summary>
public enum KnowledgeRequestStatus
{
    Idle,
    Pending,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}

/// <summary>One Terraria Wiki search result returned by TerraClawKnowledge.</summary>
public sealed record KnowledgeSearchResult(
    string Title,
    string Url,
    string Extract,
    string Snippet);

/// <summary>Completed Terraria Wiki query payload.</summary>
public sealed record KnowledgeQueryResult(
    string Query,
    IReadOnlyList<KnowledgeSearchResult> Results,
    bool FromCache,
    string Source);

/// <summary>
/// Pollable handle returned by <see cref="TerraClawKnowledgeSystem.Request"/>.
/// Agents keep this handle while wiki data is in flight and read the result later.
/// </summary>
public sealed class KnowledgeRequestHandle
{
    private KnowledgeQueryResult? _result;

    /// <summary>Unique request id used to match runtime responses.</summary>
    public string RequestId { get; }

    /// <summary>Logical agent id that owns this request.</summary>
    public string AgentId { get; }

    /// <summary>Original search query.</summary>
    public string Query { get; }

    /// <summary>Game tick when the request was created.</summary>
    public int StartedTick { get; }

    /// <summary>Timeout in milliseconds before the request is marked timed out.</summary>
    public int TimeoutMs { get; }

    /// <summary>Current request status.</summary>
    public KnowledgeRequestStatus Status { get; private set; } = KnowledgeRequestStatus.Pending;

    /// <summary>Failure or timeout text when the request did not complete successfully.</summary>
    public string? Error { get; private set; }

    /// <summary>True while the runtime response has not arrived and timeout has not fired.</summary>
    public bool IsPending => Status == KnowledgeRequestStatus.Pending;

    /// <summary>True once the request reached any terminal state.</summary>
    public bool IsDone => Status is KnowledgeRequestStatus.Completed or KnowledgeRequestStatus.Failed
        or KnowledgeRequestStatus.Cancelled or KnowledgeRequestStatus.TimedOut;

    /// <summary>True only when the wiki query returned a completed response.</summary>
    public bool IsCompleted => Status == KnowledgeRequestStatus.Completed;

    internal KnowledgeRequestHandle(string requestId, string agentId, string query, int startedTick, int timeoutMs)
    {
        RequestId = requestId;
        AgentId = agentId;
        Query = query;
        StartedTick = startedTick;
        TimeoutMs = timeoutMs;
    }

    /// <summary>Attempts to read the completed wiki query result.</summary>
    public bool TryGetResult(out KnowledgeQueryResult result)
    {
        result = _result ?? new KnowledgeQueryResult(Query, Array.Empty<KnowledgeSearchResult>(), false, "");
        return Status == KnowledgeRequestStatus.Completed && _result != null;
    }

    /// <summary>Cancels this request if it is still pending.</summary>
    public void Cancel()
    {
        if (IsDone)
            return;
        MarkCancelled();
        TerraClawKnowledgeSystem.Instance?.Cancel(RequestId);
    }

    internal void MarkCancelled()
    {
        if (IsDone)
            return;
        Status = KnowledgeRequestStatus.Cancelled;
    }

    internal void Complete(KnowledgeQueryResult result)
    {
        if (IsDone)
            return;
        _result = result;
        Status = KnowledgeRequestStatus.Completed;
    }

    internal void Fail(string error)
    {
        if (IsDone)
            return;
        Error = error;
        Status = KnowledgeRequestStatus.Failed;
    }

    internal void Timeout()
    {
        if (IsDone)
            return;
        Error = "Knowledge request timed out";
        Status = KnowledgeRequestStatus.TimedOut;
    }
}
