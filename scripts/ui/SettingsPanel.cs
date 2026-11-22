using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Display settings (F2, #52): window mode, windowed size and interface scale. Changes apply at
/// once and are saved to <see cref="GameSettings.FilePath"/> (not the game save).
/// </summary>
public partial class SettingsPanel : PanelContainer
{
    private readonly GameSettings _settings;
    private readonly Action _changed;
    private OptionButton _mode = null!, _size = null!, _scale = null!;
    private Label _info = null!;
    private List<Vector2I> _sizes = new();
    private bool _updating;

    public SettingsPanel(GameSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        Build();
    }

    public SettingsPanel() : this(new GameSettings(), () => { }) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.Center);
        OffsetLeft = -230;
        OffsetRight = 230;
        OffsetTop = -130;
        OffsetBottom = 130;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);
        var title = new Label { Text = "SETTINGS  (F2 to close)" };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);

        var grid = new GridContainer { Columns = 2 };
        box.AddChild(grid);
        grid.AddChild(new Label { Text = "Window" });
        _mode = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(250, 0) };
        _mode.AddItem("Windowed");
        _mode.AddItem("Fullscreen (borderless)");
        _mode.AddItem("Fullscreen (exclusive)");
        _mode.ItemSelected += i => Change(() =>
        {
            if (_settings.WindowMode == GameSettings.Mode.Windowed) _settings.CaptureWindow(); // come back to this size later
            _settings.WindowMode = (GameSettings.Mode)(int)i;
        });
        grid.AddChild(_mode);

        grid.AddChild(new Label { Text = "Window size" });
        _size = new OptionButton { FocusMode = FocusModeEnum.None };
        _size.ItemSelected += i => Change(() => _settings.WindowSize = _sizes[(int)i]);
        grid.AddChild(_size);

        grid.AddChild(new Label { Text = "Interface size" });
        _scale = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var s in GameSettings.UiScales) _scale.AddItem(s == 0 ? "Automatic" : $"{s * 100:0}%");
        _scale.ItemSelected += i => Change(() => _settings.UiScale = GameSettings.UiScales[i]);
        grid.AddChild(_scale);

        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0), Modulate = new Color(1, 1, 1, 0.65f) };
        box.AddChild(_info);
    }

    public void Open()
    {
        Visible = true;
        Refresh();
    }

    private void Change(Action apply)
    {
        if (_updating) return;
        apply();
        _changed();
        Refresh();
    }

    private void Refresh()
    {
        _updating = true;
        _mode.Select((int)_settings.WindowMode);

        var screen = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen()).Size;
        _sizes = GameSettings.WindowSizes.Where(s => s.X <= screen.X && s.Y <= screen.Y).ToList();
        var current = DisplayServer.WindowGetSize();
        if (!_sizes.Contains(current)) _sizes.Insert(0, current);
        _size.Clear();
        foreach (var s in _sizes) _size.AddItem($"{s.X} × {s.Y}" + (s == current ? "  (now)" : ""));
        _size.Select(_sizes.IndexOf(current));
        _size.Disabled = _settings.WindowMode != GameSettings.Mode.Windowed;

        _scale.Select(Math.Max(0, Array.IndexOf(GameSettings.UiScales, _settings.UiScale)));
        var window = DisplayServer.WindowGetSize();
        _info.Text = $"Interface at {_settings.EffectiveUiScale(window) * 100:0}% for this {window.X} × {window.Y} window. "
                     + "It's capped so every panel still fits. A bigger window shows more of space. F11 toggles fullscreen. "
                     + "You can also drag the window edges.";
        _updating = false;
    }
}
