using System;
using System.Collections.Generic;
using TerraClaw.Interfaces;
using Terraria.ModLoader;

namespace TerraClaw;

public class TerraClaw : Mod
{
    public static TerraClaw Instance { get; private set; } = null!;
    public static API.TerraClawApi? Api { get; private set; }

    public override void Load()
    {
        Instance = this;
        Api = new API.TerraClawApi();
    }

    public override void Unload()
    {
        Api?.Clear();
        Api = null;
        Instance = null!;
    }

    public override object? Call(params object[] args)
    {
        if (args.Length == 0 || args[0] is not string command || string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("The first Mod.Call argument must be a command name.", nameof(args));

        var api = Api ?? throw new InvalidOperationException("TerraClaw API is not initialized.");

        return command switch
        {
            "GetAPI" => api,
            "GetVersion" => API.TerraClawApi.ApiVersion,
            "HasFeature" when args.Length == 2 && args[1] is string feature => api.HasFeature(feature),
            "GetFeatures" => api.Features,
            "RegisterAgent" when args.Length == 2 && args[1] is ILlmAgentDefinition agent => RegisterAgent(api, agent),
            "RegisterAgent" when IsWeakAgentRegistration(args) => RegisterAgent(api, args),
            "HasAgent" when args.Length == 2 && args[1] is string agentId => api.HasAgent(agentId),
            "GetAgent" when args.Length == 2 && args[1] is string agentId => api.GetAgent(agentId),
            "GetAgents" => api.GetAgents(),
            _ => throw new ArgumentException($"Unknown or invalid TerraClaw Mod.Call command: {command}", nameof(args)),
        };
    }

    private static bool RegisterAgent(API.TerraClawApi api, ILlmAgentDefinition agent)
    {
        api.RegisterAgent(agent);
        return true;
    }

    private static bool RegisterAgent(API.TerraClawApi api, object[] args)
    {
        var tags = args.Length >= 7 && args[6] is IEnumerable<string> tagValues
            ? tagValues
            : Array.Empty<string>();

        api.RegisterAgent(
            (string)args[1],
            (string)args[2],
            (string)args[3],
            (string)args[4],
            (string)args[5],
            tags);

        return true;
    }

    private static bool IsWeakAgentRegistration(object[] args)
    {
        return args.Length is 6 or 7
            && args[1] is string
            && args[2] is string
            && args[3] is string
            && args[4] is string
            && args[5] is string
            && (args.Length == 6 || args[6] is IEnumerable<string>);
    }
}
