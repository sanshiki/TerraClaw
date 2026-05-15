using System;
using Terraria.ModLoader;

namespace TerraClaw;

public class TerraClaw : Mod
{
    public static TerraClaw Instance { get; private set; } = null!;

    public override void Load()
    {
        Instance = this;
    }

    public override void Unload()
    {
        Instance = null!;
    }
}
