using System;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The in-game pause menu (#59): Esc (with no panel open) or the controller's Start opens it, and the
/// game is paused while it shows. Resume, Save, Load, Settings, Help, Quit to main menu, Quit to desktop.
/// Keyboard and controller drive it; Esc again resumes.
/// </summary>
public partial class PauseMenu : Control
{
    public event Action? ResumeChosen;
    public event Action? SaveChosen;
    public event Action? LoadChosen;
    public event Action? SettingsChosen;
    public event Action? HelpChosen;
    public event Action? GoalsToggled;
    public event Action? GoalsRestarted;
    public event Action? QuitToMenuChosen;
    public event Action? QuitToDesktopChosen;

    private TextureRect _portrait = null!;

    /// <summary>The race the game is played as (#98).</summary>
    public Texture2D? Portrait { set => _portrait.Texture = value; }

    private Button _resume = null!;
    private Label _about = null!;
    private Button _goals = null!;
    private PanelContainer _worldPanel = null!;
    private readonly WorldOptionsBox _world = new(beside: false); // #119

    /// <summary>The world's options were changed.</summary>
    public event Action? WorldChanged;

    /// <summary>Dev: shows the world's options.</summary>
    public void ShowWorld()
    {
        _worldPanel.Visible = true;
        _world.Expand();
    }

    public PauseMenu()
    {
        Name = "PauseMenu";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var across = new HBoxContainer();
        across.AddThemeConstantOverride("separation", 28);
        centre.AddChild(across);
        _portrait = new TextureRect // #98
        {
            CustomMinimumSize = new Vector2(288, 360), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            TextureFilter = TextureFilterEnum.LinearWithMipmaps, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore,
        };
        across.AddChild(_portrait);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        across.AddChild(box);
        var title = new Label { Text = "PAUSED", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 32);
        box.AddChild(title);
        _about = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.65f) };
        box.AddChild(_about);
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });
        _resume = Item(box, "Resume", () => ResumeChosen?.Invoke());
        Item(box, "Save", () => SaveChosen?.Invoke());
        Item(box, "Load", () => LoadChosen?.Invoke());
        Item(box, "Settings", () => SettingsChosen?.Invoke());
        Item(box, "Help", () => HelpChosen?.Invoke());
        _goals = Item(box, "Show goal list", () => GoalsToggled?.Invoke());
        Item(box, "Restart goal list", () => GoalsRestarted?.Invoke());
        Item(box, "World options", () => { _worldPanel.Visible = !_worldPanel.Visible; if (_worldPanel.Visible) _world.Expand(); }); // #119
        Item(box, "Quit to main menu", () => QuitToMenuChosen?.Invoke());
        Item(box, "Quit to desktop", () => QuitToDesktopChosen?.Invoke());
        _worldPanel = new PanelContainer { Visible = false, SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(380, 0) };
        _worldPanel.AddChild(_world);
        _world.Changed += () => WorldChanged?.Invoke();
        across.AddChild(_worldPanel);
    }

    private static Button Item(VBoxContainer box, string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(280, 40) };
        button.AddThemeFontSizeOverride("font_size", 18);
        button.Pressed += pressed;
        box.AddChild(button);
        return button;
    }

    /// <summary>Focus back on the first item (after a screen opened from the menu closes).</summary>
    public void FocusFirst() => _resume.GrabFocus();

    /// <summary>Shows the menu; <paramref name="about"/> is the game's name, seed and play time.</summary>
    public void Open(string about = "", bool goalsOn = false, WorldOptions? world = null)
    {
        _world.Set(world ?? new WorldOptions());
        _worldPanel.Visible = false;
        _goals.Text = goalsOn ? "Hide goal list" : "Show goal list";
        _about.Text = about;
        Visible = true;
        _resume.GrabFocus();
    }
}
