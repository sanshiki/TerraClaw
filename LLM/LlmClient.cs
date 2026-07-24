#nullable enable

using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraClaw.LLM;

internal sealed class LlmClient
{
    private ChatClient? _client;
    private string _clientKey = "";

    public async Task<JsonNode?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        JsonObject outputContract,
        CancellationToken cancellationToken)
    {
        TerraClawLlmConfig config = TerraClawLlmConfig.Load();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException($"OpenAI API key is not set. Set llm.api_key in {TerraClawLlmConfig.ConfigPath} or OPENAI_API_KEY.");

        ChatClient client = GetClient(config.Model, config.ApiKey, config.ApiBase);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt),
        };

        var options = new ChatCompletionOptions();
        if (config.MaxTokens.HasValue)
            options.MaxOutputTokenCount = config.MaxTokens.Value;
        if (config.Temperature.HasValue)
            options.Temperature = config.Temperature.Value;

        ChatCompletion completion = await client.CompleteChatAsync(messages, options, cancellationToken);
        if (completion.Content.Count == 0)
            throw new InvalidOperationException("OpenAI response did not contain text content.");

        string text = StripCodeFence(completion.Content[0].Text.Trim());
        JsonNode? parsed = ParseJsonOutput(text);
        if (parsed is not JsonObject && parsed is not JsonArray)
            throw new InvalidOperationException("LLM output must be a JSON object or array.");
        return parsed;
    }

    private ChatClient GetClient(string model, string apiKey, string apiBase)
    {
        string key = $"{model}\n{apiKey}\n{apiBase}";
        if (_client != null && key == _clientKey)
            return _client;

        _client = string.IsNullOrWhiteSpace(apiBase)
            ? new ChatClient(model: model, apiKey: apiKey)
            : new ChatClient(
                model: model,
                credential: new ApiKeyCredential(apiKey),
                options: new OpenAIClientOptions { Endpoint = new Uri(apiBase) });
        _clientKey = key;
        return _client;
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        string[] lines = text.Split('\n');
        int start = lines.Length > 0 && lines[0].StartsWith("```", StringComparison.Ordinal) ? 1 : 0;
        int end = lines.Length;
        if (end > start && lines[end - 1].StartsWith("```", StringComparison.Ordinal))
            end--;
        return string.Join("\n", lines[start..end]).Trim();
    }

    private static JsonNode? ParseJsonOutput(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            foreach (string extracted in ExtractJsonValues(text))
            {
                try
                {
                    return JsonNode.Parse(extracted);
                }
                catch (JsonException)
                {
                }
            }

            throw new InvalidOperationException("LLM output was not valid JSON.", ex);
        }
    }

    private static IEnumerable<string> ExtractJsonValues(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '{' && text[i] != '[')
                continue;

            string? value = ExtractJsonValueAt(text, i);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static string? ExtractJsonValueAt(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{' || c == '[')
                depth++;
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)].Trim();
            }
        }

        return null;
    }
}
