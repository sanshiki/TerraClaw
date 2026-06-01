#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;

namespace TerraClaw.UI;

internal sealed class TerraClawDashboardState : UIState
{
    private readonly Color _panelColor = new(28, 32, 45, 235);
    private readonly Color _innerColor = new(18, 21, 30, 240);
    private readonly Color _borderColor = new(75, 85, 115);

    private UIPanel _root = null!;
    private UIList _entryList = null!;
    private UIList _detailList = null!;
    private UIText _summary = null!;
    private string _selectedRequestId = "";
    private DetailTab _tab = DetailTab.Symbolic;
    private volatile bool _needsRefresh;

    public override void OnInitialize()
    {
        _root = new UIPanel();
        _root.Width.Set(0f, 0.72f);
        _root.Height.Set(0f, 0.68f);
        _root.HAlign = 0.5f;
        _root.VAlign = 0.5f;
        _root.BackgroundColor = _panelColor;
        _root.BorderColor = _borderColor;
        Append(_root);

        var title = new UIText("TerraClaw LLM Dashboard");
        title.Left.Set(8f, 0f);
        title.Top.Set(8f, 0f);
        title.TextColor = new Color(160, 190, 255);
        _root.Append(title);

        var close = Button("Close", 74f, 30f);
        close.HAlign = 1f;
        close.Top.Set(0f, 0f);
        close.OnLeftClick += (_, _) => TerraClawDashboardSystem.Instance?.SetOpen(false);
        _root.Append(close);

        var clear = Button("Clear", 72f, 30f);
        clear.HAlign = 1f;
        clear.Left.Set(-82f, 0f);
        clear.Top.Set(0f, 0f);
        clear.OnLeftClick += (_, _) => LlmDebugLog.Clear();
        _root.Append(clear);

        var listPanel = Panel(280f, 0f);
        listPanel.Top.Set(44f, 0f);
        listPanel.Height.Set(-54f, 1f);
        _root.Append(listPanel);

        _entryList = new UIList();
        _entryList.ManualSortMethod = _ => { };
        _entryList.Width.Set(-26f, 1f);
        _entryList.Height.Set(0f, 1f);
        _entryList.ListPadding = 6f;
        listPanel.Append(_entryList);

        var listScrollbar = new UIScrollbar();
        listScrollbar.HAlign = 1f;
        listScrollbar.Height.Set(0f, 1f);
        listPanel.Append(listScrollbar);
        _entryList.SetScrollbar(listScrollbar);

        var detailPanel = Panel(0f, 0f);
        detailPanel.Left.Set(292f, 0f);
        detailPanel.Top.Set(44f, 0f);
        detailPanel.Width.Set(-292f, 1f);
        detailPanel.Height.Set(-54f, 1f);
        _root.Append(detailPanel);

        _summary = new UIText("No LLM activity yet.");
        _summary.TextOriginX = 0f;
        _summary.Width.Set(0f, 1f);
        detailPanel.Append(_summary);

        AddTabButtons(detailPanel);

        _detailList = new UIList();
        _detailList.ManualSortMethod = _ => { };
        _detailList.Top.Set(82f, 0f);
        _detailList.Width.Set(-38f, 1f);
        _detailList.Height.Set(-82f, 1f);
        _detailList.ListPadding = 8f;
        detailPanel.Append(_detailList);

        var detailScrollbar = new UIScrollbar();
        detailScrollbar.Top.Set(82f, 0f);
        detailScrollbar.HAlign = 1f;
        detailScrollbar.Height.Set(-82f, 1f);
        detailPanel.Append(detailScrollbar);
        _detailList.SetScrollbar(detailScrollbar);
    }

    public override void OnActivate()
    {
        LlmDebugLog.Changed += MarkDirty;
        Refresh();
    }

    public override void OnDeactivate()
    {
        LlmDebugLog.Changed -= MarkDirty;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (_root.ContainsPoint(Main.MouseScreen))
        {
            Main.LocalPlayer.mouseInterface = true;
            if (_detailList.ContainsPoint(Main.MouseScreen))
                _detailList.ViewPosition -= Terraria.GameInput.PlayerInput.ScrollWheelDeltaForUI;
            else if (_entryList.ContainsPoint(Main.MouseScreen))
                _entryList.ViewPosition -= Terraria.GameInput.PlayerInput.ScrollWheelDeltaForUI;
        }

        if (_needsRefresh)
        {
            _needsRefresh = false;
            Refresh();
        }
    }

    public void Refresh()
    {
        if (_entryList == null || _detailList == null)
            return;

        IReadOnlyList<LlmDebugEntry> entries = LlmDebugLog.GetSnapshot();
        if (entries.Count > 0 && !entries.Any(entry => entry.RequestId == _selectedRequestId))
            _selectedRequestId = entries[0].RequestId;

        RebuildEntries(entries);
        RebuildDetails(entries.FirstOrDefault(entry => entry.RequestId == _selectedRequestId));
        Recalculate();
    }

    private void MarkDirty()
    {
        _needsRefresh = true;
    }

