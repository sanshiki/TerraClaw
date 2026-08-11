using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

/// <summary>
/// Adds structured observation data to an LLM request.
/// Implement this when a context block only needs verbose JSON for debugging or fallback prompts.
/// </summary>
public interface IContextProvider
{
    /// <summary>Adds this provider's verbose JSON data to the observation root.</summary>
    void AddContext(JsonObject target);
}

/// <summary>Describes one positional field in a compact symbolic observation array.</summary>
public sealed record SymbolicField(string Name, string Description = "");

/// <summary>
/// Adds both verbose JSON and compact symbolic data to an LLM request.
/// Symbolic providers are preferred for reusable context because the prompt builder can generate legends automatically.
/// </summary>
public interface ISymbolicContextProvider : IContextProvider
{
    /// <summary>Short key used in the flat symbolic observation, for example <c>npc</c> or <c>time</c>.</summary>
    string Symbol { get; }

    /// <summary>Human-readable description of this symbolic context block.</summary>
    string Description { get; }

    /// <summary>Ordered field definitions matching <see cref="ToSymbolicValues"/>.</summary>
    IReadOnlyList<SymbolicField> Fields { get; }

    /// <summary>Returns compact values in exactly the same order as <see cref="Fields"/>.</summary>
    JsonArray ToSymbolicValues();
}

/// <summary>
/// Request-local observation builder.
/// It keeps verbose JSON for dashboard/debugging and symbolic JSON for token-efficient prompts.
/// </summary>
public sealed class LlmObservation
{
    private readonly JsonObject _data = new();
    private readonly JsonObject _symbolicData = new();
    private readonly JsonObject _legend = new();
    private readonly JsonObject _docs = new();
    private readonly JsonObject _extensions = new();

    public static LlmObservation Create() => new();

    /// <summary>
    /// Adds a raw extension field.
    /// Prefer <see cref="Custom"/> when the field should appear in generated prompt docs.
    /// </summary>
    public LlmObservation With(string key, object? value)
    {
        var node = JsonSerializer.SerializeToNode(value);
        _data[key] = node?.DeepClone();
        _extensions[key] = node;
        return this;
    }

    /// <summary>
    /// Adds a small custom extension field under symbolic key <c>x</c>.
    /// Use <see cref="Context.Custom"/> for multi-field symbolic custom state.
    /// </summary>
    public LlmObservation Custom(string key, object? value, string description = "")
    {
        With(key, value);
        if (!string.IsNullOrWhiteSpace(description))
        {
            if (_docs["x"] is not JsonObject xDocs)
            {
                xDocs = new JsonObject();
                _docs["x"] = xDocs;
            }
            xDocs[key] = description;
        }
        return this;
    }

    /// <summary>Adds one context provider to both verbose and symbolic observation data when supported.</summary>
    public LlmObservation With(IContextProvider provider)
    {
        provider.AddContext(_data);
        if (provider is ISymbolicContextProvider symbolic)
        {
            _symbolicData[symbolic.Symbol] = symbolic.ToSymbolicValues();
            var fieldNames = new JsonArray();
            var fieldDocs = new JsonObject();
            foreach (var field in symbolic.Fields)
            {
                fieldNames.Add(field.Name);
                if (!string.IsNullOrWhiteSpace(field.Description))
                    fieldDocs[field.Name] = field.Description;
            }
            _legend[symbolic.Symbol] = fieldNames;
            if (!string.IsNullOrWhiteSpace(symbolic.Description) || fieldDocs.Count > 0)
            {
                _docs[symbolic.Symbol] = new JsonObject
                {
                    ["description"] = symbolic.Description,
                    ["fields"] = fieldDocs,
                };
            }
        }
        return this;
    }

    /// <summary>Preferred alias for adding a single context provider.</summary>
    public LlmObservation Use(IContextProvider provider) => With(provider);

    /// <summary>Adds a fluent custom symbolic component created by <see cref="Context.Custom"/>.</summary>
    public LlmObservation Use(CustomContextBuilder builder) => With(builder.Build());

    /// <summary>Adds all context providers in a reusable component bundle.</summary>
    public LlmObservation Use(ContextBundle bundle)
    {
        foreach (var provider in bundle.Components)
            With(provider);
        return this;
    }

    /// <summary>Returns the verbose observation JSON used by dashboards and fallback prompts.</summary>
    public JsonObject ToJson() => _data.DeepClone().AsObject();

