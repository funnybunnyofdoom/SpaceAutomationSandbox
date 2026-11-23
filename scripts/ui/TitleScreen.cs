using System;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Title screen and main menu (#58): the game's (working) name over the starfield, then Continue
/// (the save, if there is one), New Game, Load, Settings and Quit. Keyboard and controller
/// drive it with the usual UI keys (arrows/D-pad to move, Enter/A to choose); the first available
/// button has focus. Load opens the save list (#60). No credits screen for now (#66).
/// </summary>
public partial class TitleScreen : Control
{
    public event Action? ContinueChosen;
    public event Action? NewGameChosen;
    public event Action? LoadChosen;
    public event Action? SettingsChosen;
    public event Action? QuitChosen;

    private Button _continue = null!;
    private Button _newGame = null!;
    private Label _saveInfo = null!;
    private Label _message = null!;

    public TitleScreen()
    {
        Build();
    }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.35f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 10);
        centre.AddChild(box);

        var title = new Label { Text = "SpaceAutomationSandbox", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 48);
        box.AddChild(title);
        box.AddChild(new Label
        {
            Text = "working title",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.6f),
        });
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

        _continue = MenuButton("Continue", () => ContinueChosen?.Invoke());
        box.AddChild(_continue);
        _saveInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.55f) };
        box.AddChild(_saveInfo);
        _newGame = MenuButton("New Game", () => NewGameChosen?.Invoke());
        box.AddChild(_newGame);
        box.AddChild(MenuButton("Load", () => LoadChosen?.Invoke()));
        box.AddChild(MenuButton("Settings", () => SettingsChosen?.Invoke()));
        box.AddChild(MenuButton("Quit", () => QuitChosen?.Invoke()));
        _message = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center, Modulate = Palette.Bad,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0),
        };
        box.AddChild(_message);
    }

    private static Button MenuButton(string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(280, 40), FocusMode = FocusModeEnum.All };
        button.AddThemeFontSizeOverride("font_size", 20);
        button.Pressed += pressed;
        return button;
    }

    /// <summary>Shows the menu. <paramref name="saveSummary"/> describes the save Continue loads, or null if there's none.</summary>
    public void Open(string? saveSummary, string? message = null)
    {
        _message.Text = message ?? "";
        _message.Visible = !string.IsNullOrEmpty(message);
        _continue.Disabled = saveSummary is null;
        _saveInfo.Text = saveSummary ?? "";
        _saveInfo.Visible = saveSummary is not null;
        Visible = true;
        (saveSummary is null ? _newGame : _continue).GrabFocus(); // keyboard and controller start on the likely choice
    }
}
