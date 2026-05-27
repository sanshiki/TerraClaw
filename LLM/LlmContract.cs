using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

public interface IContextProvider
{
    void AddContext(JsonObject target);
}

public sealed record SymbolicField(string Name, string Description = "");

public interface ISymbolicContextProvider : IContextProvider
{
    string Symbol { get; }
    string Description { get; }
    IReadOnlyList<SymbolicField> Fields { get; }
    JsonArray ToSymbolicValues();
}

public sealed class LlmObservation
{
    private readonly JsonObject _data = new();
    private readonly JsonObject _symbolicData = new();
    private readonly JsonObject _legend = new();
    private readonly JsonObject _docs = new();
    private readonly JsonObject _extensions = new();

    public static LlmObservation Create() => new();

    public LlmObservation With(string key, object? value)
    {
        var node = JsonSerializer.SerializeToNode(value);
        _data[key] = node?.DeepClone();
        _extensions[key] = node;
        return this;
    }

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

    public JsonObject ToJson() => _data.DeepClone().AsObject();

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
        if (_extensions.Count > 0)
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

    public static LlmObjectBuilder Object(string name, string description = "")
        => new(name, description);

    public static LlmOutput OneOf(params LlmOutput[] outputs)
        => Composite("oneOf", "Return exactly one of the listed output shapes.", outputs);

    public static LlmOutput AnyOf(params LlmOutput[] outputs)
        => Composite("anyOf", "Return one or more of the listed output shapes.", outputs);

    public static LlmOutput AllOf(params LlmOutput[] outputs)
        => Composite("allOf", "Return an output satisfying all listed shapes.", outputs);

    public LlmOutput And(LlmOutput other) => AllOf(this, other);

    public JsonObject ToJsonSchema() => CloneObject(_schema);

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

    public LlmObjectBuilder String(string name, bool required = false, int? maxLength = null, string description = "")
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (maxLength.HasValue)
            schema["maxLength"] = maxLength.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

    public LlmObjectBuilder Number(string name, bool required = false, double? defaultValue = null, string description = "")
    {
        var schema = new JsonObject { ["type"] = "number" };
        if (defaultValue.HasValue)
            schema["default"] = defaultValue.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

    public LlmObjectBuilder Boolean(string name, bool required = false, string description = "")
    {
        var schema = new JsonObject { ["type"] = "boolean" };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required, description);
    }

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