    /// <summary>Returns compact symbolic observation data, generated legends, docs, and shared conventions.</summary>
    public JsonObject ToSymbolicJson()
    {
        var data = _symbolicData.DeepClone().AsObject();
        if (_extensions.Count > 0)
            data["x"] = _extensions.DeepClone();

        var legend = _legend.DeepClone().AsObject();
        if (_extensions.Count > 0)
        {
            var extensionFields = new JsonArray();
            foreach (var kvp in _extensions)
                extensionFields.Add(kvp.Key);
            legend["x"] = extensionFields;
        }

        var docs = _docs.DeepClone().AsObject();
        if (_extensions.Count > 0 && !docs.ContainsKey("x"))
            docs["x"] = "custom extension fields";

        return new JsonObject
        {
            ["data"] = data,
            ["legend"] = legend,
            ["docs"] = docs,
            ["conventions"] = new JsonObject
            {
                ["booleans"] = "0=false, 1=true",
                ["dir"] = "l=left, r=right",
            },
        };
    }
}

/// <summary>
/// Fluent collection of reusable observation components.
/// Builders such as <see cref="NpcContextBuilder"/> inherit from this type.
/// </summary>
public class ContextBundle
{
    private readonly List<IContextProvider> _components = new();

    public IReadOnlyList<IContextProvider> Components => _components;

    /// <summary>Adds one component to the bundle and returns the bundle for chaining.</summary>
    public ContextBundle Add(IContextProvider component)
    {
        _components.Add(component);
        return this;
    }
}

/// <summary>
/// Builds a named custom symbolic context block without requiring a dedicated provider class.
/// Use this for small agent-owned state such as emotions, modes, queues, or trigger metadata.
/// </summary>
public sealed class CustomContextBuilder
{
    private readonly string _symbol;
    private readonly string _description;
    private readonly JsonObject _verbose = new();
    private readonly JsonArray _values = new();
    private readonly List<SymbolicField> _fields = new();

    internal CustomContextBuilder(string symbol, string description = "")
    {
        _symbol = symbol;
        _description = description;
    }

    /// <summary>Adds one ordered field to this custom symbolic block.</summary>
    public CustomContextBuilder Field(string name, object? value, string description = "")
    {
        _verbose[name] = JsonSerializer.SerializeToNode(value);
        _values.Add(JsonSerializer.SerializeToNode(value));
        _fields.Add(new SymbolicField(name, description));
        return this;
    }

    /// <summary>Converts the builder into an immutable context provider for request assembly.</summary>
    public IContextProvider Build() => new CustomSymbolicContextProvider(_symbol, _description, _fields, _verbose, _values);

    public static implicit operator ContextBundle(CustomContextBuilder builder)
        => new ContextBundle().Add(builder.Build());
}

/// <summary>Factory helpers for custom symbolic context blocks.</summary>
public static class Context
{
    /// <summary>Starts a named custom symbolic context block.</summary>
    public static CustomContextBuilder Custom(string symbol, string description = "") => new(symbol, description);
}

internal sealed class CustomSymbolicContextProvider : ISymbolicContextProvider
{
    private readonly JsonObject _verbose;
    private readonly JsonArray _values;

    public CustomSymbolicContextProvider(
        string symbol,
        string description,
        IReadOnlyList<SymbolicField> fields,
        JsonObject verbose,
        JsonArray values)
    {
        Symbol = symbol;
        Description = description;
        Fields = fields;
        _verbose = verbose;
        _values = values;
    }

    public string Symbol { get; }
    public string Description { get; }
    public IReadOnlyList<SymbolicField> Fields { get; }

    public void AddContext(JsonObject target)
    {
        target[Symbol] = _verbose.DeepClone();
    }

    public JsonArray ToSymbolicValues() => _values.DeepClone().AsArray();
}

/// <summary>Result of validating raw LLM output against a TerraClaw output contract.</summary>
public sealed class LlmOutputValidationResult
{
    public LlmOutputValidationResult(bool isValid, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }
    public string ErrorMessage => string.Join("; ", Errors);

    public static LlmOutputValidationResult Valid { get; } = new(true, Array.Empty<string>());

    public static LlmOutputValidationResult Invalid(params string[] errors) => new(false, errors);
}

public sealed class LlmOutput
{
    private readonly JsonObject _schema;
    private readonly string _description;
    private readonly JsonArray _legend;
    private readonly JsonObject _flat;

    private LlmOutput(JsonObject schema, string description = "", JsonArray? legend = null, JsonObject? flat = null)
    {
        _schema = schema;
        _description = description;
        _legend = legend ?? new JsonArray();
        _flat = flat ?? new JsonObject();
    }

    /// <summary>Starts an output object branch with a required <c>type</c> discriminator.</summary>
    public static LlmObjectBuilder Object(string name, string description = "")
        => new(name, description);

    /// <summary>Requires the model to return exactly one of the listed output shapes.</summary>
    public static LlmOutput OneOf(params LlmOutput[] outputs)
        => Composite("oneOf", "Return exactly one of the listed output shapes.", outputs);

