#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

/// <summary>
/// Convenience reader for raw LLM JSON output.
/// It keeps the raw node available while avoiding repeated JsonObject casts in agents.
/// </summary>
public sealed class LlmResult
{
    private readonly JsonNode? _node;

    public LlmResult(JsonNode? node)
    {
        _node = node;
    }

    public JsonNode? Raw => _node;

    public bool IsObject => _node is JsonObject;

    public bool IsArray => _node is JsonArray;

    public string Type => String("type");

    public bool Is(string type) => string.Equals(Type, type, StringComparison.OrdinalIgnoreCase);

    public string String(string key, string fallback = "")
    {
        JsonNode? node = Get(key);
        if (node == null)
            return fallback;

        try
        {
            if (node is JsonValue value && value.TryGetValue<string>(out string? text))
                return text ?? fallback;
            return node.ToJsonString();
        }
        catch
        {
            return fallback;
        }
    }

    public int Int(string key, int fallback = 0)
    {
        JsonNode? node = Get(key);
        if (node == null)
            return fallback;

        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<int>(out int intValue))
                    return intValue;
                if (value.TryGetValue<double>(out double doubleValue))
                    return (int)Math.Round(doubleValue);
                if (value.TryGetValue<string>(out string? text) && int.TryParse(text, out int parsed))
                    return parsed;
            }
        }
        catch
        {
        }

        return fallback;
    }

    public double Number(string key, double fallback = 0)
    {
        JsonNode? node = Get(key);
        if (node == null)
            return fallback;

        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<double>(out double doubleValue))
                    return doubleValue;
                if (value.TryGetValue<int>(out int intValue))
                    return intValue;
                if (value.TryGetValue<string>(out string? text) && double.TryParse(text, out double parsed))
                    return parsed;
            }
        }
        catch
        {
        }

        return fallback;
    }

    public bool Bool(string key, bool fallback = false)
    {
        JsonNode? node = Get(key);
        if (node == null)
            return fallback;

        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out bool boolValue))
                    return boolValue;
                if (value.TryGetValue<int>(out int intValue))
                    return intValue != 0;
                if (value.TryGetValue<string>(out string? text) && bool.TryParse(text, out bool parsed))
                    return parsed;
            }
        }
        catch
        {
        }

        return fallback;
    }

    public IEnumerable<LlmResult> Items()
    {
        if (_node is JsonArray array)
        {
            foreach (var item in array)
                yield return new LlmResult(item);
            yield break;
        }

        if (_node is JsonObject obj && obj["outputs"] is JsonArray outputs)
        {
            foreach (var item in outputs)
                yield return new LlmResult(item);
        }
    }

    public bool TryGetBranch(string type, out LlmResult branch)
    {
        branch = new LlmResult(null);

        if (_node is JsonObject obj)
        {
            if (Is(type))
            {
                branch = this;
                return true;
            }

            if (obj[type] is JsonObject keyedObject)
            {
                branch = new LlmResult(keyedObject);
                return true;
            }

            if (obj[type] is JsonValue keyedValue)
            {
                branch = new LlmResult(new JsonObject { ["text"] = keyedValue.DeepClone() });
                return true;
            }
        }

        foreach (var item in Items())
        {
            if (item.TryGetBranch(type, out branch))
                return true;
        }

        return false;
    }

    public bool TryDeserialize<T>(out T? result)
    {
        result = default;
        if (_node == null)
            return false;

        try
        {
            result = _node.Deserialize<T>();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private JsonNode? Get(string key)
    {
        return _node is JsonObject obj ? obj[key] : null;
    }
}
