using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Named input actions (Godot InputMap) instead of raw keys, so controls can be rebound and
/// gamepads work. <see cref="EnsureDefaults"/> adds the default bindings below only for actions
/// that don't exist yet: define an action with the same name in Project Settings → Input Map to
/// override it. The blueprint editor's own keys (R, digits, Ctrl+Z/S) stay local to the editor.
/// <para>Rebinding (#63): the player's changes are kept per action as text (see <see cref="Serialize"/>)
/// in the settings file and applied after the defaults; <see cref="ResetToDefault"/> puts one back.
/// <see cref="KeyText"/> names an action's current key for the HUD and help.</para>
/// </summary>
public static class InputActions
{
    public const string Thrust = "ship_thrust";
    public const string TurnLeft = "ship_turn_left";
    public const string TurnRight = "ship_turn_right";
    public const string Brake = "ship_brake";
    public const string Reverse = "ship_reverse";
    public const string StrafeLeft = "ship_strafe_left";
    public const string StrafeRight = "ship_strafe_right";
    public const string Anchor = "ship_anchor";
    public const string Dock = "ship_dock";
    public const string Deploy = "ship_deploy_kit";
    public const string Cruise = "ship_cruise";
    public const string HoldFire = "ship_hold_fire";
    public const string Map = "open_map";
    public const string TimeFaster = "time_faster";
    public const string TimeSlower = "time_slower";
    public const string TimePause = "time_pause";
    public const string Diagnostics = "open_diagnostics";
    public const string Settings = "open_settings";
    public const string Fullscreen = "toggle_fullscreen";
    public const string TargetCycle = "target_cycle";
    public const string TargetNearest = "target_nearest";
    public const string ZoomIn = "view_zoom_in";
    public const string ZoomOut = "view_zoom_out";
    public const string Editor = "open_blueprint_editor";
    public const string Shipyard = "open_shipyard";
    public const string Fleet = "open_fleet";
    public const string Cargo = "open_cargo";
    public const string Job = "open_job";
    public const string Debug = "open_debug";
    public const string ClosePanels = "close_panels";
    public const string QuickSave = "game_quicksave";
    public const string QuickLoad = "game_quickload";
    public const string Help = "open_help";
    public const string Jump = "ship_warp_jump";
    public const string Research = "open_research";
    public const string Laws = "open_laws";
    public const string Power = "open_power";
    public const string PanelFocus = "panel_focus";

    /// <summary>Every action the player can rebind, in the order the Controls screen lists them, with its name and group.</summary>
    public static readonly (string Action, string Name, string Group)[] All =
    {
        (Thrust, "Thrust", "Flying"), (Reverse, "Reverse thrust", "Flying"), (TurnLeft, "Turn left", "Flying"), (TurnRight, "Turn right", "Flying"),
        (StrafeLeft, "Strafe left", "Flying"), (StrafeRight, "Strafe right", "Flying"), (Brake, "Brake (match the target)", "Flying"),
        (Cruise, "Cruise drive", "Flying"), (Dock, "Dock / undock", "Flying"), (Anchor, "Anchor / release (galaxy view in the map)", "Flying"),
        (Deploy, "Deploy a kit", "Flying"), (Jump, "Warp jump (docked at a gate)", "Flying"), (HoldFire, "Hold fire / weapons free", "Flying"),
        (TargetCycle, "Cycle target", "Targets and view"), (TargetNearest, "Target the nearest body", "Targets and view"),
        (ZoomIn, "Zoom in", "Targets and view"), (ZoomOut, "Zoom out", "Targets and view"), (Map, "Map", "Targets and view"),
        (Editor, "Blueprint editor", "Panels"), (Shipyard, "Shipyard", "Panels"), (Fleet, "Fleet", "Panels"), (Cargo, "Cargo", "Panels"),
        (Job, "Job", "Panels"), (Research, "Research", "Panels"), (Laws, "Laws and civilizations", "Panels"), (Power, "Power", "Panels"), (Settings, "Settings", "Panels"), (Debug, "Debug menu", "Panels"),
        (Diagnostics, "Diagnostics", "Panels"), (Help, "Keys and help", "Panels"),
        (PanelFocus, "Use the open panel with keys / controller", "Panels"),
        (ClosePanels, "Close panels / pause menu", "Game"), (TimeFaster, "Time faster", "Game"), (TimeSlower, "Time slower", "Game"),
        (TimePause, "Pause time", "Game"), (QuickSave, "Quicksave", "Game"), (QuickLoad, "Quickload", "Game"), (Fullscreen, "Fullscreen", "Game"),
    };

