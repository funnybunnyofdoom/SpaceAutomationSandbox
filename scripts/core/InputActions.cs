using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Named input actions (Godot InputMap) instead of raw keys, so controls can be rebound and
/// gamepads work. <see cref="EnsureDefaults"/> adds the default bindings below only for actions
/// that don't exist yet: define an action with the same name in Project Settings → Input Map to
/// override it. The blueprint editor's own keys (R, digits, Ctrl+Z/S) stay local to the editor.
/// </summary>
public static class InputActions
{
    public const string Thrust = "ship_thrust";
    public const string TurnLeft = "ship_turn_left";
    public const string TurnRight = "ship_turn_right";
    public const string Brake = "ship_brake";
    public const string Anchor = "ship_anchor";
    public const string Dock = "ship_dock";
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
    public const string NewGame = "game_new";

    public static void EnsureDefaults()
    {
        Add(Thrust, 0.2f, Keys(Key.W, Key.Up), Stick(JoyAxis.TriggerRight, 1), Pad(JoyButton.A));
        Add(TurnLeft, 0.2f, Keys(Key.A, Key.Left), Stick(JoyAxis.LeftX, -1));
        Add(TurnRight, 0.2f, Keys(Key.D, Key.Right), Stick(JoyAxis.LeftX, 1));
        Add(Brake, 0.5f, Keys(Key.S, Key.Down, Key.Space), Stick(JoyAxis.TriggerLeft, 1), Pad(JoyButton.B));
        Add(Anchor, 0.5f, Keys(Key.G), Pad(JoyButton.X));
        Add(Dock, 0.5f, Keys(Key.K), Pad(JoyButton.Y));
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
        Add(ClosePanels, 0.5f, Keys(Key.Escape));
        Add(QuickSave, 0.5f, Keys(Key.F6));
        Add(QuickLoad, 0.5f, Keys(Key.F9));
        Add(NewGame, 0.5f, Keys(Key.F7));
    }

    /// <summary>Throttle 0..1 (analog on a trigger), turn −1..1 (analog on the stick), brake on/off.</summary>
    public static (double Throttle, double Turn, bool Brake) ReadFlight() =>
        (Input.GetActionStrength(Thrust), Input.GetAxis(TurnLeft, TurnRight), Input.IsActionPressed(Brake));

    private static void Add(string action, float deadzone, params object[] events)
    {
        if (InputMap.HasAction(action)) return; // defined in the project: that binding wins
        InputMap.AddAction(action, deadzone);
        foreach (var e in events)
        {
            if (e is InputEvent single) InputMap.ActionAddEvent(action, single);
            else foreach (var k in (InputEvent[])e) InputMap.ActionAddEvent(action, k);
        }
    }

    private static InputEvent[] Keys(params Key[] keys)
    {
        var events = new InputEvent[keys.Length];
        for (var i = 0; i < keys.Length; i++) events[i] = new InputEventKey { PhysicalKeycode = keys[i] };
        return events;
    }

    private static InputEvent Pad(JoyButton button) => new InputEventJoypadButton { ButtonIndex = button };

    private static InputEvent Stick(JoyAxis axis, float direction) => new InputEventJoypadMotion { Axis = axis, AxisValue = direction };
}
