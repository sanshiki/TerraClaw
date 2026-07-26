#nullable enable

using System;
using System.IO;
using System.Text.Json.Nodes;

namespace TerraClaw.Knowledge;

internal sealed record TerraClawKnowledgeConfig(
    bool Enabled,
    string WikiApi,
    int DefaultLimit,
    int ExtractChars,
    int TimeoutMs,
    int CacheSeconds)
{
    private const string DefaultWikiApi = "https://terraria.wiki.gg/api.php";

    public static TerraClawKnowledgeConfig Load()
    {
        JsonObject? fileConfig = LoadFileConfig();
        JsonObject? knowledge = fileConfig?["knowledge"] as JsonObject;

        return new TerraClawKnowledgeConfig(
            Enabled: ReadBool(knowledge, true, "enabled"),
            WikiApi: ReadString(knowledge, "wiki_api", "wikiApi") ?? Environment.GetEnvironmentVariable("TERRACLAW_KNOWLEDGE_WIKI_API") ?? DefaultWikiApi,
            DefaultLimit: ReadInt(knowledge, "default_limit", "defaultLimit") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_LIMIT") ?? 3,
            ExtractChars: ReadInt(knowledge, "extract_chars", "extractChars") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_EXTRACT_CHARS") ?? 700,
            TimeoutMs: ReadInt(knowledge, "timeout_ms", "timeoutMs") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_TIMEOUT_MS") ?? 20000,
            CacheSeconds: ReadInt(knowledge, "cache_seconds", "cacheSeconds") ?? ReadIntEnv("TERRACLAW_KNOWLEDGE_CACHE_SECONDS") ?? 300);
    }

    public static string ConfigPath
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "My Games", "Terraria", "tModLoader", "TerraClawConfig.json");
        }
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
}
