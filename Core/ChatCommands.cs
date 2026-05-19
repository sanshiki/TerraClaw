using System;
using Terraria;
using Terraria.ModLoader;
using TerraClaw.Network;

namespace TerraClaw.Core;

/// <summary>
/// Chat commands for controlling the TerraClaw NPC agent.
/// Usage: /agent <instruction>
/// </summary>
public class AgentChatCommand : ModCommand
{
    public override string Command => "agent";

    public override CommandType Type => CommandType.Chat;

    public override string Usage => "/agent <instruction>";

    public override string Description => "Send an instruction to the TerraClaw NPC agent";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (args.Length == 0)
        {
            Main.NewText("Usage: /agent <instruction>", 150, 200, 255);
            return;
        }

        string instruction = string.Join(" ", args);

        var json = MessageSerializer.BuildMessage("player.chat", new
        {
            player = caller.Player?.name ?? "Unknown",
            text = instruction,
            timestamp_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        BridgeModSystem.Instance.WebSocketServer.Broadcast(json);
        Main.NewText($"[TerraClaw] Instruction sent to agent: {instruction}", 150, 200, 255);
    }
}
