#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

internal sealed record LlmPrompt(string SystemPrompt, string UserPrompt);

internal static class LlmPromptBuilder
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    public static LlmPrompt Build(
        string system,
        string instruction,
        JsonObject observation,
        JsonObject? symbolicObservation,
        JsonObject outputContract)
    {
        JsonObject symbolic = symbolicObservation ?? FallbackSymbolic(observation);
        JsonObject data = symbolic["data"] as JsonObject ?? symbolic;
        JsonObject legend = symbolic["legend"] as JsonObject ?? new JsonObject();
        JsonObject docs = symbolic["docs"] as JsonObject ?? new JsonObject();
        JsonObject conventions = symbolic["conventions"] as JsonObject ?? new JsonObject();

        var systemParts = new[]
        {
            system.Trim(),
            FormatReturnRule(outputContract),
            FormatObservationLegend(legend, docs, conventions),
            FormatOutputContract(outputContract),
        }.Where(part => !string.IsNullOrWhiteSpace(part));

        string userPrompt =
            "Instruction:\n" +
            $"{instruction}\n\n" +
            "Observation:\n" +
            $"{ToCompactJson(data)}\n\n" +
            "Output contract:\n" +
            $"{ContractPayloadForUser(outputContract)}";

        return new LlmPrompt(string.Join("\n\n", systemParts), userPrompt);
    }

    private static string FormatObservationLegend(JsonObject legend, JsonObject docs, JsonObject conventions)
    {
        if (legend.Count == 0)
            return "";

        var lines = new List<string> { "Observation uses flat symbolic JSON:" };
        foreach (var item in legend)
        {
            if (item.Value is JsonArray fields)
                lines.Add($"{item.Key}=[{string.Join(",", fields.Select(FieldToString))}]");
            else
                lines.Add($"{item.Key}={item.Value}");
        }

        if (conventions.Count > 0)
        {
            lines.Add("");
            lines.Add("Conventions:");
            foreach (var item in conventions)
                lines.Add($"{item.Key}: {NodeToScalarString(item.Value)}");
        }

        var compactDocs = CompactDocs(docs);
        if (compactDocs.Count > 0)
        {
            lines.Add("");
            lines.Add("Field docs:");
            lines.AddRange(compactDocs);
        }

        return string.Join("\n", lines);
    }

    private static string FormatOutputContract(JsonObject outputContract)
    {
        if (outputContract["flat"] is JsonObject flat && flat["choices"] is JsonArray choices && choices.Count > 0)
            return FormatOutputFlat(flat);

        return FormatOutputLegend(outputContract["legend"] as JsonArray ?? new JsonArray());
    }

    private static string FormatReturnRule(JsonObject outputContract)
    {
        string? mode = (outputContract["flat"] as JsonObject)?["mode"]?.GetValue<string>();
        return mode == "any"
            ? "Return only JSON matching the output contract. For multiple outputs, return an array of JSON objects. Do not wrap it in markdown."
            : "Return only a single JSON object matching the output contract. Do not wrap it in markdown.";
    }

    private static string ContractPayloadForUser(JsonObject outputContract)
    {
        if (outputContract["flat"] is JsonObject flat && flat["choices"] is JsonArray choices && choices.Count > 0)
            return CompactFlatContract(flat);

        if (outputContract["legend"] is JsonArray legend && legend.Count > 0)
            return ToCompactJson(legend);

        JsonNode schema = outputContract["schema"] ?? outputContract;
        return ToCompactJson(schema);
    }

    private static string CompactFlatContract(JsonObject flat)
    {
        var parts = new List<string> { $"mode={flat["mode"]?.GetValue<string>() ?? "object"}" };
        parts.Add("return=top-level type field; no type-name wrapper");
        var choices = new List<string>();
        if (flat["choices"] is JsonArray choicesArray)
        {
            foreach (var choiceNode in choicesArray)
            {
                if (choiceNode is not JsonObject choice)
                    continue;

                string outputType = choice["type"]?.GetValue<string>() ?? "?";
                var rendered = new List<string>();
                if (choice["fields"] is JsonArray fields)
                {
                    foreach (var fieldNode in fields)
                    {
                        if (fieldNode is JsonObject field)
                            rendered.Add(RenderFlatField(field));
                    }
                }
                choices.Add($"{outputType}({string.Join(";", rendered)})");
            }
        }

        if (choices.Count > 0)
            parts.Add("choices=" + string.Join("|", choices));
        return string.Join("\n", parts);
    }

    private static string FormatOutputFlat(JsonObject flat)
    {
        string mode = flat["mode"]?.GetValue<string>() ?? "object";
        string modeLabel = mode switch
        {
            "one" => "choose exactly one",
            "any" => "choose one or more",
            "all" => "include all",
            "object" => "return",
            _ => mode,
        };

        var lines = new List<string> { "Output contract:", $"{modeLabel}:" };
        var typeNames = new List<string>();
        if (flat["choices"] is JsonArray choices)
        {
            foreach (var choiceNode in choices)
            {
                if (choiceNode is not JsonObject choice)
                    continue;

                string outputType = choice["type"]?.GetValue<string>() ?? "?";
                typeNames.Add(outputType);
                var rendered = new List<string>();
                if (choice["fields"] is JsonArray fields)
                {
                    foreach (var fieldNode in fields)
                    {
                        if (fieldNode is JsonObject field)
                            rendered.Add(RenderFlatField(field));
                    }
                }

                string body = string.Join(",", rendered);
                lines.Add($"{outputType}={{type:\"{outputType}\"{(body.Length > 0 ? "," + body : "")}}}");
            }
        }

        if (typeNames.Count > 0)
            lines.Add($"Required selector: type in [{string.Join(",", typeNames)}]");
        lines.Add(mode == "any"
            ? "Every returned object must include its own top-level type field."
            : "The returned object must include top-level type; do not wrap fields under the type name.");
        lines.Add("Field suffix: !=required, ?=optional.");
        return string.Join("\n", lines);
    }

    private static string FormatOutputLegend(JsonArray legend)
    {
        if (legend.Count == 0)
            return "";

        var lines = new List<string> { "Output choices:" };
        foreach (var itemNode in legend)
        {
            if (itemNode is not JsonObject item)
                continue;

            string outputType = item["type"]?.GetValue<string>() ?? "?";
            var fieldNames = new List<string>();
            if (item["fields"] is JsonArray fields)
            {
                foreach (var fieldNode in fields)
                {
                    if (fieldNode is not JsonObject field)
                        continue;
                    string suffix = field["required"]?.GetValue<bool>() == true ? "" : "?";
                    fieldNames.Add($"{field["name"]?.GetValue<string>() ?? "?"}{suffix}");
                }
            }
            lines.Add($"{outputType}={{type:\"{outputType}\"{(fieldNames.Count > 0 ? "," + string.Join(",", fieldNames) : "")}}}");
        }

        return string.Join("\n", lines);
    }

    private static string RenderFlatField(JsonObject field)
    {
        string name = field["name"]?.GetValue<string>() ?? "?";
        string fieldType = ShortType(field["type"]?.GetValue<string>() ?? "any");
        string required = field["required"]?.GetValue<bool>() == true ? "!" : "?";
        var parts = new List<string> { $"{name}:{fieldType}{required}" };
        if (field["max"] is JsonNode max)
            parts.Add($"max={NodeToScalarString(max)}");
        if (field["default"] is JsonNode defaultValue)
            parts.Add($"default={NodeToScalarString(defaultValue)}");
        return string.Join(" ", parts);
    }

    private static string ShortType(string fieldType)
    {
        return fieldType switch
        {
            "string" => "str",
            "number" => "num",
            "integer" => "int",
            "boolean" => "bool",
            _ => fieldType,
        };
    }

    private static List<string> CompactDocs(JsonObject docs)
    {
        var lines = new List<string>();
        foreach (var item in docs)
        {
            if (item.Value is JsonValue)
            {
                lines.Add($"{item.Key}: {NodeToScalarString(item.Value)}");
                continue;
            }

            if (item.Value is not JsonObject doc)
                continue;

            string desc = doc["description"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(desc))
                lines.Add($"{item.Key}: {desc}");

            if (doc["fields"] is JsonObject fields)
            {
                foreach (var field in fields)
                {
                    string fieldDesc = field.Value?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(fieldDesc))
                        lines.Add($"{item.Key}.{field.Key}: {fieldDesc}");
                }
            }
        }

        return lines;
    }

    private static JsonObject FallbackSymbolic(JsonObject observation)
    {
        var keys = new JsonArray();
        foreach (var item in observation)
            keys.Add(item.Key);

        return new JsonObject
        {
            ["data"] = new JsonObject { ["x"] = observation.DeepClone() },
            ["legend"] = new JsonObject { ["x"] = keys },
            ["docs"] = new JsonObject { ["x"] = "uncompressed observation fields" },
        };
    }

    private static string FieldToString(JsonNode? node) => NodeToScalarString(node);

    private static string NodeToScalarString(JsonNode? node)
    {
        if (node == null)
            return "";
        if (node is JsonValue value && value.TryGetValue<string>(out string? str))
            return str;
        return node.ToJsonString(CompactJsonOptions);
    }

    private static string ToCompactJson(JsonNode node) => node.ToJsonString(CompactJsonOptions);
}
