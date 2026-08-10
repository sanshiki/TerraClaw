#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TerraClaw.Knowledge;

internal sealed record TerraClawKnowledgeConfig(
    bool Enabled,
    string WikiApi,
    int DefaultLimit,
    int ExtractChars,
    int TimeoutMs,
    int CacheSeconds,
    IReadOnlyList<WikiSourceConfig> Sources)
{
    private const string DefaultWikiApi = "https://terraria.wiki.gg/api.php";
    private const string DefaultWikiPageBase = "https://terraria.wiki.gg/wiki/";
    private const string DefaultZhWikiApi = "https://terraria.wiki.gg/zh/api.php";
    private const string DefaultZhWikiPageBase = "https://terraria.wiki.gg/zh/wiki/";

    public static TerraClawKnowledgeConfig Load()
    {
        JsonObject? fileConfig = LoadFileConfig();
        JsonObject? knowledge = fileConfig?["knowledge"] as JsonObject;
        string? configuredWikiApi = ReadString(knowledge, "wiki_api", "wikiApi")
            ?? Environment.GetEnvironmentVariable("TERRACLAW_KNOWLEDGE_WIKI_API");
        IReadOnlyList<WikiSourceConfig> sources = ReadSources(knowledge, configuredWikiApi);

        return new TerraClawKnowledgeConfig(
            Enabled: ReadBool(knowledge, true, "enabled"),
            WikiApi: sources.Count > 0 ? sources[0].Api : configuredWikiApi ?? DefaultWikiApi,
            DefaultLimit: ReadInt(knowledge, "default_limit", "defaultLimit") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_LIMIT") ?? 3,
            ExtractChars: ReadInt(knowledge, "extract_chars", "extractChars") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_EXTRACT_CHARS") ?? 700,
            TimeoutMs: ReadInt(knowledge, "timeout_ms", "timeoutMs") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_TIMEOUT_MS") ?? 20000,
            CacheSeconds: ReadInt(knowledge, "cache_seconds", "cacheSeconds") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_CACHE_SECONDS") ?? 300,
            Sources: sources);
    }

    public static string ConfigPath
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "My Games", "Terraria", "tModLoader", "TerraClawConfig.json");
        }
    }

    private static IReadOnlyList<WikiSourceConfig> ReadSources(JsonObject? knowledge, string? configuredWikiApi)
    {
        if (knowledge?["sources"] is JsonArray sourcesArray)
        {
            var sources = new List<WikiSourceConfig>();
            foreach (JsonNode? sourceNode in sourcesArray)
            {
                if (sourceNode is not JsonObject sourceObject)
                    continue;
                if (!ReadBool(sourceObject, true, "enabled"))
                    continue;

                string? api = ReadString(sourceObject, "api", "wiki_api", "wikiApi");
                if (string.IsNullOrWhiteSpace(api))
                    continue;

                string id = ReadString(sourceObject, "id", "source_id", "sourceId") ?? MakeSourceId(api);
                string language = ReadString(sourceObject, "language", "lang") ?? InferLanguage(api);
                string profile = ReadString(sourceObject, "profile") ?? DefaultProfile(language);
                string pageBase = ReadString(sourceObject, "page_base", "pageBase", "wiki_base", "wikiBase") ?? InferPageBase(api);
                int priority = ReadInt(sourceObject, "priority") ?? DefaultPriority(id, language);
                string transport = NormalizeTransport(ReadString(sourceObject, "transport") ?? "http");

                sources.Add(new WikiSourceConfig(
                    Id: NormalizeId(id),
                    Api: api.Trim(),
                    PageBase: NormalizePageBase(pageBase),
                    Language: language.Trim(),
                    Priority: priority,
                    Profile: profile.Trim(),
                    Transport: transport));
            }

            return DedupeSources(sources);
        }

        if (!string.IsNullOrWhiteSpace(configuredWikiApi) && !string.Equals(configuredWikiApi.Trim(), DefaultWikiApi, StringComparison.OrdinalIgnoreCase))
        {
            string api = configuredWikiApi.Trim();
            string language = InferLanguage(api);
            return new[]
            {
                new WikiSourceConfig(
                    Id: MakeSourceId(api),
                    Api: api,
                    PageBase: NormalizePageBase(InferPageBase(api)),
                    Language: language,
                    Priority: DefaultPriority(api, language),
                    Profile: DefaultProfile(language),
                    Transport: "http"),
            };
        }

        return new[]
        {
            new WikiSourceConfig("terraria-en", DefaultWikiApi, DefaultWikiPageBase, "en", 100, "terraria-en", "http"),
            new WikiSourceConfig("terraria-zh", DefaultZhWikiApi, DefaultZhWikiPageBase, "zh", 95, "terraria-zh", "http"),
        };
    }

    private static IReadOnlyList<WikiSourceConfig> DedupeSources(IEnumerable<WikiSourceConfig> sources)
    {
        var result = new List<WikiSourceConfig>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WikiSourceConfig source in sources)
        {
            string key = source.Id;
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;
            result.Add(source);
        }
        return result;
    }

    private static JsonObject? LoadFileConfig()
    {
        string path = ConfigPath;
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject? obj, params string[] keys)
    {
        if (obj == null)
            return null;

        foreach (string key in keys)
        {
            string value = obj[key]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? ReadInt(JsonObject? obj, params string[] keys)
    {
        if (obj == null)
            return null;

        foreach (string key in keys)
        {
            if (obj[key] == null)
                continue;
            if (obj[key]!.GetValueKind() == System.Text.Json.JsonValueKind.Number && obj[key]!.GetValue<int>() is int value)
                return value;
        }

        return null;
    }

    private static bool ReadBool(JsonObject? obj, bool defaultValue, params string[] keys)
    {
        if (obj == null)
            return defaultValue;

        foreach (string key in keys)
        {
            if (obj[key] == null)
                continue;
            if (obj[key]!.GetValueKind() == System.Text.Json.JsonValueKind.True)
                return true;
            if (obj[key]!.GetValueKind() == System.Text.Json.JsonValueKind.False)
                return false;
        }

        return defaultValue;
    }

    private static int? ReadIntEnv(string name)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : null;
    }

    private static string NormalizeTransport(string transport)
    {
        transport = transport.Trim().ToLowerInvariant();
        return transport == "curl" ? "curl" : "http";
    }

    private static string InferLanguage(string api)
    {
        string text = api.ToLowerInvariant();
        if (text.Contains("/zh/", StringComparison.Ordinal) || text.Contains("huijiwiki", StringComparison.Ordinal))
            return "zh";
        return "en";
    }

    private static string DefaultProfile(string language)
    {
        return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "terraria-zh" : "terraria-en";
    }

    private static int DefaultPriority(string id, string language)
    {
        if (id.Contains("terraria", StringComparison.OrdinalIgnoreCase))
            return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 95 : 100;
        return 80;
    }

    private static string InferPageBase(string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out Uri? uri))
            return api.Replace("api.php", "wiki/", StringComparison.OrdinalIgnoreCase);

        string path = uri.AbsolutePath;
        if (path.EndsWith("/api.php", StringComparison.OrdinalIgnoreCase))
            path = path[..^"api.php".Length] + "wiki/";
        else if (!path.EndsWith("/wiki/", StringComparison.OrdinalIgnoreCase))
            path = "/wiki/";

        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = "",
            Fragment = "",
        };
        return builder.Uri.ToString();
    }

    private static string NormalizePageBase(string pageBase)
    {
        pageBase = pageBase.Trim();
        return pageBase.EndsWith("/", StringComparison.Ordinal) ? pageBase : pageBase + "/";
    }

    private static string MakeSourceId(string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out Uri? uri))
            return NormalizeId(api);

        string host = uri.Host.Replace(".", "-", StringComparison.OrdinalIgnoreCase);
        string path = uri.AbsolutePath.Replace("/", "-", StringComparison.OrdinalIgnoreCase).Trim('-');
        return NormalizeId(string.IsNullOrWhiteSpace(path) ? host : host + "-" + path.Replace("-api-php", "", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeId(string id)
    {
        string normalized = Regex.Replace(id.Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "wiki" : normalized;
    }
}

internal sealed record WikiSourceConfig(
    string Id,
    string Api,
    string PageBase,
    string Language,
    int Priority,
    string Profile,
    string Transport);