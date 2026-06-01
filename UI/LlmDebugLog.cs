#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Terraria;

namespace TerraClaw.UI;

internal static class LlmDebugLog
{
    private const int MaxEntries = 50;
    private static readonly object Sync = new();
    private static readonly List<LlmDebugEntry> Entries = new();

    public static event Action? Changed;

    public static void AddRequest(
        string requestId,
        string agentId,
        string instruction,
        int timeoutMs,
        JsonObject observation,
        JsonObject symbolicObservation,
        JsonObject outputContract,
        IReadOnlyList<string> observationKeys)
    {
        lock (Sync)
        {
            var existing = Entries.FirstOrDefault(entry => entry.RequestId == requestId);
            if (existing == null)
            {
                existing = new LlmDebugEntry
                {
                    RequestId = requestId,
                    AgentId = agentId,
                    CreatedTick = (int)Main.GameUpdateCount,
                };
                Entries.Insert(0, existing);
            }

            existing.Instruction = instruction;
            existing.TimeoutMs = timeoutMs;
            existing.Status = "pending";
            existing.ObservationKeys = observationKeys.ToArray();
            existing.SymbolicObservationJson = ToPrettyJson(symbolicObservation);
            existing.ObservationJson = ToPrettyJson(observation);
            existing.OutputContractJson = ToPrettyJson(outputContract);
            existing.UpdatedTick = (int)Main.GameUpdateCount;
            Trim();
        }

        Changed?.Invoke();
    }

    public static void AddResult(
        string requestId,
        string agentId,
        string status,
        double durationSeconds,
        JsonNode? output,
        string? error)
    {
        lock (Sync)
        {
            var existing = Entries.FirstOrDefault(entry => entry.RequestId == requestId);
            if (existing == null)
            {
                existing = new LlmDebugEntry
                {
                    RequestId = requestId,
                    AgentId = agentId,
                    CreatedTick = (int)Main.GameUpdateCount,
                };
                Entries.Insert(0, existing);
            }

            existing.Status = status;
            existing.DurationSeconds = durationSeconds;
            existing.OutputJson = output == null ? "" : ToPrettyJson(output);
            existing.Error = error ?? "";
            existing.UpdatedTick = (int)Main.GameUpdateCount;
            Trim();
        }

        Changed?.Invoke();
    }

    public static IReadOnlyList<LlmDebugEntry> GetSnapshot()
    {
        lock (Sync)
        {
            return Entries.Select(Clone).ToList();
        }
    }

    public static void Clear()
    {
        lock (Sync)
            Entries.Clear();

        Changed?.Invoke();
    }

    private static void Trim()
    {
        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);
    }

    private static LlmDebugEntry Clone(LlmDebugEntry entry)
    {
        return new LlmDebugEntry
        {
            RequestId = entry.RequestId,
            AgentId = entry.AgentId,
            Instruction = entry.Instruction,
            TimeoutMs = entry.TimeoutMs,
            Status = entry.Status,
            DurationSeconds = entry.DurationSeconds,
            Error = entry.Error,
            ObservationKeys = entry.ObservationKeys.ToArray(),
            SymbolicObservationJson = entry.SymbolicObservationJson,
            ObservationJson = entry.ObservationJson,
            OutputContractJson = entry.OutputContractJson,
            OutputJson = entry.OutputJson,
            CreatedTick = entry.CreatedTick,
            UpdatedTick = entry.UpdatedTick,
        };
    }

    private static string ToPrettyJson(JsonNode node)
    {
        try
        {
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = true,
            }))
            {
                node.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return node.ToJsonString();
        }
    }
}
