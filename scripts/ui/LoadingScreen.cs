using Godot;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// "Loading…" over everything while a save is read on a worker thread (#67), so a large save doesn't
/// make the window look frozen. It fades in only after a short moment, so quick loads don't flash.
/// </summary>
public partial class LoadingScreen : Control
{
    private const double ShowAfterS = 0.15;

    private readonly Label _label, _tip;
    private readonly TextureRect _portrait;
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
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 16);
        centre.AddChild(box);
        _portrait = new TextureRect // #98
        {
            CustomMinimumSize = new Vector2(256, 320), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            TextureFilter = TextureFilterEnum.LinearWithMipmaps, SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(_portrait);
        _label = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _label.AddThemeFontSizeOverride("font_size", 24);
        box.AddChild(_label);
        _tip = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(520, 0), Modulate = new Color(1, 1, 1, 0.75f) };
        box.AddChild(_tip);
    }

    public void Begin(string what, Texture2D? portrait = null, string tip = "")
    {
        _portrait.Texture = portrait;
        _portrait.Visible = portrait is not null;
        _tip.Text = tip;
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