    private void RebuildEntries(IReadOnlyList<LlmDebugEntry> entries)
    {
        _entryList.Clear();

        if (entries.Count == 0)
        {
            _entryList.Add(TextLine("Waiting for LLM activity...", new Color(145, 153, 180)));
            return;
        }

        foreach (LlmDebugEntry entry in entries)
        {
            var row = Button(RowTitle(entry), 0f, 58f);
            row.Width.Set(0f, 1f);
            row.BackgroundColor = EntryColor(entry);
            if (entry.RequestId == _selectedRequestId)
                row.BorderColor = new Color(122, 162, 247);

            string requestId = entry.RequestId;
            row.OnLeftClick += (_, _) =>
            {
                _selectedRequestId = requestId;
                Refresh();
            };
            _entryList.Add(row);
        }
    }

    private void RebuildDetails(LlmDebugEntry? entry)
    {
        _detailList.Clear();

        if (entry == null)
        {
            _summary.SetText("No LLM activity yet.");
            return;
        }

        string duration = entry.DurationSeconds.HasValue ? $"{entry.DurationSeconds:0.##}s" : "pending";
        _summary.SetText($"{entry.Status}  |  agent {ShortId(entry.AgentId)}  |  request {ShortId(entry.RequestId)}  |  {duration}");

        _detailList.Add(TextLine("Instruction", new Color(160, 190, 255)));
        _detailList.Add(TextBlock(string.IsNullOrWhiteSpace(entry.Instruction) ? "(none)" : entry.Instruction));

        if (!string.IsNullOrWhiteSpace(entry.Error))
        {
            _detailList.Add(TextLine("Error", new Color(247, 118, 142)));
            _detailList.Add(TextBlock(entry.Error));
        }

        string body = _tab switch
        {
            DetailTab.Symbolic => entry.SymbolicObservationJson,
            DetailTab.Observation => entry.ObservationJson,
            DetailTab.Contract => entry.OutputContractJson,
            DetailTab.Output => entry.OutputJson,
            _ => "",
        };

        _detailList.Add(TextLine(_tab.ToString(), new Color(160, 190, 255)));
        _detailList.Add(TextBlock(string.IsNullOrWhiteSpace(body) ? "(empty)" : body));
    }

    private void AddTabButtons(UIElement parent)
    {
        AddTab(parent, "Symbolic", DetailTab.Symbolic, 0f);
        AddTab(parent, "Observation", DetailTab.Observation, 92f);
        AddTab(parent, "Contract", DetailTab.Contract, 204f);
        AddTab(parent, "Output", DetailTab.Output, 304f);
    }

    private void AddTab(UIElement parent, string label, DetailTab tab, float left)
    {
        var button = Button(label, 92f, 28f);
        button.Left.Set(left, 0f);
        button.Top.Set(42f, 0f);
        button.OnLeftClick += (_, _) =>
        {
            _tab = tab;
            Refresh();
        };
        parent.Append(button);
    }

    private UIPanel Panel(float width, float height)
    {
        var panel = new UIPanel();
        panel.Width.Set(width, 0f);
        panel.Height.Set(height, 0f);
        panel.BackgroundColor = _innerColor;
        panel.BorderColor = _borderColor;
        return panel;
    }

    private static UITextPanel<string> Button(string text, float width, float height)
    {
        var button = new UITextPanel<string>(text, 0.8f, false);
        button.Width.Set(width, 0f);
        button.Height.Set(height, 0f);
        button.BackgroundColor = new Color(35, 42, 60, 240);
        button.BorderColor = new Color(82, 96, 128);
        return button;
    }

    private static UIText TextLine(string text, Color color)
    {
        var line = new UIText(text, 0.85f);
        line.TextOriginX = 0f;
        line.TextColor = color;
        line.Width.Set(0f, 1f);
        line.Height.Set(22f, 0f);
        return line;
    }

    private static UIText TextBlock(string text)
    {
        string wrapped = Wrap(text, 72);
        int lineCount = wrapped.Count(ch => ch == '\n') + 1;
        var block = new UIText(wrapped, 0.72f);
        block.TextOriginX = 0f;
        block.TextColor = new Color(205, 213, 245);
        block.Width.Set(0f, 1f);
        block.Height.Set(Math.Max(24f, lineCount * 17f + 8f), 0f);
        return block;
    }

    private static string RowTitle(LlmDebugEntry entry)
    {
        string instruction = entry.Instruction.ReplaceLineEndings(" ");
        if (instruction.Length > 44)
            instruction = instruction[..41] + "...";

        string duration = entry.DurationSeconds.HasValue ? $"{entry.DurationSeconds:0.##}s" : "pending";
        return $"{entry.Status}  {duration}\n{ShortId(entry.AgentId)}  {instruction}";
    }

    private static Color EntryColor(LlmDebugEntry entry)
    {
        return entry.Status switch
        {
            "completed" => new Color(31, 72, 52, 240),
            "failed" or "timeout" or "cancelled" => new Color(78, 35, 42, 240),
            _ => new Color(34, 48, 76, 240),
        };
    }

    private static string ShortId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Length <= 8 ? value : value[..8];
    }

    private static string Wrap(string text, int width)
    {
        var lines = new List<string>();
        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine;
            while (line.Length > width)
            {
                lines.Add(line[..width]);
                line = line[width..];
            }
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    private enum DetailTab
    {
        Symbolic,
        Observation,
        Contract,
        Output,
    }
}
