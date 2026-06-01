#nullable enable

using System;
using System.IO;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

internal sealed record TerraClawLlmConfig(
    string ApiKey,
    string Model,
    string ApiBase,
    int? MaxTokens,
    float? Temperature)
{
    private const string DefaultModel = "gpt-4o-mini";

    public static TerraClawLlmConfig Load()
    {
        JsonObject? fileConfig = LoadFileConfig();
        JsonObject? llm = fileConfig?["llm"] as JsonObject;

        return new TerraClawLlmConfig(
            ApiKey: ReadString(llm, "api_key", "apiKey") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "",
            Model: ReadString(llm, "model") ?? Environment.GetEnvironmentVariable("LLM_MODEL") ?? DefaultModel,
            ApiBase: ReadString(llm, "api_base", "apiBase") ?? Environment.GetEnvironmentVariable("LLM_API_BASE") ?? "",
            MaxTokens: ReadInt(llm, "max_tokens", "maxTokens") ?? ReadIntEnv("LLM_MAX_TOKENS"),
            Temperature: ReadFloat(llm, "temperature") ?? ReadFloatEnv("LLM_TEMPERATURE"));
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

    private static float? ReadFloat(JsonObject? obj, params string[] keys)
    {
        if (obj == null)
            return null;

        foreach (string key in keys)
        {
            if (obj[key] == null)
                continue;
            if (obj[key]!.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                return obj[key]!.GetValue<float>();
        }

        return null;
    }

    private static int? ReadIntEnv(string name)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : null;
    }

    private static float? ReadFloatEnv(string name)
    {
        return float.TryParse(Environment.GetEnvironmentVariable(name), out float value) ? value : null;
    }
}
