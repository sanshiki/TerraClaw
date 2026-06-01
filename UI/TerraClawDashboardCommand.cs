#nullable enable

using Terraria.ModLoader;

namespace TerraClaw.UI;

internal sealed class TerraClawDashboardCommand : ModCommand
{
    public override string Command => "terraclawdash";
    public override CommandType Type => CommandType.Chat;
    public override string Usage => "/terraclawdash";
    public override string Description => "Toggle the TerraClaw in-game LLM dashboard";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        TerraClawDashboardSystem.Instance?.Toggle();
    }
}
