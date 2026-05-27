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
    /// <summary>Chat command name without the leading slash.</summary>
    public override string Command => "agent";

    /// <summary>Registers this as an in-game chat command.</summary>
    public override CommandType Type => CommandType.Chat;

    public override string Usage => "/agent <instruction>";

    public override string Description => "Send an instruction to the TerraClaw NPC agent";

    /// <summary>Broadcasts player text to the runtime and directly delivers it to in-game instruction receivers.</summary>
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
        int delivered = BridgeModSystem.Instance.DeliverPlayerInstruction(
            caller.Player?.name ?? "Unknown",
            instruction);
        Main.NewText($"[TerraClaw] Instruction sent to {delivered} agent(s): {instruction}", 150, 200, 255);
    }
}
