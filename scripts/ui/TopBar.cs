using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>One button of the menu bar. <c>Blocked</c> gives why it can't be used now; <c>Badge</c> what needs attention; null for neither.</summary>
public sealed record TopBarItem(string Icon, string Name, string? Action, Action Press, Func<bool> IsOpen, Func<string?>? Blocked = null, Func<string?>? Badge = null, Action? Attend = null);

/// <summary>
/// The menu bar (#106): a row of icon buttons at the top centre, one per menu or panel, so everything can be reached with the
/// mouse. A button is lit while its panel is open; a mark shows when something there needs attention (the tooltip says what).
/// </summary>
public partial class TopBar : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private readonly List<(TopBarItem Item, Button Button, TextureRect Mark)> _buttons = new();
    private double _sinceRefresh = RefreshSeconds;

    public TopBar(IEnumerable<TopBarItem> items)
    {
        SetAnchorsPreset(LayoutPreset.CenterTop);
        GrowHorizontal = GrowDirection.Both;
        OffsetTop = 6;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 1);
        AddChild(row);
        foreach (var item in items)
        {
            var button = new Button
            {
                ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(32, 32), Icon = UiIcons.Get(item.Icon),
                IconAlignment = HorizontalAlignment.Center, ExpandIcon = true, TextureFilter = TextureFilterEnum.Nearest,
            };
            button.AddThemeConstantOverride("icon_max_width", 16);
            button.Pressed += () =>
            {
                item.Press();
                button.SetPressedNoSignal(item.IsOpen());
            };
            if (item.Attend is { } attend) // #110
                button.GuiInput += e =>
                {
                    if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
                    attend();
                    button.AcceptEvent();
                };
            var mark = UiTheme.Icon("warn", 12);
            mark.SetAnchorsPreset(LayoutPreset.TopRight);
            mark.OffsetLeft = -13;
            mark.OffsetTop = 0;
            mark.OffsetRight = -1;
            mark.OffsetBottom = 12;
            mark.Visible = false;
            button.AddChild(mark);
            row.AddChild(button);
            _buttons.Add((item, button, mark));
        }
    }

    public TopBar() : this(Array.Empty<TopBarItem>()) { }

    private bool? _large;

    /// <summary>Double-size icons, where the window has room.</summary>
    public bool Large
    {
        set
        {
            if (value == _large) return;
            _large = value;
            foreach (var (_, button, _) in _buttons)
            {
                button.CustomMinimumSize = value ? new Vector2(50, 46) : new Vector2(32, 32);
                button.AddThemeConstantOverride("icon_max_width", value ? 32 : 16);
            }
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        foreach (var (item, button, _) in _buttons) button.SetPressedNoSignal(item.IsOpen());
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds) return;
        _sinceRefresh = 0;
        foreach (var (item, button, mark) in _buttons)
        {
            var blocked = item.Blocked?.Invoke();
            var badge = blocked is null ? item.Badge?.Invoke() : null;
            var key = item.Action is { } action ? InputActions.Key1(action) : "";
            button.Disabled = blocked is not null;
            button.TooltipText = item.Name + (key.Length > 0 ? $"  ({key})" : "") + (blocked is not null ? $"\n{blocked}" : "") + (badge is not null ? $"\n! {badge}{(item.Attend is null ? "" : "  (right click: target it)")}" : "");
            mark.Visible = badge is not null;
        }
    }
}
