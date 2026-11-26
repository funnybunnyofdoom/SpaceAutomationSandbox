using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Where you left off (#146): after a save loads, what wants your attention, from <see cref="SectorState.Recap"/>. Names are links
/// (#100). Carry on, Esc or B closes it; a setting turns it off.
/// </summary>
public partial class RecapPanel : Control
{
    private readonly Label _heading = new() { ThemeTypeVariation = "SectionHeader" };
    private readonly InfoList _list = new();
    private readonly Button _carryOn = new() { Text = "Carry on", CustomMinimumSize = new Vector2(160, 36), SizeFlagsHorizontal = SizeFlags.ShrinkEnd };

    public RecapPanel()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(520, 0) };
        centre.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);
        box.AddChild(_heading);
        box.AddChild(_list);
        _carryOn.Pressed += Close;
        box.AddChild(_carryOn);
    }

    /// <summary>Shows the recap for <paramref name="gameName"/>; false (and nothing shown) if there's nothing to say.</summary>
    public bool Open(string gameName, IReadOnlyList<RecapLine> lines)
    {
        if (lines.Count == 0) return false;
        _heading.Text = $"Where you left off in {gameName}";
        _list.Set(lines.Select(l => new HudRow(l.Icon, l.Text, l.Urgent ? Severity.Warning : Severity.Normal,
            Link: l.Target.IsNone ? null : l.Target, Click: l.Target.IsNone ? null : Close)).ToList());
        Visible = true;
        _carryOn.GrabFocus();
        return true;
    }

    private void Close() => Visible = false;

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || !e.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        Close();
    }
}
