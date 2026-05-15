using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TerraClaw.Network;

public static class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>
    /// Build a standard message envelope as a JSON string.
    /// </summary>
    public static string BuildMessage(string type, object payload, string? sessionId = null, string? inReplyTo = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = type,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = payload,
        };

        if (sessionId != null)
            envelope["session_id"] = sessionId;
        if (inReplyTo != null)
            envelope["in_reply_to"] = inReplyTo;

        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <summary>
    /// Parse a message envelope from JSON.
    /// </summary>
    public static MessageEnvelope? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MessageEnvelope>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract just the payload as a JsonElement for further processing.
    /// </summary>
    public static JsonElement? ExtractPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("payload", out var payload))
            return payload;
        return null;
    }

    /// <summary>
    /// Extract the message type from a JSON envelope.
    /// </summary>
    public static string? ExtractType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("type", out var typeProp))
            return typeProp.GetString();
        return null;
    }
}

public class MessageEnvelope
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public long Timestamp { get; set; }
    public string? SessionId { get; set; }
    public string? InReplyTo { get; set; }
    public JsonElement Payload { get; set; }
}
