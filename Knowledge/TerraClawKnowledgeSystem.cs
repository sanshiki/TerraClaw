#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace TerraClaw.Knowledge;

/// <summary>
/// Runtime Terraria Wiki query service for agents.
/// Requests are non-blocking and should be polled from normal tModLoader update hooks.
/// </summary>
public sealed class TerraClawKnowledgeSystem : ModSystem
{
    /// <summary>Current loaded knowledge system instance, or null before the mod system is loaded.</summary>
    public static TerraClawKnowledgeSystem? Instance { get; private set; }

    private readonly object _sync = new();
    private readonly Dictionary<string, KnowledgeRequestHandle> _pending = new();
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _nextCacheCleanupTick;

    public override void Load()
    {
        Instance = this;
    }

    public override void Unload()
    {
        lock (_sync)
        {
            foreach (CancellationTokenSource cancellation in _cancellations.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }

            _cancellations.Clear();
            _pending.Clear();
            _cache.Clear();
        }

        Instance = null;
    }

    public override void PostUpdateEverything()
    {
        foreach (KnowledgeRequestHandle handle in PendingSnapshot())
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

        int tick = (int)Main.GameUpdateCount;
        if (tick >= _nextCacheCleanupTick)
        {
            _nextCacheCleanupTick = tick + 60 * 30;
            CleanupCache();
        }
    }

    /// <summary>
    /// Starts a Terraria Wiki query unless an equivalent pending query already exists.
    /// Returns immediately with a pollable handle.
    /// </summary>
    public KnowledgeRequestHandle Request(
        string agentId,
        string query,
        int limit = 0,
        int extractChars = 0,
        int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Knowledge query is required.", nameof(query));

        TerraClawKnowledgeConfig config = TerraClawKnowledgeConfig.Load();
        int resolvedLimit = Math.Clamp(limit > 0 ? limit : config.DefaultLimit, 1, 8);
        int resolvedExtractChars = Math.Clamp(extractChars > 0 ? extractChars : config.ExtractChars, 120, 2000);
        int resolvedTimeoutMs = Math.Clamp(timeoutMs > 0 ? timeoutMs : config.TimeoutMs, 1000, 60000);

        string normalizedQuery = query.Trim();
        string requestId = Guid.NewGuid().ToString();
        var handle = new KnowledgeRequestHandle(requestId, agentId, normalizedQuery, (int)Main.GameUpdateCount, resolvedTimeoutMs);

        if (!config.Enabled)
        {
            handle.Fail("TerraClaw knowledge queries are disabled.");
            return handle;
        }

        string cacheKey = CacheKey(config.WikiApi, normalizedQuery, resolvedLimit, resolvedExtractChars);
        lock (_sync)
        {
            if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                handle.Complete(cached.Result with { FromCache = true });
                return handle;
            }

            KnowledgeRequestHandle? existing = _pending.Values.FirstOrDefault(pending =>
                pending.IsPending
                && string.Equals(pending.AgentId, agentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pending.Query, normalizedQuery, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            _pending[requestId] = handle;
            _cancellations[requestId] = new CancellationTokenSource();
        }

        _ = RunRequestAsync(handle, config, resolvedLimit, resolvedExtractChars, cacheKey);
        return handle;
    }

    /// <summary>Cancels a pending knowledge request.</summary>
    public void Cancel(string requestId)
    {
        if (TryGetPending(requestId, out KnowledgeRequestHandle handle))
            CancelHandle(handle);
        DisposeCancellation(requestId, cancel: true);
        RemovePending(requestId);
    }

    private async Task RunRequestAsync(
        KnowledgeRequestHandle handle,
        TerraClawKnowledgeConfig config,
        int limit,
        int extractChars,
        string cacheKey)
    {
        CancellationToken cancellationToken = GetCancellationToken(handle.RequestId);
        try
        {
            using var timeout = new CancellationTokenSource(handle.TimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var client = new TerrariaWikiClient(config.WikiApi);
            IReadOnlyList<KnowledgeSearchResult> results = await client.QueryAsync(handle.Query, limit, extractChars, linked.Token);
            var payload = new KnowledgeQueryResult(handle.Query, results, false, config.WikiApi);

            StoreCache(cacheKey, payload, config.CacheSeconds);
            CompleteHandle(handle, payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (handle.Status == KnowledgeRequestStatus.TimedOut)
                return;
            CancelHandle(handle);
        }
        catch (OperationCanceledException)
        {
            TimeoutHandle(handle);
        }
        catch (Exception ex)
        {
            FailHandle(handle, ex.Message);
        }
        finally
        {
            DisposeCancellation(handle.RequestId, cancel: false);
        }
    }

    private CancellationToken GetCancellationToken(string requestId)
    {
        lock (_sync)
            return _cancellations.TryGetValue(requestId, out CancellationTokenSource? cancellation)
                ? cancellation.Token
                : CancellationToken.None;
    }

    private List<KnowledgeRequestHandle> PendingSnapshot()
    {
        lock (_sync)
            return _pending.Values.ToList();
    }

    private bool TryGetPending(string requestId, out KnowledgeRequestHandle handle)
    {
        lock (_sync)
            return _pending.TryGetValue(requestId, out handle!);
    }

    private void RemovePending(string requestId)
    {
        lock (_sync)
            _pending.Remove(requestId);
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

    private void CompleteHandle(KnowledgeRequestHandle handle, KnowledgeQueryResult result)
    {
        lock (_sync)
            handle.Complete(result);
    }

    private void FailHandle(KnowledgeRequestHandle handle, string error)
    {
        lock (_sync)
            handle.Fail(error);
    }

    private void CancelHandle(KnowledgeRequestHandle handle)
    {
        lock (_sync)
            handle.MarkCancelled();
    }

    private void TimeoutHandle(KnowledgeRequestHandle handle)
    {
        lock (_sync)
            handle.Timeout();
    }

    private void StoreCache(string cacheKey, KnowledgeQueryResult result, int cacheSeconds)
    {
        if (cacheSeconds <= 0)
            return;

        lock (_sync)
            _cache[cacheKey] = new CacheEntry(result, DateTimeOffset.UtcNow.AddSeconds(cacheSeconds));
    }

    private void CleanupCache()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            foreach (string key in _cache.Where(kvp => kvp.Value.ExpiresAt <= now).Select(kvp => kvp.Key).ToArray())
                _cache.Remove(key);
        }
    }

    private static string CacheKey(string wikiApi, string query, int limit, int extractChars)
    {
        return string.Join("\n", wikiApi.Trim(), query.Trim(), limit, extractChars);
    }

    private sealed record CacheEntry(KnowledgeQueryResult Result, DateTimeOffset ExpiresAt);
}
