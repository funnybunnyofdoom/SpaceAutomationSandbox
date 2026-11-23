using Godot;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// "Loading…" over everything while a save is read on a worker thread (#67), so a large save doesn't
/// make the window look frozen. It fades in only after a short moment, so quick loads don't flash.
/// </summary>
public partial class LoadingScreen : Control
{
    private const double ShowAfterS = 0.15;

    private readonly Label _label;
    private double _elapsed;
    private string _what = "";

    public LoadingScreen()
    {
        Name = "LoadingScreen";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop; // no clicks reach the game or menus underneath
        Visible = false;
        var shade = new ColorRect { Color = new Color(0.02f, 0.02f, 0.06f, 0.92f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        _label = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _label.AddThemeFontSizeOverride("font_size", 24);
        centre.AddChild(_label);
    }

    public void Begin(string what)
    {
        _what = what;
        _elapsed = 0;
        Modulate = Colors.Transparent;
        Visible = true;
        GetParent()?.MoveChild(this, GetParent().GetChildCount() - 1);
        Update();
    }

    public void End() => Visible = false;

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _elapsed += delta;
        Update();
    }

    private void Update()
    {
        Modulate = _elapsed >= ShowAfterS ? Colors.White : Colors.Transparent;
        var dots = new string('.', 1 + (int)(_elapsed * 3) % 3);
        _label.Text = $"Loading {_what}{dots}\n{_elapsed:0.0} s";
    }
}