    private static readonly Dictionary<string, InputEvent[]> Defaults = new(StringComparer.Ordinal);

    public static void EnsureDefaults()
    {
        Add(Thrust, 0.2f, Keys(Key.W, Key.Up), Stick(JoyAxis.TriggerRight, 1), Pad(JoyButton.A));
        Add(TurnLeft, 0.2f, Keys(Key.A, Key.Left), Stick(JoyAxis.LeftX, -1));
        Add(TurnRight, 0.2f, Keys(Key.D, Key.Right), Stick(JoyAxis.LeftX, 1));
        Add(Brake, 0.5f, Keys(Key.S, Key.Down, Key.Space), Stick(JoyAxis.TriggerLeft, 1), Pad(JoyButton.B));
        Add(Reverse, 0.2f, Keys(Key.X), Stick(JoyAxis.LeftY, 1));
        Add(StrafeLeft, 0.2f, Keys(Key.Q), Stick(JoyAxis.RightX, -1));
        Add(StrafeRight, 0.2f, Keys(Key.E), Stick(JoyAxis.RightX, 1));
        Add(Anchor, 0.5f, Keys(Key.G), Pad(JoyButton.X));
        Add(Dock, 0.5f, Keys(Key.K), Pad(JoyButton.Y));
        Add(Deploy, 0.5f, Keys(Key.U));
        Add(Cruise, 0.5f, Keys(Key.V));
        Add(HoldFire, 0.5f, Keys(Key.Z)); // #85
        Add(Map, 0.5f, Keys(Key.M));
        Add(TimeFaster, 0.5f, Keys(Key.Bracketright));
        Add(TimeSlower, 0.5f, Keys(Key.Bracketleft));
        Add(TimePause, 0.5f, Keys(Key.P));
        Add(Diagnostics, 0.5f, Keys(Key.F4));
        Add(Settings, 0.5f, Keys(Key.F2));
        Add(Fullscreen, 0.5f, Keys(Key.F11));
        Add(TargetCycle, 0.5f, Keys(Key.Tab), Pad(JoyButton.RightShoulder));
        Add(TargetNearest, 0.5f, Keys(Key.T), Pad(JoyButton.LeftShoulder));
        Add(ZoomIn, 0.5f, new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp }, Pad(JoyButton.DpadUp));
        Add(ZoomOut, 0.5f, new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown }, Pad(JoyButton.DpadDown));
        Add(Editor, 0.5f, Keys(Key.B));
        Add(Shipyard, 0.5f, Keys(Key.Y));
        Add(Fleet, 0.5f, Keys(Key.F));
        Add(Cargo, 0.5f, Keys(Key.C));
        Add(Job, 0.5f, Keys(Key.J));
        Add(Debug, 0.5f, Keys(Key.F3));
        Add(ClosePanels, 0.5f, Keys(Key.Escape), Pad(JoyButton.Start)); // closes panels, else opens the pause menu (#59)
        Add(QuickSave, 0.5f, Keys(Key.F6));
        Add(QuickLoad, 0.5f, Keys(Key.F9));
        Add(Help, 0.5f, Keys(Key.H, Key.F1));
        Add(Jump, 0.5f, Keys(Key.N));
        Add(Research, 0.5f, Keys(Key.R));
        Add(Laws, 0.5f, Keys(Key.L)); // #36
        Add(Power, 0.5f, Keys(Key.O)); // #75
        Add(PanelFocus, 0.5f, Keys(Key.Quoteleft), Pad(JoyButton.Back)); // #68
        // Menus with a controller (#68): A chooses, B goes back (Godot's own UI actions, not rebindable).
        AddEvent("ui_accept", Pad(JoyButton.A));
        AddEvent("ui_cancel", Pad(JoyButton.B));
    }

    private static void AddEvent(string action, InputEvent e)
    {
        if (InputMap.HasAction(action) && !InputMap.ActionGetEvents(action).Any(x => Same(x, e))) InputMap.ActionAddEvent(action, e);
    }

    /// <summary>Throttle 0..1 (analog on a trigger), turn −1..1 (analog on the stick), brake on/off.</summary>
    /// <summary>Throttle is forward minus reverse (−1..1); strafe is left −1 .. right +1.</summary>
    public static (double Throttle, double Turn, bool Brake, double Strafe) ReadFlight() =>
        (Input.GetActionStrength(Thrust) - Input.GetActionStrength(Reverse), Input.GetAxis(TurnLeft, TurnRight),
         Input.IsActionPressed(Brake), Input.GetAxis(StrafeLeft, StrafeRight));

    private static void Add(string action, float deadzone, params object[] events)
    {
        var all = new List<InputEvent>();
        foreach (var e in events)
        {
            if (e is InputEvent single) all.Add(single);
            else all.AddRange((InputEvent[])e);
        }
        Defaults[action] = all.ToArray();
        if (InputMap.HasAction(action)) return; // defined in the project: that binding wins
        InputMap.AddAction(action, deadzone);
        foreach (var e in all) InputMap.ActionAddEvent(action, (InputEvent)e.Duplicate());
    }

    private static InputEvent[] Keys(params Key[] keys)
    {
        var events = new InputEvent[keys.Length];
        for (var i = 0; i < keys.Length; i++) events[i] = new InputEventKey { PhysicalKeycode = keys[i] };
        return events;
    }

    private static InputEvent Pad(JoyButton button) => new InputEventJoypadButton { ButtonIndex = button };

    private static InputEvent Stick(JoyAxis axis, float direction) => new InputEventJoypadMotion { Axis = axis, AxisValue = direction };

    // --- Rebinding (#63) ---

    /// <summary>The events bound to an action now.</summary>
    public static IReadOnlyList<InputEvent> Events(string action) =>
        InputMap.HasAction(action) ? InputMap.ActionGetEvents(action).ToList() : new List<InputEvent>();

    /// <summary>Keyboard and mouse events vs controller events: rebinding replaces one kind and keeps the other.</summary>
    public static bool IsController(InputEvent e) => e is InputEventJoypadButton or InputEventJoypadMotion;

    /// <summary>Replaces the action's keyboard/mouse (or controller) bindings with this one event.</summary>
    public static void Rebind(string action, InputEvent input)
    {
        var keep = Events(action).Where(e => IsController(e) != IsController(input)).ToList();
        InputMap.ActionEraseEvents(action);
        foreach (var e in keep) InputMap.ActionAddEvent(action, e);
        InputMap.ActionAddEvent(action, input);
    }

    /// <summary>Removes the action's keyboard/mouse (or controller) bindings.</summary>
    public static void Clear(string action, bool controller)
    {
        var keep = Events(action).Where(e => IsController(e) != controller).ToList();
        InputMap.ActionEraseEvents(action);
        foreach (var e in keep) InputMap.ActionAddEvent(action, e);
    }

    public static void ResetToDefault(string action)
    {
        if (!Defaults.TryGetValue(action, out var defaults)) return;
        InputMap.ActionEraseEvents(action);
        foreach (var e in defaults) InputMap.ActionAddEvent(action, (InputEvent)e.Duplicate());
    }

    /// <summary>True when the action is bound as it is by default.</summary>
    public static bool IsDefault(string action) =>
        Defaults.TryGetValue(action, out var defaults) && Serialize(defaults) == Serialize(Events(action));

    /// <summary>Other actions that also use this input (a conflict to warn about).</summary>
    public static IEnumerable<string> ActionsUsing(InputEvent input, string except) =>
        All.Where(a => a.Action != except && Events(a.Action).Any(e => Same(e, input))).Select(a => a.Name);

    private static bool Same(InputEvent a, InputEvent b) => a switch
    {
        InputEventKey k when b is InputEventKey l => k.PhysicalKeycode == l.PhysicalKeycode && k.Keycode == l.Keycode,
        InputEventMouseButton m when b is InputEventMouseButton n => m.ButtonIndex == n.ButtonIndex,
        InputEventJoypadButton j when b is InputEventJoypadButton k2 => j.ButtonIndex == k2.ButtonIndex,
        InputEventJoypadMotion j when b is InputEventJoypadMotion k2 => j.Axis == k2.Axis && Math.Sign(j.AxisValue) == Math.Sign(k2.AxisValue),
        _ => false,
    };

    /// <summary>A short, readable name for one input ("W", "Wheel up", "Pad A", "Left stick right").</summary>
    public static string Describe(InputEvent e) => e switch
    {
        InputEventKey k => OS.GetKeycodeString(k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode) is { Length: > 0 } s ? s : "?",
        InputEventMouseButton m => m.ButtonIndex switch
        {
            MouseButton.WheelUp => "Wheel up", MouseButton.WheelDown => "Wheel down", MouseButton.Left => "Left click",
            MouseButton.Right => "Right click", MouseButton.Middle => "Middle click", _ => $"Mouse {(int)m.ButtonIndex}",
        },
        InputEventJoypadButton j => j.ButtonIndex switch
        {
            JoyButton.A => "Pad A", JoyButton.B => "Pad B", JoyButton.X => "Pad X", JoyButton.Y => "Pad Y",
            JoyButton.LeftShoulder => "Pad LB", JoyButton.RightShoulder => "Pad RB", JoyButton.Start => "Pad Start", JoyButton.Back => "Pad Back",
            JoyButton.DpadUp => "D-pad up", JoyButton.DpadDown => "D-pad down", JoyButton.DpadLeft => "D-pad left", JoyButton.DpadRight => "D-pad right",
            _ => $"Pad button {(int)j.ButtonIndex}",
        },
        InputEventJoypadMotion j => j.Axis switch
        {
            JoyAxis.TriggerLeft => "Left trigger", JoyAxis.TriggerRight => "Right trigger",
            JoyAxis.LeftX => j.AxisValue < 0 ? "Left stick left" : "Left stick right",
            JoyAxis.LeftY => j.AxisValue < 0 ? "Left stick up" : "Left stick down",
            JoyAxis.RightX => j.AxisValue < 0 ? "Right stick left" : "Right stick right",
            JoyAxis.RightY => j.AxisValue < 0 ? "Right stick up" : "Right stick down",
            _ => $"Axis {(int)j.Axis}",
        },
        _ => e.AsText(),
    };

    /// <summary>All of an action's keyboard/mouse inputs ("W / Up"), or "unbound".</summary>
    public static string KeyText(string action)
    {
        var keys = Events(action).Where(e => !IsController(e)).Select(Describe).ToList();
        return keys.Count > 0 ? string.Join(" / ", keys) : "unbound";
    }

    /// <summary>The first keyboard/mouse input of an action, for short HUD hints ("K to dock").</summary>
    public static string Key1(string action) => Events(action).FirstOrDefault(e => !IsController(e)) is { } e ? Describe(e) : "(unbound)";

    /// <summary>A panel title such as "CARGO  (C to close)" that names the current key, refreshed whenever the panel shows.</summary>
    public static void TitleWithKey(Label label, string title, string action)
    {
        void Set() => label.Text = $"{title}  ({Key1(action)} to close)";
        Set();
        label.VisibilityChanged += Set;
    }

    /// <summary>The controller inputs of an action ("Pad A / Right trigger"), or "".</summary>
    public static string PadText(string action) => string.Join(" / ", Events(action).Where(IsController).Select(Describe));

    // --- Saving (as text in the settings file) ---

    /// <summary>An action's bindings as text: "key:87;mouse:4;pad:0;axis:5:1".</summary>
    public static string Serialize(IEnumerable<InputEvent> events) => string.Join(";", events.Select(e => e switch
    {
        InputEventKey k => $"key:{(long)(k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode)}",
        InputEventMouseButton m => $"mouse:{(int)m.ButtonIndex}",
        InputEventJoypadButton j => $"pad:{(int)j.ButtonIndex}",
        InputEventJoypadMotion j => $"axis:{(int)j.Axis}:{Math.Sign(j.AxisValue)}",
        _ => "",
    }).Where(s => s.Length > 0));

    public static string Serialize(string action) => Serialize(Events(action));

    /// <summary>Applies saved bindings (after <see cref="EnsureDefaults"/>); bad entries are skipped.</summary>
    public static void Apply(IReadOnlyDictionary<string, string> saved)
    {
        foreach (var (action, text) in saved)
        {
            if (!InputMap.HasAction(action)) continue;
            var events = new List<InputEvent>();
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split(':');
                if (bits.Length < 2 || !long.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
                InputEvent? e = bits[0] switch
                {
                    "key" => new InputEventKey { PhysicalKeycode = (Key)value },
                    "mouse" => new InputEventMouseButton { ButtonIndex = (MouseButton)value },
                    "pad" => new InputEventJoypadButton { ButtonIndex = (JoyButton)value },
                    "axis" when bits.Length >= 3 && int.TryParse(bits[2], out var sign) => new InputEventJoypadMotion { Axis = (JoyAxis)value, AxisValue = sign },
                    _ => null,
                };
                if (e != null) events.Add(e);
            }
            InputMap.ActionEraseEvents(action);
            foreach (var e in events) InputMap.ActionAddEvent(action, e);
        }
    }

    /// <summary>The bindings that differ from the defaults, to save (an empty string means "nothing bound").</summary>
    public static Dictionary<string, string> Changed() =>
        All.Where(a => !IsDefault(a.Action)).ToDictionary(a => a.Action, a => Serialize(a.Action), StringComparer.Ordinal);
}
