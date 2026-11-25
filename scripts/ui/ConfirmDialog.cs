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
    private LineEdit _entry = null!;
    private Label _problem = null!;
    private Func<string, string?>? _blocker;
    private Action<string>? _entered;
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
        _entry = new LineEdit { SelectAllOnFocus = true };
        _entry.TextChanged += _ => _problem.Text = Problem() ?? "";
        _entry.TextSubmitted += _ => Enter();
        _entry.GuiInput += e => // the box would keep Esc to itself
        {
            if (!e.IsActionPressed("ui_cancel")) return;
            _cancel?.Invoke();
            _entry.AcceptEvent();
        };
        box.AddChild(_entry);
        _problem = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_problem);
        _buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(_buttons);
    }

    /// <summary>Asks <paramref name="question"/>; each choice runs its action and closes the dialog. Cancel is added last.</summary>
    public void Ask(string question, params (string Label, Action Chosen)[] choices)
    {
        _message.Text = question;
        _entry.Visible = _problem.Visible = false;
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

    /// <summary>Asks for a line of text (#115); <paramref name="blocker"/> says why it can't be used, or null.</summary>
    public void AskText(string question, string text, int maxLength, string accept, Func<string, string?> blocker, Action<string> entered)
    {
        (_blocker, _entered) = (blocker, entered);
        Ask(question, (accept, Enter));
        _problem.Text = "";
        _problem.AddThemeColorOverride("font_color", UiTheme.Of(Severity.Warning));
        _entry.MaxLength = maxLength;
        _entry.Text = text;
        _entry.Visible = _problem.Visible = true;
        Edit();
    }

    private void Edit()
    {
        _entry.GrabFocus();
        _entry.Edit();
        _entry.SelectAll();
    }

    private string? Problem() => _blocker?.Invoke(_entry.Text);

    private void Enter()
    {
        if (Problem() is { } why)
        {
            Visible = true; // the button has closed it
            _problem.Text = why;
            Edit();
            return;
        }
        Visible = false;
        _entered?.Invoke(_entry.Text);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || !@event.IsActionPressed("ui_cancel")) return;
        _cancel?.Invoke();
        GetViewport().SetInputAsHandled();
    }
}
