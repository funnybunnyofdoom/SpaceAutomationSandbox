using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>The world's options (#119): a button that says what differs from usual and opens them, closed at first, and a reset.</summary>
public partial class WorldOptionsBox : VBoxContainer
{
    private static readonly string[] Amounts = { "Off", "Rare", "Normal", "Often" };

    private readonly Button _header = new() { Alignment = HorizontalAlignment.Left, ToggleMode = true, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly VBoxContainer _rows = new() { Visible = false };
    private readonly Button _reset = new() { Text = "Reset", TooltipText = "Back to the usual" };
    private readonly List<(WorldOption Option, OptionButton Choice)> _choices = new();
    private WorldOptions _options = WorldOptions.Usual();

    /// <summary>An option was changed.</summary>
    public event Action? Changed;

    public WorldOptionsBox() : this(true) { }

    /// <param name="beside">The descriptions stand beside the choices, not under them.</param>
    public WorldOptionsBox(bool beside)
    {
        AddThemeConstantOverride("separation", 6);
        _header.Toggled += open => { _rows.Visible = open; Show_(); };
        var top = new HBoxContainer();
        _header.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(_header);
        top.AddChild(_reset);
        AddChild(top);
        AddChild(_rows);
        foreach (var option in WorldOptions.All)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _rows.AddChild(row);
            row.AddChild(new Label { Text = option.Name, CustomMinimumSize = new Vector2(150, 0) });
            var choice = new OptionButton { TooltipText = option.Description, CustomMinimumSize = new Vector2(110, 0), SizeFlagsVertical = SizeFlags.ShrinkBegin };
            foreach (var amount in Amounts) choice.AddItem(amount);
            choice.ItemSelected += i => { _options[option.Id] = (WorldAmount)(int)i; Show_(); Changed?.Invoke(); };
            row.AddChild(choice);
            (beside ? row : (Container)_rows).AddChild(new Label
            {
                Text = option.Description, ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = new Color(1, 1, 1, 0.7f),
            });
            _choices.Add((option, choice));
        }
        _reset.Pressed += () =>
        {
            foreach (var option in WorldOptions.All) _options[option.Id] = WorldAmount.Normal;
            Show_();
            Changed?.Invoke();
        };
    }

    /// <summary>Shows (and changes) these; closed again.</summary>
    public void Set(WorldOptions options)
    {
        _options = options;
        _header.SetPressedNoSignal(false);
        _rows.Visible = false;
        Show_();
    }

    /// <summary>Opens it (dev, and where there's nothing else to see).</summary>
    public void Expand()
    {
        _header.SetPressedNoSignal(true);
        _rows.Visible = true;
        Show_();
    }

    private void Show_()
    {
        _header.Text = $"{(_rows.Visible ? "▾" : "▸")}  Advanced: {_options.Summary()}";
        _reset.Disabled = _options.IsUsual;
        foreach (var (option, choice) in _choices) choice.Select((int)_options[option.Id]);
    }
}
