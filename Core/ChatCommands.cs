using Terraria;
using Terraria.ModLoader;
using TerraClaw.Agents.TerraClaw;

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

    /// <summary>Directly delivers player text to in-game instruction receivers.</summary>
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (args.Length == 0)
        {
            Main.NewText("Usage: /agent <instruction>", 150, 200, 255);
            return;
        }

        string instruction = string.Join(" ", args);
        int delivered = ExampleTerraClawAgent.DeliverPlayerInstruction(
            caller.Player?.name ?? "Unknown",
            instruction);
        Main.NewText($"[TerraClaw] Instruction sent to {delivered} agent(s): {instruction}", 150, 200, 255);
    }
}
