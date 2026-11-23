using System;
using System.Collections.Generic;
using Godot;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The one confirmation dialog (#61): a question and a few choices, the last of which is always
/// Cancel. Keyboard and controller friendly (the first choice has focus; Esc or the controller's B
/// cancels). It blocks the screen behind it while open.
/// </summary>
public partial class ConfirmDialog : Control
{
    private Label _message = null!;
    private HBoxContainer _buttons = null!;
    private Action? _cancel;

    public ConfirmDialog()
    {
        Name = "ConfirmDialog";
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
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);
        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) };
        box.AddChild(_message);
        _buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(_buttons);
    }

    /// <summary>Asks <paramref name="question"/>; each choice runs its action and closes the dialog. Cancel is added last.</summary>
    public void Ask(string question, params (string Label, Action Chosen)[] choices)
    {
        _message.Text = question;
        foreach (var child in _buttons.GetChildren()) { _buttons.RemoveChild(child); child.QueueFree(); }
        var all = new List<(string, Action)>(choices) { ("Cancel", () => { }) };
        Button? first = null;
        foreach (var (label, chosen) in all)
        {
            var button = new Button { Text = label, CustomMinimumSize = new Vector2(110, 36) };
            button.Pressed += () => { Visible = false; chosen(); };
            _buttons.AddChild(button);
            first ??= button;
        }
        _cancel = () => Visible = false;
        Visible = true;
        first?.GrabFocus();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || !@event.IsActionPressed("ui_cancel")) return;
        _cancel?.Invoke();
        GetViewport().SetInputAsHandled();
    }
}
