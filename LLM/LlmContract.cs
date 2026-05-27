using System;
using System.Collections.Generic;
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
    public LlmObjectBuilder String(string name, bool required = false, int? maxLength = null, string description = "")
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (maxLength.HasValue)
            schema["maxLength"] = maxLength.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

    /// <summary>Adds a numeric field to the output object.</summary>
    public LlmObjectBuilder Number(string name, bool required = false, double? defaultValue = null, string description = "")
    {
        var schema = new JsonObject { ["type"] = "number" };
        if (defaultValue.HasValue)
            schema["default"] = defaultValue.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

    /// <summary>Adds a boolean field to the output object.</summary>
    public LlmObjectBuilder Boolean(string name, bool required = false, string description = "")
    {
        var schema = new JsonObject { ["type"] = "boolean" };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

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
            ["type"] = schema["type"]?.GetValue<string>() ?? "any",
            ["required"] = required,
        };
        if (schema["maxLength"] is JsonNode maxLength)
            item["max"] = maxLength.DeepClone();
        if (schema["default"] is JsonNode defaultValue)
            item["default"] = defaultValue.DeepClone();
        if (!string.IsNullOrWhiteSpace(description))
            item["description"] = description;
        _fieldLegend.Add(item);
        return this;
    }
}
