#nullable enable

using Terraria;
using Terraria.ModLoader;

namespace TerraClaw.UI;

internal sealed class TerraClawDashboardKeybinds : ModSystem
{
    internal static ModKeybind? ToggleDashboard { get; private set; }

    public override void Load()
    {
        if (!Main.dedServ)
            ToggleDashboard = KeybindLoader.RegisterKeybind(Mod, "ToggleDashboard", "OemTilde");
    }

    public override void Unload()
    {
        ToggleDashboard = null;
    }
}