    /// <summary>Allows the model to return one or more of the listed output shapes.</summary>
    public static LlmOutput AnyOf(params LlmOutput[] outputs)
        => Composite("anyOf", "Return one or more of the listed output shapes.", outputs);

    /// <summary>Asks the model to satisfy all listed output shapes.</summary>
    public static LlmOutput AllOf(params LlmOutput[] outputs)
        => Composite("allOf", "Return an output satisfying all listed shapes.", outputs);

    /// <summary>Combines this output contract with another using <see cref="AllOf"/>.</summary>
    public LlmOutput And(LlmOutput other) => AllOf(this, other);

    /// <summary>Returns the full JSON Schema representation of this contract.</summary>
    public JsonObject ToJsonSchema() => CloneObject(_schema);

    /// <summary>Returns schema, generated legend, and flat prompt contract metadata.</summary>
    public JsonObject ToContractJson()
    {
        var result = new JsonObject
        {
            ["schema"] = ToJsonSchema(),
            ["legend"] = _legend.DeepClone(),
            ["flat"] = _flat.DeepClone(),
        };
        if (!string.IsNullOrWhiteSpace(_description))
            result["description"] = _description;
        return result;
    }


    /// <summary>Validates raw LLM output against this contract's TerraClaw output semantics.</summary>
    public LlmOutputValidationResult Validate(JsonNode? output)
    {
        return LlmOutputValidator.Validate(Normalize(output), _flat);
    }

    /// <summary>Returns a canonical typed-object form for common valid-but-sloppy LLM output shapes.</summary>
    internal JsonNode? Normalize(JsonNode? output)
    {
        return LlmOutputNormalizer.Normalize(output, _flat);
    }

    private static LlmOutput Composite(string key, string description, LlmOutput[] outputs)
    {
        var array = new JsonArray();
        foreach (var output in outputs)
            array.Add(output.ToJsonSchema());
        var legend = new JsonArray();
        var flatChoices = new JsonArray();
        foreach (var output in outputs)
        {
            foreach (var item in output._legend)
                legend.Add(item?.DeepClone());
            foreach (var choice in output.GetFlatChoices())
                flatChoices.Add(choice?.DeepClone());
        }
        return new LlmOutput(new JsonObject { [key] = array }, description, legend, new JsonObject
        {
            ["mode"] = key switch
            {
                "oneOf" => "one",
                "anyOf" => "any",
                "allOf" => "all",
                _ => key,
            },
            ["description"] = description,
            ["choices"] = flatChoices,
        });
    }

    internal static LlmOutput FromObject(string name, string description, JsonObject properties, JsonArray required, JsonArray fieldLegend)
    {
        properties["type"] = new JsonObject
        {
            ["type"] = "string",
            ["const"] = name,
            ["description"] = "Output branch identifier.",
        };
        required.Add("type");

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = required,
        };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        var legend = new JsonArray
        {
            new JsonObject
            {
                ["type"] = name,
                ["description"] = description,
                ["fields"] = fieldLegend,
            },
        };
        var flatChoice = new JsonObject
        {
            ["type"] = name,
            ["description"] = description,
            ["fields"] = fieldLegend.DeepClone(),
        };
        return new LlmOutput(schema, description, legend, new JsonObject
        {
            ["mode"] = "object",
            ["choices"] = new JsonArray(flatChoice),
        });
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return source.DeepClone().AsObject();
    }

    private JsonArray GetFlatChoices()
    {
        if (_flat["choices"] is JsonArray choices)
            return choices.DeepClone().AsArray();
        return new JsonArray();
    }
}

