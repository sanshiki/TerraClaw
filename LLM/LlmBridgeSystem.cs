using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TerraClaw.UI;
using Terraria;
using Terraria.ModLoader;

namespace TerraClaw.LLM;

/// <summary>
/// tModLoader system that sends non-blocking LLM requests from C# and routes responses back to handles.
/// </summary>
public sealed class LlmBridgeSystem : ModSystem
{
    /// <summary>Current loaded bridge instance, or null before the mod system is loaded.</summary>
    public static LlmBridgeSystem? Instance { get; private set; }

    private readonly object _sync = new();
    private readonly Dictionary<string, LlmRequestHandle> _pending = new();
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly LlmClient _llm = new();

    public override void Load()
    {
        Instance = this;
    }

    public override void Unload()
    {
        lock (_sync)
        {
            foreach (var cancellation in _cancellations.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
            _cancellations.Clear();
            _pending.Clear();
        }
        Instance = null;
    }

    public override void PostUpdateEverything()
    {
        foreach (var handle in PendingSnapshot())
        {
            if (!handle.IsPending)
            {
                RemovePending(handle.RequestId);
                DisposeCancellation(handle.RequestId, cancel: false);
                continue;
            }

            int elapsedMs = (int)((Main.GameUpdateCount - handle.StartedTick) * (1000.0 / 60.0));
            if (elapsedMs > handle.TimeoutMs)
            {
                TimeoutHandle(handle);
                RemovePending(handle.RequestId);
                DisposeCancellation(handle.RequestId, cancel: true);
            }
        }

        global::TerraClaw.TerraClaw.Api?.FlushQueuedEvents();
    }

    /// <summary>
    /// Sends an LLM request unless the same agent already has a pending request.
    /// Returns a handle that callers can poll from AI hooks without blocking the game loop.
    /// </summary>
    public LlmRequestHandle Request(
        string agentId,
        string system,
        string instruction,
        LlmObservation observation,
        LlmOutput output,
        int timeoutMs = 30000)
    {
        string requestId = Guid.NewGuid().ToString();
        var handle = new LlmRequestHandle(requestId, agentId, (int)Main.GameUpdateCount, timeoutMs);

        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            var existing = _pending.Values.FirstOrDefault(h => h.AgentId == agentId && h.IsPending);
            if (existing != null)
            {
                cancellation.Dispose();
                return existing;
            }

            _pending[requestId] = handle;
            _cancellations[requestId] = cancellation;
        }

        global::TerraClaw.TerraClaw.Api?.NotifyLlmRequestStarted(handle);
        _ = RunRequestAsync(handle, system, instruction, observation, output, cancellation.Token);
        return handle;
    }

    /// <summary>Cancels a pending request.</summary>
    public void Cancel(string requestId)
    {
        if (TryGetPending(requestId, out var handle))
            CancelHandle(handle);
        DisposeCancellation(requestId, cancel: true);
        RemovePending(requestId);
    }

