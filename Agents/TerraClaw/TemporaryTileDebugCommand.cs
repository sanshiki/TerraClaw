using System.Collections.Generic;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ModLoader;
using TerraClaw.LLM;

namespace TerraClaw.Agents.TerraClaw;

/// <summary>Temporary local-only command for checking tile flood-fill dust without sending LLM requests.</summary>
public sealed class TemporaryTileDebugCommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "tiledebug";
    public override string Description => "Temporarily debug TerraClaw tile flood fill.";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        var observation = LlmObservation.Create()
            .Use(TerrariaContext.Tiles(caller.Player.Center, radiusTiles: 24).Area(maxSpecials: 12));
        var tileArea = observation.ToJson()["tile_area"] as JsonObject;
        if (tileArea == null)
        {
            Main.NewText("Tile debug failed: tile_area missing.", 255, 100, 100);
            return;
        }

        var tagValues = new List<string>();
        if (tileArea["tags"] is JsonArray tagArray)
        {
            foreach (var tag in tagArray)
                tagValues.Add(tag?.GetValue<string>() ?? "");
        }

        string tags = string.Join(", ", tagValues);
        string floor = tileArea["floor"]?.ToJsonString() ?? "null";
        var ratios = tileArea["ratios"] as JsonObject;
        string solid = ratios?["solid"]?.ToJsonString() ?? "?";
        string wall = ratios?["wall"]?.ToJsonString() ?? "?";
        string open = ratios?["open"]?.ToJsonString() ?? "?";
        string liquid = tileArea["liquid"]?.ToJsonString() ?? "{}";
        string topology = tileArea["topology"]?.ToJsonString() ?? "{}";

        Main.NewText($"Tile topology tags: [{tags}]", 120, 220, 255);
        Main.NewText($"ratios solid={solid} wall={wall} open={open}; floor={floor}; liquid={liquid}", 180, 220, 255);
        Main.NewText($"topology={topology}", 180, 220, 255);
        Main.NewText("Temporary debug command: delete TemporaryTileDebugCommand.cs when done.", 160, 160, 160);
    }
}
