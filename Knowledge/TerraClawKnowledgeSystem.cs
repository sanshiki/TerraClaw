#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace TerraClaw.Knowledge;

/// <summary>
/// Runtime MediaWiki knowledge query service for agents.
/// Requests are non-blocking and should be polled from normal tModLoader update hooks.
/// </summary>
public sealed class TerraClawKnowledgeSystem : ModSystem
{
    /// <summary>Current loaded knowledge system instance, or null before the mod system is loaded.</summary>
    public static TerraClawKnowledgeSystem? Instance { get; private set; }

    private static readonly HashSet<string> RankingStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "recipe", "recipes", "craft", "crafting", "make", "making", "drop", "drops", "loot", "obtain", "obtained", "get", "getting",
        "how", "to", "do", "i", "terraria", "wiki", "guide", "for", "of", "the", "mod", "calamity",
        "配方", "合成", "制作", "掉落", "掉落物", "掉率", "获取", "获得", "来源", "怎么", "如何", "什么", "泰拉瑞亚", "灾厄", "模组",
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, KnowledgeRequestHandle> _pending = new();
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingSourceFingerprints = new(StringComparer.OrdinalIgnoreCase);
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
            _pendingSourceFingerprints.Clear();
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

    /// <summary>Returns the currently configured and enabled wiki knowledge sources.</summary>
    public IReadOnlyList<KnowledgeSourceInfo> GetSources()
    {
        TerraClawKnowledgeConfig config = TerraClawKnowledgeConfig.Load();
        if (!config.Enabled)
            return Array.Empty<KnowledgeSourceInfo>();
        return config.Sources.Select(ToInfo).ToArray();
    }

