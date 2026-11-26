using System;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Title screen and main menu (#58): the game's (working) name over a scene of its own (#98), then Continue
/// (the save, if there is one), New Game, Load, Settings and Quit. Keyboard and controller
/// drive it with the usual UI keys (arrows/D-pad to move, Enter/A to choose); the first available
/// button has focus. Load opens the save list (#60). Credits (#66, #138) lists who made it. The version is in the corner (#136).
/// </summary>
public partial class TitleScreen : Control
{
    public event Action? ContinueChosen;
    public event Action? NewGameChosen;
    public event Action? LoadChosen;
    public event Action? SettingsChosen;
    public event Action? CreditsChosen;
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

        // Dark behind the menu, clear over the scene (#98).
        var gradient = new Gradient();
        gradient.SetColor(0, new Color(0, 0.01f, 0.03f, 0.88f));
        gradient.SetColor(1, new Color(0, 0.01f, 0.03f, 0));
        gradient.SetOffset(0, 0.3f);
        var shade = new TextureRect { Texture = new GradientTexture2D { Gradient = gradient, Width = 256, Height = 4 }, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.LeftWide);
        shade.AnchorRight = 0.72f;
        AddChild(shade);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 72);
        margin.AddThemeConstantOverride("margin_right", 24);
        AddChild(margin);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        box.AddThemeConstantOverride("separation", 10);
        margin.AddChild(box);

        var (name, subtitle) = GameTitle();
        if (Logo() is { } logo)
            box.AddChild(new TextureRect { Texture = logo, CustomMinimumSize = new Vector2(440, 150), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspect, MouseFilter = MouseFilterEnum.Ignore, TooltipText = name });
        else
            box.AddChild(new Label
            {
                Text = name.ToUpperInvariant(), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0),
                LabelSettings = new LabelSettings
                {
                    Font = new FontVariation { BaseFont = ThemeDB.FallbackFont, SpacingGlyph = 5, VariationEmbolden = 0.7f }, FontSize = 46, LineSpacing = -6,
                    OutlineSize = 6, OutlineColor = new Color(0.02f, 0.1f, 0.16f), ShadowSize = 22, ShadowColor = new Color(UiTheme.Cyan, 0.28f), ShadowOffset = Vector2.Zero,
                },
            });
        if (subtitle.Length > 0) box.AddChild(new Label { Text = subtitle, Modulate = new Color(UiTheme.Cyan, 0.85f) });
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 20) });

        _continue = MenuButton("Continue", () => ContinueChosen?.Invoke());
        box.AddChild(_continue);
        _saveInfo = new Label { Modulate = new Color(1, 1, 1, 0.55f) };
        box.AddChild(_saveInfo);
        _newGame = MenuButton("New Game", () => NewGameChosen?.Invoke());
        box.AddChild(_newGame);
        box.AddChild(MenuButton("Load", () => LoadChosen?.Invoke()));
        box.AddChild(MenuButton("Settings", () => SettingsChosen?.Invoke()));
        box.AddChild(MenuButton("Credits", () => CreditsChosen?.Invoke()));
        box.AddChild(MenuButton("Quit", () => QuitChosen?.Invoke()));
        _message = new Label
        {
            Modulate = Palette.Bad,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0),
        };
        box.AddChild(_message);

        var version = new Label { Text = $"{GameInfo.Name} {GameInfo.VersionLabel}", Modulate = new Color(1, 1, 1, 0.45f), MouseFilter = MouseFilterEnum.Ignore };
        version.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight, LayoutPresetMode.KeepSize, 14);
        version.GrowHorizontal = GrowDirection.Begin;
        version.GrowVertical = GrowDirection.Begin;
        AddChild(version);
    }

    /// <summary>The game's name is data, and a logo picture (assets/art/ui/logo.png or .webp) takes its place if there is one (#98).</summary>
    private static (string Name, string Subtitle) GameTitle()
    {
        const string path = "res://data/title.json";
        try
        {
            if (FileAccess.FileExists(path) && System.Text.Json.JsonDocument.Parse(FileAccess.GetFileAsString(path)).RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root)
                return (root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", root.TryGetProperty("subtitle", out var t) ? t.GetString() ?? "" : "");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { GD.PushWarning($"{path}: {e.Message}"); }
        return (GameInfo.Name, "");
    }

    private static Texture2D? Logo()
    {
        foreach (var extension in new[] { "webp", "png" })
            if ($"res://assets/art/ui/logo.{extension}" is var path && ResourceLoader.Exists(path)) return GD.Load<Texture2D>(path);
        return null;
    }

    private static Button MenuButton(string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(320, 42), FocusMode = FocusModeEnum.All, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        button.AddThemeFontSizeOverride("font_size", 20);
        button.Pressed += pressed;
        return button;
    }

    /// <summary>Opens the credits as if chosen (a dev check).</summary>
    public void PickCredits() => CreditsChosen?.Invoke();

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
