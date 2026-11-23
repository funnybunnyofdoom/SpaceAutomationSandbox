using System;
using Godot;

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
    public event Action? QuitToMenuChosen;
    public event Action? QuitToDesktopChosen;

    private Button _resume = null!;
    private Label _about = null!;

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
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        centre.AddChild(box);
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
        Item(box, "Quit to main menu", () => QuitToMenuChosen?.Invoke());
        Item(box, "Quit to desktop", () => QuitToDesktopChosen?.Invoke());
    }

    private static Button Item(VBoxContainer box, string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(280, 40) };
        button.AddThemeFontSizeOverride("font_size", 18);
        button.Pressed += pressed;
        box.AddChild(button);
        return button;
    }

    /// <summary>Shows the menu; <paramref name="about"/> is the game's name, seed and play time.</summary>
    public void Open(string about = "")
    {
        _about.Text = about;
        Visible = true;
        _resume.GrabFocus();
    }
}