    /// <summary>Returns true when the configured and enabled source id exists.</summary>
    public bool HasSource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return false;
        TerraClawKnowledgeConfig config = TerraClawKnowledgeConfig.Load();
        return config.Enabled && config.Sources.Any(source => string.Equals(source.Id, sourceId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts a configured wiki knowledge query against all enabled sources unless an equivalent pending query already exists.
    /// Returns immediately with a pollable handle.
    /// </summary>
    public KnowledgeRequestHandle Request(
        string agentId,
        string query,
        int limit = 0,
        int extractChars = 0,
        int timeoutMs = 0)
    {
        return Request(agentId, query, null, limit, extractChars, timeoutMs);
    }

    /// <summary>
    /// Starts a configured wiki knowledge query against the requested source ids unless an equivalent pending query already exists.
    /// Returns immediately with a pollable handle.
    /// </summary>
    public KnowledgeRequestHandle Request(
        string agentId,
        string query,
        IEnumerable<string>? sourceIds,
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

        IReadOnlyList<WikiSourceConfig> sources = ResolveSources(config.Sources, sourceIds, out string[] unknownSourceIds);
        if (unknownSourceIds.Length > 0)
        {
            handle.Fail("Unknown TerraClaw knowledge source ids: " + string.Join(", ", unknownSourceIds));
            return handle;
        }
        if (sources.Count == 0)
        {
            handle.Fail("No TerraClaw knowledge sources are configured for this request.");
            return handle;
        }

        string sourceFingerprint = SourceFingerprint(sources);
        string cacheKey = CacheKey(sourceFingerprint, normalizedQuery, resolvedLimit, resolvedExtractChars);
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
                && string.Equals(pending.Query, normalizedQuery, StringComparison.OrdinalIgnoreCase)
                && _pendingSourceFingerprints.TryGetValue(pending.RequestId, out string? pendingFingerprint)
                && string.Equals(pendingFingerprint, sourceFingerprint, StringComparison.Ordinal));
            if (existing != null)
                return existing;

            _pending[requestId] = handle;
            _pendingSourceFingerprints[requestId] = sourceFingerprint;
            _cancellations[requestId] = new CancellationTokenSource();
        }

        _ = RunRequestAsync(handle, config, sources, resolvedLimit, resolvedExtractChars, cacheKey);
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
        IReadOnlyList<WikiSourceConfig> sources,
        int limit,
        int extractChars,
        string cacheKey)
    {
        CancellationToken cancellationToken = GetCancellationToken(handle.RequestId);
        try
        {
            using var timeout = new CancellationTokenSource(handle.TimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            Task<SourceQueryResult>[] tasks = sources
                .Select(source => QuerySourceAsync(source, handle.Query, limit, extractChars, linked.Token))
                .ToArray();
            SourceQueryResult[] sourceResults = await Task.WhenAll(tasks);
            if (sourceResults.All(result => result.Error != null))
                throw new InvalidOperationException("All knowledge sources failed: " + string.Join(" | ", sourceResults.Select(result => result.Error)));

            IReadOnlyList<KnowledgeSearchResult> results = MergeAndRank(handle.Query, sourceResults, limit);
            string sourceSummary = string.Join(", ", sources.Select(source => source.Id));
            var payload = new KnowledgeQueryResult(handle.Query, results, false, sourceSummary);

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

    private static async Task<SourceQueryResult> QuerySourceAsync(
        WikiSourceConfig source,
        string query,
        int limit,
        int extractChars,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = new TerrariaWikiClient(source);
            IReadOnlyList<KnowledgeSearchResult> results = await client.QueryAsync(query, limit, extractChars, cancellationToken);
            return new SourceQueryResult(source, results, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceQueryResult(source, Array.Empty<KnowledgeSearchResult>(), $"{source.Id}: {ex.Message}");
        }
    }

    private static IReadOnlyList<KnowledgeSearchResult> MergeAndRank(
        string query,
        IEnumerable<SourceQueryResult> sourceResults,
        int limit)
    {
        string normalizedQuery = NormalizeSearchText(query);
        string[] terms = ExtractSearchTerms(query).ToArray();
        QueryLanguage queryLanguage = DetectQueryLanguage(query);
        var best = new Dictionary<string, ScoredResult>(StringComparer.OrdinalIgnoreCase);

        foreach (SourceQueryResult sourceResult in sourceResults.Where(result => result.Error == null))
        {
            foreach (KnowledgeSearchResult result in sourceResult.Results)
            {
                if (string.IsNullOrWhiteSpace(result.Title) && string.IsNullOrWhiteSpace(result.Extract) && string.IsNullOrWhiteSpace(result.Snippet))
                    continue;

                int score = ScoreResult(sourceResult.Source, result, normalizedQuery, terms, queryLanguage);
                string key = sourceResult.Source.Id + ":" + NormalizeSearchText(string.IsNullOrWhiteSpace(result.Url) ? result.Title : result.Url);
                if (!best.TryGetValue(key, out ScoredResult? existing) || score > existing.Score)
                    best[key] = new ScoredResult(result, score, sourceResult.Source.Priority);
            }
        }

        return best.Values
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.SourcePriority)
            .ThenBy(result => result.Result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(result => result.Result)
            .ToArray();
    }

    private static int ScoreResult(
        WikiSourceConfig source,
        KnowledgeSearchResult result,
        string normalizedQuery,
        string[] terms,
        QueryLanguage queryLanguage)
    {
        string title = NormalizeSearchText(result.Title);
        string snippet = NormalizeSearchText(result.Snippet);
        string extract = NormalizeSearchText(result.Extract);
        string body = title + " " + snippet + " " + extract;
        int score = source.Priority;

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            if (title.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                score += 260;
            else if (title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                score += 140;
            else if (body.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                score += 60;
        }

        int matchedTerms = 0;
        foreach (string term in terms)
        {
            bool matched = false;
            if (title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 55;
                matched = true;
            }
            if (snippet.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 24;
                matched = true;
            }
            if (extract.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 16;
                matched = true;
            }
            if (matched)
                matchedTerms++;
        }

        if (terms.Length > 0 && matchedTerms == terms.Length)
            score += 80;
        if (!string.IsNullOrWhiteSpace(result.Extract))
            score += 15;
        if (!string.IsNullOrWhiteSpace(result.Snippet))
            score += 5;
        if (MatchesLanguage(source.Language, queryLanguage))
            score += 45;
        else if (queryLanguage == QueryLanguage.Unknown || queryLanguage == QueryLanguage.Mixed)
            score += 10;

        return score;
    }

    private static IEnumerable<string> ExtractSearchTerms(string query)
    {
        foreach (Match match in Regex.Matches(query ?? "", @"[\u3400-\u9FFF]+|[A-Za-z0-9']+"))
        {
            string term = match.Value.Trim().ToLowerInvariant();
            if (term.Length == 0 || RankingStopWords.Contains(term))
                continue;
            yield return term;
        }
    }

    private static QueryLanguage DetectQueryLanguage(string query)
    {
        bool hasCjk = query.Any(ch => ch >= '\u3400' && ch <= '\u9FFF');
        bool hasLatin = query.Any(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (hasCjk && hasLatin)
            return QueryLanguage.Mixed;
        if (hasCjk)
            return QueryLanguage.Zh;
        if (hasLatin)
            return QueryLanguage.En;
        return QueryLanguage.Unknown;
    }

    private static bool MatchesLanguage(string sourceLanguage, QueryLanguage queryLanguage)
    {
        return queryLanguage switch
        {
            QueryLanguage.En => sourceLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase),
            QueryLanguage.Zh => sourceLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string NormalizeSearchText(string text)
    {
        return Regex.Replace((text ?? "").Replace('_', ' ').Trim(), @"\s+", " ").ToLowerInvariant();
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
        {
            _pending.Remove(requestId);
            _pendingSourceFingerprints.Remove(requestId);
        }
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

    private static IReadOnlyList<WikiSourceConfig> ResolveSources(
        IReadOnlyList<WikiSourceConfig> configuredSources,
        IEnumerable<string>? requestedSourceIds,
        out string[] unknownSourceIds)
    {
        if (requestedSourceIds == null)
        {
            unknownSourceIds = Array.Empty<string>();
            return configuredSources;
        }

        string[] ids = requestedSourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            unknownSourceIds = Array.Empty<string>();
            return configuredSources;
        }

        var byId = configuredSources.ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        var sources = new List<WikiSourceConfig>();
        var unknown = new List<string>();
        foreach (string id in ids)
        {
            if (byId.TryGetValue(id, out WikiSourceConfig? source))
                sources.Add(source);
            else
                unknown.Add(id);
        }

        unknownSourceIds = unknown.ToArray();
        return sources;
    }

    private static KnowledgeSourceInfo ToInfo(WikiSourceConfig source)
    {
        return new KnowledgeSourceInfo(source.Id, source.Api, source.PageBase, source.Language, source.Priority, source.Profile, source.Transport);
    }
    private static string SourceFingerprint(IEnumerable<WikiSourceConfig> sources)
    {
        return string.Join("\n", sources.Select(source => string.Join("|",
            source.Id,
            source.Api,
            source.PageBase,
            source.Language,
            source.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.Profile,
            source.Transport)));
    }

    private static string CacheKey(string sourceFingerprint, string query, int limit, int extractChars)
    {
        return string.Join("\n", sourceFingerprint, query.Trim(), limit, extractChars);
    }

    private enum QueryLanguage
    {
        Unknown,
        En,
        Zh,
        Mixed,
    }

    private sealed record SourceQueryResult(WikiSourceConfig Source, IReadOnlyList<KnowledgeSearchResult> Results, string? Error);

    private sealed record ScoredResult(KnowledgeSearchResult Result, int Score, int SourcePriority);

    private sealed record CacheEntry(KnowledgeQueryResult Result, DateTimeOffset ExpiresAt);
}