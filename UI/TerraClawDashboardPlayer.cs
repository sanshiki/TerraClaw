#nullable enable

using Terraria.GameInput;
using Terraria.ModLoader;

namespace TerraClaw.UI;

internal sealed class TerraClawDashboardPlayer : ModPlayer
{
    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (TerraClawDashboardKeybinds.ToggleDashboard?.JustPressed == true)
            TerraClawDashboardSystem.Instance?.Toggle();
    }
}