    private async Task RunRequestAsync(
        LlmRequestHandle handle,
        string system,
        string instruction,
        LlmObservation observation,
        LlmOutput output,
        CancellationToken cancellationToken)
    {
        JsonObject verboseObservation = observation.ToJson();
        JsonObject symbolicObservation = observation.ToSymbolicJson();
        JsonObject outputContract = output.ToContractJson();
        var stopwatch = Stopwatch.StartNew();

        PublishDebugRequest(
            handle,
            instruction,
            verboseObservation,
            symbolicObservation,
            outputContract);

        try
        {
            using var timeout = new CancellationTokenSource(handle.TimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var prompt = LlmPromptBuilder.Build(
                system,
                instruction,
                verboseObservation,
                symbolicObservation,
                outputContract);

            JsonNode? result = await _llm.GenerateAsync(
                prompt.SystemPrompt,
                prompt.UserPrompt,
                outputContract,
                linked.Token);

            LlmOutputValidationResult validation = output.Validate(result);
            if (!validation.IsValid)
            {
                string error = "LLM output failed validation: " + validation.ErrorMessage;
                PublishDebugResult(handle, "failed", stopwatch.Elapsed, result, error);
                FailHandle(handle, error);
                return;
            }

            PublishDebugResult(handle, "completed", stopwatch.Elapsed, result, error: null);
            CompleteHandle(handle, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (handle.Status == LlmRequestStatus.TimedOut)
                PublishDebugResult(handle, "timeout", stopwatch.Elapsed, output: null, error: "Request timed out");
            else
                PublishDebugResult(handle, "cancelled", stopwatch.Elapsed, output: null, error: "Request cancelled");
            CancelHandle(handle);
        }
        catch (OperationCanceledException)
        {
            PublishDebugResult(handle, "timeout", stopwatch.Elapsed, output: null, error: "Request timed out");
            TimeoutHandle(handle);
        }
        catch (Exception ex)
        {
            PublishDebugResult(handle, "failed", stopwatch.Elapsed, output: null, error: ex.Message);
            FailHandle(handle, ex.Message);
        }
        finally
        {
            DisposeCancellation(handle.RequestId, cancel: false);
        }
    }

    private static void PublishDebugRequest(
        LlmRequestHandle handle,
        string instruction,
        JsonObject observation,
        JsonObject symbolicObservation,
        JsonObject outputContract)
    {
        LlmDebugLog.AddRequest(
            handle.RequestId,
            handle.AgentId,
            instruction,
            handle.TimeoutMs,
            observation,
            symbolicObservation,
            outputContract,
            observation.Select(pair => pair.Key).ToArray());
    }

    private static void PublishDebugResult(
        LlmRequestHandle handle,
        string status,
        TimeSpan duration,
        JsonNode? output,
        string? error)
    {
        LlmDebugLog.AddResult(
            handle.RequestId,
            handle.AgentId,
            status,
            Math.Round(duration.TotalSeconds, 2),
            output,
            error);
    }

    private void DisposeCancellation(string requestId, bool cancel)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!_cancellations.Remove(requestId, out cancellation))
                return;
        }

        if (cancel)
            cancellation.Cancel();
        cancellation.Dispose();
    }

    private List<LlmRequestHandle> PendingSnapshot()
    {
        lock (_sync)
            return _pending.Values.ToList();
    }

    private bool TryGetPending(string requestId, out LlmRequestHandle handle)
    {
        lock (_sync)
            return _pending.TryGetValue(requestId, out handle!);
    }

    private void RemovePending(string requestId)
    {
        lock (_sync)
            _pending.Remove(requestId);
    }

    private void CompleteHandle(LlmRequestHandle handle, JsonNode? result)
    {
        bool notify = false;
        lock (_sync)
        {
            bool wasDone = handle.IsDone;
            handle.Complete(result);
            notify = !wasDone && handle.IsDone;
        }
        if (notify)
            global::TerraClaw.TerraClaw.Api?.NotifyLlmRequestFinished(handle);
    }

    private void FailHandle(LlmRequestHandle handle, string error)
    {
        bool notify = false;
        lock (_sync)
        {
            bool wasDone = handle.IsDone;
            handle.Fail(error);
            notify = !wasDone && handle.IsDone;
        }
        if (notify)
            global::TerraClaw.TerraClaw.Api?.NotifyLlmRequestFinished(handle);
    }

    private void CancelHandle(LlmRequestHandle handle)
    {
        bool notify = false;
        lock (_sync)
        {
            bool wasDone = handle.IsDone;
            handle.MarkCancelled();
            notify = !wasDone && handle.IsDone;
        }
        if (notify)
            global::TerraClaw.TerraClaw.Api?.NotifyLlmRequestFinished(handle);
    }

    private void TimeoutHandle(LlmRequestHandle handle)
    {
        bool notify = false;
        lock (_sync)
        {
            bool wasDone = handle.IsDone;
            handle.Timeout();
            notify = !wasDone && handle.IsDone;
        }
        if (notify)
            global::TerraClaw.TerraClaw.Api?.NotifyLlmRequestFinished(handle);
    }
}
