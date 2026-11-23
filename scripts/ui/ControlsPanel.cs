using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Controls (#63), opened from Settings: every action with its keyboard/mouse and controller inputs.
/// Click a binding, then press the new key, mouse button or controller input (Esc cancels, Backspace
/// clears). Rebinding replaces that kind of input and keeps the other. An input used by another action
/// is allowed but shown in orange with a warning. Reset puts one action back; Reset all puts them all back.
/// Changes apply at once and are saved with the settings (only the changed actions).
/// </summary>
public partial class ControlsPanel : Control
{
    public event Action? Closed;

    private readonly GameSettings _settings;
    private readonly Action _changed;
    private readonly Dictionary<string, (Button Keys, Button Pad, Button Reset)> _rows = new(StringComparer.Ordinal);
    private Label _status = null!;
    private Button _close = null!;

    /// <summary>The action and kind of input being waited for, if any.</summary>
    private (string Action, string Name, bool Controller)? _waiting;

    private static readonly Color Warning = new(1f, 0.7f, 0.3f);

    public ControlsPanel(GameSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        Name = "ControlsPanel";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var panel = new PanelContainer();
        centre.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);
        var title = new Label { Text = "CONTROLS" };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);
        box.AddChild(new Label
        {
            Text = "Click a binding, then press the new key or button. Esc cancels, Backspace clears.",
            Modulate = new Color(1, 1, 1, 0.65f),
        });

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(720, 440), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 3);
        scroll.AddChild(grid);
        string? group = null;
        foreach (var (action, name, actionGroup) in InputActions.All)
        {
            if (actionGroup != group)
            {
                group = actionGroup;
                var heading = new Label { Text = group, Modulate = new Color(0.6f, 0.8f, 1f) };
                grid.AddChild(heading);
                for (var i = 0; i < 3; i++) grid.AddChild(new Label { Text = i == 0 ? "Keyboard / mouse" : i == 1 ? "Controller" : "", Modulate = new Color(1, 1, 1, 0.5f) });
            }
            grid.AddChild(new Label { Text = name, CustomMinimumSize = new Vector2(250, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var keys = new Button { CustomMinimumSize = new Vector2(190, 0), ClipText = true };
            keys.Pressed += () => Wait(action, name, controller: false);
            grid.AddChild(keys);
            var pad = new Button { CustomMinimumSize = new Vector2(170, 0), ClipText = true };
            pad.Pressed += () => Wait(action, name, controller: true);
            grid.AddChild(pad);
            var reset = new Button { Text = "Reset", TooltipText = "Back to the default binding" };
            reset.Pressed += () => { InputActions.ResetToDefault(action); Changed($"{name}: back to the default."); };
            grid.AddChild(reset);
            _rows[action] = (keys, pad, reset);
        }

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(720, 0) };
        box.AddChild(_status);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);
        var resetAll = new Button { Text = "Reset all", CustomMinimumSize = new Vector2(110, 34) };
        resetAll.Pressed += () =>
        {
            foreach (var (action, _, _) in InputActions.All) InputActions.ResetToDefault(action);
            Changed("Every control is back to its default.");
        };
        buttons.AddChild(resetAll);
        _close = new Button { Text = "Close", CustomMinimumSize = new Vector2(110, 34) };
        _close.Pressed += Close;
        buttons.AddChild(_close);
    }

    public ControlsPanel() : this(new GameSettings(), () => { }) { }

    public void Open()
    {
        _waiting = null;
        _status.Text = "";
        Refresh();
        Visible = true;
        _close.GrabFocus();
    }

    private void Close()
    {
        _waiting = null;
        Visible = false;
        Closed?.Invoke();
    }

    private void Wait(string action, string name, bool controller)
    {
        _waiting = (action, name, controller);
        Refresh();
        _status.Modulate = Colors.White;
        _status.Text = controller
            ? $"{name}: press a controller button, trigger or stick direction (Esc cancels, Backspace clears)."
            : $"{name}: press a key or mouse button (Esc cancels, Backspace clears).";
    }

    /// <summary>Takes the next input while waiting, before the buttons or the game see it.</summary>
    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (_waiting is not { } waiting)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        InputEvent? bound = null;
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key when key.PhysicalKeycode == Key.Escape:
                Finish(null, "Cancelled.");
                break;
            case InputEventKey { Pressed: true, Echo: false } key when key.PhysicalKeycode == Key.Backspace:
                InputActions.Clear(waiting.Action, waiting.Controller);
                Finish(null, $"{waiting.Name}: {(waiting.Controller ? "controller" : "keyboard")} binding cleared.", changed: true);
                break;
            case InputEventKey { Pressed: true, Echo: false } key when !waiting.Controller:
                bound = new InputEventKey { PhysicalKeycode = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode };
                break;
            case InputEventMouseButton { Pressed: true } mouse when mouse.ButtonIndex == MouseButton.Left:
                Finish(null, "Cancelled."); // a click elsewhere; left click stays for the interface
                break;
            case InputEventMouseButton { Pressed: true } mouse when !waiting.Controller:
                bound = new InputEventMouseButton { ButtonIndex = mouse.ButtonIndex };
                break;
            case InputEventJoypadButton { Pressed: true } button when waiting.Controller:
                bound = new InputEventJoypadButton { ButtonIndex = button.ButtonIndex };
                break;
            case InputEventJoypadMotion motion when waiting.Controller && Math.Abs(motion.AxisValue) > 0.6f:
                bound = new InputEventJoypadMotion { Axis = motion.Axis, AxisValue = Math.Sign(motion.AxisValue) };
                break;
            default:
                if (@event is InputEventKey or InputEventMouseButton or InputEventJoypadButton) GetViewport().SetInputAsHandled();
                return;
        }
        GetViewport().SetInputAsHandled();
        if (bound is null) return;
        InputActions.Rebind(waiting.Action, bound);
        var others = InputActions.ActionsUsing(bound, waiting.Action).ToList();
        Finish(bound, others.Count > 0
            ? $"{waiting.Name}: {InputActions.Describe(bound)}. Warning: {InputActions.Describe(bound)} is also used by {string.Join(", ", others)}."
            : $"{waiting.Name}: {InputActions.Describe(bound)}.", changed: true, warn: others.Count > 0);
    }

    private void Finish(InputEvent? bound, string message, bool changed = false, bool warn = false)
    {
        var action = _waiting?.Action;
        var controller = _waiting?.Controller ?? false;
        _waiting = null;
        if (changed) Changed(message, warn);
        else { _status.Modulate = Colors.White; _status.Text = message; Refresh(); }
        if (action != null) (controller ? _rows[action].Pad : _rows[action].Keys).GrabFocus();
    }

    private void Changed(string message, bool warn = false)
    {
        _settings.Bindings.Clear();
        foreach (var (action, text) in InputActions.Changed()) _settings.Bindings[action] = text;
        _changed();
        _status.Modulate = warn ? Warning : Colors.White;
        _status.Text = message;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (action, (keys, pad, reset)) in _rows)
        {
            var waiting = _waiting is { } w && w.Action == action;
            var keyEvents = InputActions.Events(action).Where(e => !InputActions.IsController(e)).ToList();
            var padEvents = InputActions.Events(action).Where(InputActions.IsController).ToList();
            keys.Text = waiting && !_waiting!.Value.Controller ? "press a key…" : keyEvents.Count > 0 ? InputActions.KeyText(action) : "—";
            pad.Text = waiting && _waiting!.Value.Controller ? "press a button…" : padEvents.Count > 0 ? InputActions.PadText(action) : "—";
            keys.Modulate = keyEvents.Any(e => InputActions.ActionsUsing(e, action).Any()) ? Warning : Colors.White;
            pad.Modulate = padEvents.Any(e => InputActions.ActionsUsing(e, action).Any()) ? Warning : Colors.White;
            keys.TooltipText = keys.Modulate == Warning ? "Also used by: " + string.Join(", ", keyEvents.SelectMany(e => InputActions.ActionsUsing(e, action)).Distinct()) : "";
            pad.TooltipText = pad.Modulate == Warning ? "Also used by: " + string.Join(", ", padEvents.SelectMany(e => InputActions.ActionsUsing(e, action)).Distinct()) : "";
            reset.Disabled = InputActions.IsDefault(action);
        }
    }
}
