#nullable enable

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerraClaw.UI;

internal sealed class TerraClawDashboardSystem : ModSystem
{
    internal static TerraClawDashboardSystem? Instance { get; private set; }

    private UserInterface? _interface;
    private TerraClawDashboardState? _state;

    public bool IsOpen { get; private set; }

    public override void Load()
    {
        if (Main.dedServ)
            return;

        Instance = this;
        _state = new TerraClawDashboardState();
        _interface = new UserInterface();
    }

    public override void Unload()
    {
        _interface = null;
        _state = null;
        Instance = null;
    }

    public override void UpdateUI(GameTime gameTime)
    {
        if (IsOpen)
            _interface?.Update(gameTime);
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
        if (mouseTextIndex < 0)
            return;

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "TerraClaw: LLM Dashboard",
            delegate
            {
                if (IsOpen)
                    _interface?.Draw(Main.spriteBatch, new GameTime());
                return true;
            },
            InterfaceScaleType.UI));
    }

    public void Toggle()
    {
        SetOpen(!IsOpen);
    }

    public void SetOpen(bool open)
    {
        if (_interface == null || _state == null)
            return;

        IsOpen = open;
        _interface.SetState(open ? _state : null);
        if (open)
            _state.Refresh();
    }
}
