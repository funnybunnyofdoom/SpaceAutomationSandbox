using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Settings (F2): window mode, windowed size and interface scale (#52), and sound (#64): a volume and
/// mute for each bus, and muting while the window is in the background. Changes apply at once and are
/// saved to <see cref="GameSettings.FilePath"/> (not the game save).
/// </summary>
public partial class SettingsPanel : PanelContainer
{
    private readonly GameSettings _settings;
    private readonly Action _changed;
    private OptionButton _mode = null!, _size = null!, _scale = null!;
    private Label _info = null!;
    private readonly Dictionary<string, (HSlider Slider, CheckBox Mute, Label Value)> _audio = new();
    private CheckBox _muteUnfocused = null!;
    private OptionButton _autosave = null!;
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
        OffsetTop = -280;
        OffsetBottom = 280;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "SETTINGS", InputActions.Settings);
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

        grid.AddChild(new Label { Text = "Autosave" });
        _autosave = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var minutes in GameSettings.AutosaveChoices) _autosave.AddItem(minutes == 0 ? "Off" : $"Every {minutes} minutes of play");
        _autosave.ItemSelected += i => Change(() => _settings.AutosaveMinutes = GameSettings.AutosaveChoices[i]);
        grid.AddChild(_autosave);

        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0), Modulate = new Color(1, 1, 1, 0.65f) };
        box.AddChild(_info);

        // Sound (#64).
        box.AddChild(new Label { Text = "Sound" });
        var audio = new GridContainer { Columns = 4 };
        box.AddChild(audio);
        foreach (var bus in GameSettings.AudioBuses)
        {
            audio.AddChild(new Label { Text = bus, CustomMinimumSize = new Vector2(90, 0) });
            var slider = new HSlider { MinValue = 0, MaxValue = 100, Step = 5, CustomMinimumSize = new Vector2(200, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var name = bus;
            slider.ValueChanged += v => Change(() => _settings.Volume[name] = (float)(v / 100));
            audio.AddChild(slider);
            var value = new Label { CustomMinimumSize = new Vector2(48, 0) };
            audio.AddChild(value);
            var mute = new CheckBox { Text = "Mute" };
            mute.Toggled += on => Change(() => { if (on) _settings.Muted.Add(name); else _settings.Muted.Remove(name); });
            audio.AddChild(mute);
            _audio[bus] = (slider, mute, value);
        }
        _muteUnfocused = new CheckBox { Text = "Mute while the game is in the background" };
        _muteUnfocused.Toggled += on => Change(() => _settings.MuteWhenUnfocused = on);
        box.AddChild(_muteUnfocused);

        // A way out that works everywhere, including the title screen (#58), where F2 isn't read.
        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 8);
        box.AddChild(bottom);
        var controls = new Button { Text = "Controls…" }; // rebinding (#63)
        controls.Pressed += () => ControlsChosen?.Invoke();
        bottom.AddChild(controls);
        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var close = new Button { Text = "Close" };
        close.Pressed += () => { Visible = false; Closed?.Invoke(); };
        bottom.AddChild(close);
    }

    /// <summary>Raised by the Controls… button (#63).</summary>
    public event Action? ControlsChosen;

    /// <summary>Raised when the Close button hides the panel.</summary>
    public event Action? Closed;

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
        foreach (var (bus, (slider, mute, value)) in _audio)
        {
            slider.Value = Math.Round(_settings.Volume[bus] * 100);
            value.Text = $"{slider.Value:0}%";
            mute.ButtonPressed = _settings.Muted.Contains(bus);
        }
        _muteUnfocused.ButtonPressed = _settings.MuteWhenUnfocused;
        _autosave.Select(Math.Max(0, Array.IndexOf(GameSettings.AutosaveChoices, _settings.AutosaveMinutes)));
        var window = DisplayServer.WindowGetSize();
        _info.Text = $"Interface at {_settings.EffectiveUiScale(window) * 100:0}% for this {window.X} × {window.Y} window. "
                     + "It's capped so every panel still fits. A bigger window shows more of space. F11 toggles fullscreen. "
                     + "You can also drag the window edges.";
        _updating = false;
    }
}
