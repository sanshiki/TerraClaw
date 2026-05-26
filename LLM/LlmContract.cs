using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraClaw.LLM;

public interface IContextProvider
{
    void AddContext(JsonObject target);
}

public sealed class LlmObservation
{
    private readonly JsonObject _data = new();

    public static LlmObservation Create() => new();

    public LlmObservation With(string key, object? value)
    {
        _data[key] = JsonSerializer.SerializeToNode(value);
        return this;
    }

    public LlmObservation With(IContextProvider provider)
    {
        provider.AddContext(_data);
        return this;
    }

    public JsonObject ToJson() => _data.DeepClone().AsObject();
}

public sealed class LlmOutput
{
    private readonly JsonObject _schema;
    private readonly string _description;

    private LlmOutput(JsonObject schema, string description = "")
    {
        _schema = schema;
        _description = description;
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
        return new LlmOutput(new JsonObject { [key] = array }, description);
    }

    internal static LlmOutput FromObject(string name, string description, JsonObject properties, JsonArray required)
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
        return new LlmOutput(schema, description);
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return source.DeepClone().AsObject();
    }
}

public sealed class LlmObjectBuilder
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonObject _properties = new();
    private readonly JsonArray _required = new();

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
        return Add(name, schema, required);
    }

    public LlmObjectBuilder Number(string name, bool required = false, double? defaultValue = null, string description = "")
    {
        var schema = new JsonObject { ["type"] = "number" };
        if (defaultValue.HasValue)
            schema["default"] = defaultValue.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required);
    }

    public LlmObjectBuilder Boolean(string name, bool required = false, string description = "")
    {
        var schema = new JsonObject { ["type"] = "boolean" };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return Add(name, schema, required);
    }

    public LlmOutput Build() => LlmOutput.FromObject(_name, _description, _properties, _required);

    public static implicit operator LlmOutput(LlmObjectBuilder builder) => builder.Build();

    private LlmObjectBuilder Add(string name, JsonObject schema, bool required)
    {
        _properties[name] = schema;
        if (required)
            _required.Add(name);
        return this;
    }
}