/// <summary>Fluent builder for one discriminated object output branch.</summary>
public sealed class LlmObjectBuilder
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonObject _properties = new();
    private readonly JsonArray _required = new();
    private readonly JsonArray _fieldLegend = new();

    internal LlmObjectBuilder(string name, string description)
    {
        _name = name;
        _description = description;
    }

    /// <summary>Adds a string field to the output object.</summary>
    public LlmObjectBuilder String(string name, string description = "", bool required = true, int? maxLength = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (maxLength.HasValue)
            schema["maxLength"] = maxLength.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

    /// <summary>Adds a string field to the output object using the legacy positional required argument.</summary>
    public LlmObjectBuilder String(string name, bool isRequired, int? maxLength = null, string description = "")
        => String(name, description, isRequired, maxLength);

    /// <summary>Adds a numeric field to the output object.</summary>
    public LlmObjectBuilder Number(string name, string description = "", bool required = true, double? defaultValue = null)
    {
        var schema = new JsonObject { ["type"] = "number" };
        if (defaultValue.HasValue)
            schema["default"] = defaultValue.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

    /// <summary>Adds a numeric field to the output object using the legacy positional required argument.</summary>
    public LlmObjectBuilder Number(string name, bool isRequired, double? defaultValue = null, string description = "")
        => Number(name, description, isRequired, defaultValue);

    /// <summary>Adds a boolean field to the output object.</summary>
    public LlmObjectBuilder Boolean(string name, string description = "", bool required = true)
    {
        var schema = new JsonObject { ["type"] = "boolean" };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

    /// <summary>Adds a boolean field to the output object.</summary>
    public LlmObjectBuilder Boolean(string name, bool isRequired, string description = "")
        => Boolean(name, description, isRequired);

    /// <summary>Builds the final output contract branch.</summary>
    public LlmOutput Build() => LlmOutput.FromObject(_name, _description, _properties, _required, _fieldLegend);

    public static implicit operator LlmOutput(LlmObjectBuilder builder) => builder.Build();

    private LlmObjectBuilder Add(string name, JsonObject schema, bool required, string description)
    {
        _properties[name] = schema;
        if (required)
            _required.Add(name);
        var item = new JsonObject
        {
            ["name"] = name,
        };
        if (!string.IsNullOrWhiteSpace(description))
            item["description"] = description;
        item["type"] = schema["type"]?.GetValue<string>() ?? "any";
        item["required"] = required;
        if (schema["maxLength"] is JsonNode maxLength)
            item["max"] = maxLength.DeepClone();
        if (schema["default"] is JsonNode defaultValue)
            item["default"] = defaultValue.DeepClone();
        _fieldLegend.Add(item);
        return this;
    }
}

internal static class LlmOutputNormalizer
{
    private static readonly string[] SelectorKeys = ["type", "action", "tool", "branch", "output_type"];

    public static JsonNode? Normalize(JsonNode? output, JsonObject flat)
    {
        if (output == null)
            return null;

        string mode = flat["mode"]?.GetValue<string>() ?? "object";
        JsonArray choices = flat["choices"] as JsonArray ?? new JsonArray();
        return mode switch
        {
            "any" => NormalizeAny(output, choices),
            "all" => NormalizeAll(output, choices),
            _ => NormalizeSingle(output, choices) ?? output,
        };
    }

    private static JsonNode NormalizeAny(JsonNode output, JsonArray choices)
    {
        if (output is JsonArray array)
            return NormalizeArray(array, choices);

        if (output is JsonObject obj && obj["outputs"] is JsonArray outputs)
            return WithNormalizedOutputs(obj, outputs, choices);

        return NormalizeSingle(output, choices) ?? output;
    }

    private static JsonNode NormalizeAll(JsonNode output, JsonArray choices)
    {
        if (output is JsonArray array)
            return NormalizeArray(array, choices);

        if (output is JsonObject obj && obj["outputs"] is JsonArray outputs)
            return WithNormalizedOutputs(obj, outputs, choices);

        return output;
    }

    private static JsonArray NormalizeArray(JsonArray array, JsonArray choices)
    {
        var result = new JsonArray();
        foreach (JsonNode? item in array)
        {
            JsonNode? normalized = item == null ? null : NormalizeSingle(item, choices) ?? item;
            result.Add(normalized?.DeepClone());
        }
        return result;
    }

    private static JsonObject WithNormalizedOutputs(JsonObject obj, JsonArray outputs, JsonArray choices)
    {
        var result = new JsonObject();
        foreach (var property in obj)
            result[property.Key] = property.Key == "outputs" ? NormalizeArray(outputs, choices) : property.Value?.DeepClone();
        return result;
    }

    private static JsonNode? NormalizeSingle(JsonNode output, JsonArray choices)
    {
        if (output is not JsonObject obj)
            return null;

        if (TryGetSelector(obj, out string? selector))
        {
            JsonObject? selectedChoice = FindChoice(choices, selector);
            if (selectedChoice != null)
                return BuildTypedObject(selectedChoice, branchObject: obj, branchValue: null, topLevel: null);
            return null;
        }

        if (TryFindSingleKeyedChoice(obj, choices, out JsonObject keyedChoice, out JsonNode? branchNode))
        {
            JsonObject? branchObject = ToBranchObject(branchNode);
            JsonNode? branchValue = branchObject == null ? ToScalarBranchValue(branchNode, keyedChoice) : null;
            return BuildTypedObject(keyedChoice, branchObject, branchValue, obj);
        }

        JsonObject? onlyChoice = SingleChoice(choices);
        if (onlyChoice != null)
            return BuildTypedObject(onlyChoice, branchObject: obj, branchValue: null, topLevel: null);

        return null;
    }

    private static JsonObject BuildTypedObject(
        JsonObject choice,
        JsonObject? branchObject,
        JsonNode? branchValue,
        JsonObject? topLevel)
    {
        string type = ChoiceType(choice);
        var result = new JsonObject { ["type"] = type };
        var consumedBranchKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonObject field in Fields(choice))
        {
            string name = FieldName(field);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            JsonNode? value = FindFieldValue(branchObject, name, consumedBranchKeys)
                ?? FindAliasValue(branchObject, field, consumedBranchKeys)
                ?? FindFieldValue(topLevel, name, consumedKeys: null)
                ?? FindAliasValue(topLevel, field, consumedKeys: null);

            if (value == null && branchValue != null && IsFirstRequiredField(choice, field))
                value = branchValue;

            if (value != null)
                result[name] = CoerceFieldValue(value, field).DeepClone();
        }

        FillSingleUnknownStringField(result, branchObject, consumedBranchKeys, choice);
        return result;
    }

    private static bool TryGetSelector(JsonObject obj, out string? selector)
    {
        foreach (string key in SelectorKeys)
        {
            if (TryGetProperty(obj, key, out JsonNode? value)
                && value is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out string? text)
                && !string.IsNullOrWhiteSpace(text))
            {
                selector = text;
                return true;
            }
        }

        selector = null;
        return false;
    }

    private static bool TryFindSingleKeyedChoice(
        JsonObject obj,
        JsonArray choices,
        out JsonObject choice,
        out JsonNode? branchNode)
    {
        choice = null!;
        branchNode = null;
        int matches = 0;

        foreach (JsonObject candidate in Choices(choices))
        {
            string type = ChoiceType(candidate);
            if (string.IsNullOrWhiteSpace(type) || !TryGetProperty(obj, type, out JsonNode? value))
                continue;

            choice = candidate;
            branchNode = value;
            matches++;
        }

        return matches == 1;
    }

    private static JsonObject? ToBranchObject(JsonNode? branchNode)
    {
        if (branchNode is JsonObject branchObject)
            return branchObject;

        if (branchNode is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject itemObject)
                    return itemObject;
            }
        }

        return null;
    }

    private static JsonNode? ToScalarBranchValue(JsonNode? branchNode, JsonObject choice)
    {
        if (branchNode == null || branchNode is JsonObject)
            return null;

        if (branchNode is JsonArray array)
        {
            JsonObject? firstRequired = FirstRequiredField(choice);
            if (firstRequired == null || FieldType(firstRequired) != "string")
                return null;

            var parts = new List<string>();
            foreach (JsonNode? item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out string? text))
                    return null;
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }

            return parts.Count == 0 ? null : JsonValue.Create(string.Join("\n", parts));
        }

        return branchNode;
    }

    private static JsonNode? FindFieldValue(JsonObject? source, string name, HashSet<string>? consumedKeys)
    {
        if (source == null)
            return null;

        if (!TryGetProperty(source, name, out JsonNode? value, out string? actualKey))
            return null;

        consumedKeys?.Add(actualKey);
        return value;
    }

    private static JsonNode? FindAliasValue(JsonObject? source, JsonObject field, HashSet<string>? consumedKeys)
    {
        if (source == null)
            return null;

        foreach (string alias in FieldAliases(FieldName(field)))
        {
            if (TryGetProperty(source, alias, out JsonNode? value, out string? actualKey))
            {
                consumedKeys?.Add(actualKey);
                return value;
            }
        }

        return null;
    }

    private static void FillSingleUnknownStringField(
        JsonObject result,
        JsonObject? branchObject,
        HashSet<string> consumedBranchKeys,
        JsonObject choice)
    {
        if (branchObject == null)
            return;

        JsonObject? missingRequiredString = Fields(choice)
            .FirstOrDefault(field =>
                field["required"]?.GetValue<bool>() == true
                && FieldType(field) == "string"
                && !result.ContainsKey(FieldName(field)));
        if (missingRequiredString == null)
            return;

        var candidates = new List<JsonNode>();
        foreach (var property in branchObject)
        {
            if (consumedBranchKeys.Contains(property.Key) || IsKnownFieldOrSelector(property.Key, choice))
                continue;
            if (property.Value is JsonValue value && value.TryGetValue<string>(out _))
                candidates.Add(value);
        }

        if (candidates.Count == 1)
            result[FieldName(missingRequiredString)] = CoerceFieldValue(candidates[0], missingRequiredString).DeepClone();
    }

    private static JsonNode CoerceFieldValue(JsonNode value, JsonObject field)
    {
        if (value is not JsonValue jsonValue)
            return value;

        return FieldType(field) switch
        {
            "string" => CoerceString(jsonValue, field) ?? value,
            "boolean" => CoerceBoolean(jsonValue) ?? value,
            "number" => CoerceNumber(jsonValue) ?? value,
            _ => value,
        };
    }

    private static JsonNode? CoerceString(JsonValue value, JsonObject field)
    {
        if (field["max"] is not JsonNode maxNode || !value.TryGetValue<string>(out string? text))
            return null;

        int max = maxNode.GetValue<int>();
        return text.Length > max ? JsonValue.Create(text[..max]) : null;
    }

    private static JsonNode? CoerceBoolean(JsonValue value)
    {
        if (value.TryGetValue<bool>(out _))
            return null;
        if (value.TryGetValue<int>(out int intValue))
            return JsonValue.Create(intValue != 0);
        if (value.TryGetValue<string>(out string? text))
        {
            text = text.Trim();
            if (bool.TryParse(text, out bool boolValue))
                return JsonValue.Create(boolValue);
            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
                return JsonValue.Create(true);
            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase))
                return JsonValue.Create(false);
        }

        return null;
    }

    private static JsonNode? CoerceNumber(JsonValue value)
    {
        if (value.TryGetValue<double>(out _) || value.TryGetValue<int>(out _))
            return null;
        if (value.TryGetValue<string>(out string? text)
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return JsonValue.Create(number);

        return null;
    }

    private static bool IsFirstRequiredField(JsonObject choice, JsonObject field)
    {
        JsonObject? firstRequired = FirstRequiredField(choice);
        return firstRequired != null
            && string.Equals(FieldName(firstRequired), FieldName(field), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject? FirstRequiredField(JsonObject choice)
    {
        return Fields(choice).FirstOrDefault(field => field["required"]?.GetValue<bool>() == true);
    }

    private static bool IsKnownFieldOrSelector(string key, JsonObject choice)
    {
        if (SelectorKeys.Any(selector => string.Equals(selector, key, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (string.Equals(ChoiceType(choice), key, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (JsonObject field in Fields(choice))
        {
            if (string.Equals(FieldName(field), key, StringComparison.OrdinalIgnoreCase))
                return true;
            if (FieldAliases(FieldName(field)).Any(alias => string.Equals(alias, key, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string[] FieldAliases(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "text" => ["message", "content", "line", "txt", "lext"],
            "tile_x" => ["x"],
            "tile_y" => ["y"],
            "callback" => ["continue", "should_continue"],
            "summary" => ["reason", "note", "notes"],
            _ => [],
        };
    }

    private static bool TryGetProperty(JsonObject source, string name, out JsonNode? value)
    {
        return TryGetProperty(source, name, out value, out _);
    }

    private static bool TryGetProperty(JsonObject source, string name, out JsonNode? value, out string actualKey)
    {
        foreach (var property in source)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                actualKey = property.Key;
                return true;
            }
        }

        value = null;
        actualKey = "";
        return false;
    }

    private static JsonObject? SingleChoice(JsonArray choices)
    {
        JsonObject? result = null;
        foreach (JsonObject choice in Choices(choices))
        {
            if (result != null)
                return null;
            result = choice;
        }
        return result;
    }

    private static JsonObject? FindChoice(JsonArray choices, string type)
    {
        return Choices(choices).FirstOrDefault(choice => string.Equals(ChoiceType(choice), type, StringComparison.OrdinalIgnoreCase));
    }

    private static string ChoiceType(JsonObject choice) => choice["type"]?.GetValue<string>() ?? "";

    private static string FieldName(JsonObject field) => field["name"]?.GetValue<string>() ?? "";

    private static string FieldType(JsonObject field) => field["type"]?.GetValue<string>() ?? "any";

    private static IEnumerable<JsonObject> Choices(JsonArray choices)
    {
        foreach (JsonNode? choice in choices)
        {
            if (choice is JsonObject obj)
                yield return obj;
        }
    }

    private static IEnumerable<JsonObject> Fields(JsonObject choice)
    {
        if (choice["fields"] is not JsonArray fields)
            yield break;

        foreach (JsonNode? field in fields)
        {
            if (field is JsonObject obj)
                yield return obj;
        }
    }
}

internal static class LlmOutputValidator
{
    public static LlmOutputValidationResult Validate(JsonNode? output, JsonObject flat)
    {
        if (output == null)
            return LlmOutputValidationResult.Invalid("Output is null.");

        string mode = flat["mode"]?.GetValue<string>() ?? "object";
        JsonArray choices = flat["choices"] as JsonArray ?? new JsonArray();
        return mode switch
        {
            "one" => ValidateOne(output, choices),
            "any" => ValidateAny(output, choices),
            "all" => ValidateAll(output, choices),
            _ => ValidateSingleObject(output, choices),
        };
    }

    private static LlmOutputValidationResult ValidateSingleObject(JsonNode output, JsonArray choices)
    {
        if (output is not JsonObject obj)
            return LlmOutputValidationResult.Invalid("Output must be a JSON object.");

        var errors = new List<string>();
        if (TryValidateTypedBranch(obj, choices, out _, errors))
            return LlmOutputValidationResult.Valid;
        return new LlmOutputValidationResult(false, errors.Count > 0 ? errors : new List<string> { "Output did not match any branch." });
    }

    private static LlmOutputValidationResult ValidateOne(JsonNode output, JsonArray choices)
    {
        if (output is not JsonObject obj)
            return LlmOutputValidationResult.Invalid("Output must be one JSON object.");

        int matches = CountTypedMatches(obj, choices, out var errors);
        if (matches == 1)
            return LlmOutputValidationResult.Valid;
        if (matches == 0)
            return new LlmOutputValidationResult(false, errors.Count > 0 ? errors : new List<string> { "Output did not match any branch." });
        return LlmOutputValidationResult.Invalid("Output matched more than one branch.");
    }

    private static LlmOutputValidationResult ValidateAny(JsonNode output, JsonArray choices)
    {
        if (output is JsonArray array)
            return ValidateAnyArray(array, choices);

        if (output is JsonObject obj && obj["outputs"] is JsonArray outputs)
            return ValidateAnyArray(outputs, choices);

        if (output is JsonObject keyed && keyed["type"] == null)
            return ValidateAnyKeyedObject(keyed, choices);

        return ValidateOne(output, choices);
    }

    private static LlmOutputValidationResult ValidateAnyArray(JsonArray array, JsonArray choices)
    {
        var errors = new List<string>();
        if (array.Count == 0)
            errors.Add("Output array must contain at least one item.");

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
            {
                errors.Add($"Output item {i} must be an object.");
                continue;
            }

            if (CountTypedMatches(item, choices, out var itemErrors) != 1)
                errors.AddRange(itemErrors.Select(error => $"Item {i}: {error}"));
        }

        return errors.Count == 0 ? LlmOutputValidationResult.Valid : new LlmOutputValidationResult(false, errors);
    }

    private static LlmOutputValidationResult ValidateAnyKeyedObject(JsonObject keyed, JsonArray choices)
    {
        var errors = new List<string>();
        int matched = 0;
        foreach (var choice in Choices(choices))
        {
            string type = ChoiceType(choice);
            if (string.IsNullOrWhiteSpace(type) || keyed[type] == null)
                continue;

            if (keyed[type] is JsonObject branchObject)
            {
                ValidateBranchFields(branchObject, choice, requireType: false, errors, type);
                matched++;
            }
            else if (keyed[type] is JsonValue value)
            {
                ValidateScalarKeyedBranch(value, choice, errors, type);
                matched++;
            }
            else
            {
                errors.Add($"Branch '{type}' must be an object or scalar value.");
            }
        }

        if (matched == 0)
            errors.Add("Output did not include any known branch.");
        return errors.Count == 0 ? LlmOutputValidationResult.Valid : new LlmOutputValidationResult(false, errors);
    }

    private static LlmOutputValidationResult ValidateAll(JsonNode output, JsonArray choices)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (output is JsonArray array)
        {
            ValidateBranchArray(array, choices, seen, errors);
        }
        else if (output is JsonObject obj && obj["outputs"] is JsonArray outputs)
        {
            ValidateBranchArray(outputs, choices, seen, errors);
        }
        else if (output is JsonObject keyed)
        {
            foreach (var choice in Choices(choices))
            {
                string type = ChoiceType(choice);
                if (string.IsNullOrWhiteSpace(type) || keyed[type] == null)
                    continue;

                if (keyed[type] is JsonObject branchObject)
                {
                    ValidateBranchFields(branchObject, choice, requireType: false, errors, type);
                    seen.Add(type);
                }
                else if (keyed[type] is JsonValue value)
                {
                    ValidateScalarKeyedBranch(value, choice, errors, type);
                    seen.Add(type);
                }
                else
                {
                    errors.Add($"Branch '{type}' must be an object or scalar value.");
                }
            }

            if (keyed["type"] is JsonValue)
            {
                if (TryValidateTypedBranch(keyed, choices, out string? matchedType, errors) && matchedType != null)
                    seen.Add(matchedType);
            }
        }
        else
        {
            errors.Add("Output must be an object, an array, or an object with an outputs array.");
        }

        foreach (var choice in Choices(choices))
        {
            string type = ChoiceType(choice);
            if (!string.IsNullOrWhiteSpace(type) && !seen.Contains(type))
                errors.Add($"Missing branch '{type}'.");
        }

        return errors.Count == 0 ? LlmOutputValidationResult.Valid : new LlmOutputValidationResult(false, errors);
    }

    private static void ValidateBranchArray(JsonArray array, JsonArray choices, HashSet<string> seen, List<string> errors)
    {
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
            {
                errors.Add($"Output item {i} must be an object.");
                continue;
            }

            var itemErrors = new List<string>();
            if (TryValidateTypedBranch(item, choices, out string? matchedType, itemErrors) && matchedType != null)
                seen.Add(matchedType);
            else
                errors.AddRange(itemErrors.Select(error => $"Item {i}: {error}"));
        }
    }

    private static bool TryValidateTypedBranch(JsonObject obj, JsonArray choices, out string? matchedType, List<string> errors)
    {
        matchedType = null;
        int startErrorCount = errors.Count;
        string actualType = obj["type"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(actualType))
        {
            errors.Add("Output object is missing required 'type'.");
            return false;
        }

        JsonObject? choice = FindChoice(choices, actualType);
        if (choice == null)
        {
            errors.Add($"Unknown output type '{actualType}'.");
            return false;
        }

        ValidateBranchFields(obj, choice, requireType: true, errors, actualType);
        if (errors.Count == startErrorCount)
        {
            matchedType = actualType;
            return true;
        }

        return false;
    }

    private static int CountTypedMatches(JsonObject obj, JsonArray choices, out List<string> errors)
    {
        errors = new List<string>();
        var localErrors = new List<string>();
        bool matched = TryValidateTypedBranch(obj, choices, out _, localErrors);
        if (matched)
            return 1;
        errors.AddRange(localErrors);
        return 0;
    }

    private static void ValidateBranchFields(JsonObject obj, JsonObject choice, bool requireType, List<string> errors, string branchType)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requireType)
            allowed.Add("type");

        foreach (var field in Fields(choice))
        {
            string name = field["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;

            allowed.Add(name);
            bool required = field["required"]?.GetValue<bool>() == true;
            JsonNode? value = obj[name];
            if (value == null)
            {
                if (required)
                    errors.Add($"Branch '{branchType}' is missing required field '{name}'.");
                continue;
            }

            ValidateFieldValue(value, field, errors, branchType, name);
        }

        foreach (var property in obj)
        {
            if (!allowed.Contains(property.Key))
                errors.Add($"Branch '{branchType}' has unknown field '{property.Key}'.");
        }
    }

    private static void ValidateScalarKeyedBranch(JsonValue value, JsonObject choice, List<string> errors, string branchType)
    {
        JsonObject? firstRequired = Fields(choice).FirstOrDefault(field => field["required"]?.GetValue<bool>() == true);
        if (firstRequired == null)
        {
            errors.Add($"Branch '{branchType}' does not accept a scalar value.");
            return;
        }

        ValidateFieldValue(value, firstRequired, errors, branchType, firstRequired["name"]?.GetValue<string>() ?? "value");
    }

    private static void ValidateFieldValue(JsonNode value, JsonObject field, List<string> errors, string branchType, string fieldName)
    {
        string expectedType = field["type"]?.GetValue<string>() ?? "any";
        bool typeOk = expectedType switch
        {
            "string" => value is JsonValue stringValue && stringValue.TryGetValue<string>(out _),
            "number" => value is JsonValue numberValue && (numberValue.TryGetValue<double>(out _) || numberValue.TryGetValue<int>(out _)),
            "boolean" => value is JsonValue boolValue && boolValue.TryGetValue<bool>(out _),
            _ => true,
        };

        if (!typeOk)
        {
            errors.Add($"Branch '{branchType}' field '{fieldName}' must be {expectedType}.");
            return;
        }

        if (expectedType == "string" && field["max"] is JsonNode maxNode)
        {
            int max = maxNode.GetValue<int>();
            string text = value.GetValue<string>();
            if (text.Length > max)
                errors.Add($"Branch '{branchType}' field '{fieldName}' exceeds max length {max}.");
        }
    }

    private static JsonObject? FindChoice(JsonArray choices, string type)
    {
        return Choices(choices).FirstOrDefault(choice => string.Equals(ChoiceType(choice), type, StringComparison.OrdinalIgnoreCase));
    }

    private static string ChoiceType(JsonObject choice) => choice["type"]?.GetValue<string>() ?? "";

    private static IEnumerable<JsonObject> Choices(JsonArray choices)
    {
        foreach (var choice in choices)
        {
            if (choice is JsonObject obj)
                yield return obj;
        }
    }

    private static IEnumerable<JsonObject> Fields(JsonObject choice)
    {
        if (choice["fields"] is not JsonArray fields)
            yield break;

        foreach (var field in fields)
        {
            if (field is JsonObject obj)
                yield return obj;
        }
    }
}






