using System;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// New-game screen (#62): the game's name (it names its saves) and the world seed, random by default
/// and editable. A number is used as it is; any other text becomes a seed too, so a world can be
/// shared as a word. The same seed always gives the same starting world. Esc goes back.
/// </summary>
public partial class NewGameScreen : Control
{
    /// <summary>Start a game with this name and seed.</summary>
    /// <summary>Start a game with this name and seed; the bool is the goal list for new players (#65); then the starting supplies.</summary>
    public event Action<string, ulong, bool, StartingSupplies>? StartChosen;
    public event Action? BackChosen;

    private LineEdit _name = null!, _seed = null!;
    private Label _seedInfo = null!;
    private CheckBox _goals = null!;
    private OptionButton _supplies = null!;
    private readonly Random _random = new();

    public NewGameScreen()
    {
        Name = "NewGameScreen";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat // solid: the title menu is right behind it
        {
            BgColor = new Color(0.05f, 0.06f, 0.1f, 0.98f),
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 16, ContentMarginBottom = 16,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            BorderColor = new Color(0.3f, 0.4f, 0.6f, 0.6f), BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        });
        centre.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);
        var title = new Label { Text = "NEW GAME" };
        title.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(title);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        box.AddChild(grid);
        grid.AddChild(new Label { Text = "Name" });
        _name = new LineEdit { CustomMinimumSize = new Vector2(340, 0) };
        _name.TextSubmitted += _ => Start();
        grid.AddChild(_name);
        grid.AddChild(new Label { Text = "Seed" });
        var seedRow = new HBoxContainer();
        _seed = new LineEdit { CustomMinimumSize = new Vector2(260, 0) };
        _seed.TextChanged += _ => ShowSeed();
        _seed.TextSubmitted += _ => Start();
        seedRow.AddChild(_seed);
        var random = new Button { Text = "Random" };
        random.Pressed += () => { _seed.Text = RandomSeed().ToString(); ShowSeed(); };
        seedRow.AddChild(random);
        grid.AddChild(seedRow);
        grid.AddChild(new Control());
        _seedInfo = new Label { Modulate = new Color(1, 1, 1, 0.6f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        grid.AddChild(_seedInfo);

        grid.AddChild(new Label { Text = "Food and water" });
        _supplies = new OptionButton { TooltipText = "What the starting shipyard has for its 4 crew" };
        _supplies.AddItem("Lean: about 3 days");
        _supplies.AddItem("Standard: about 17 days");
        _supplies.AddItem("Generous: about 52 days");
        grid.AddChild(_supplies);

        grid.AddChild(new Control());
        _goals = new CheckBox { Text = "Goal list for new players", ButtonPressed = true, TooltipText = "A checklist of the core loop on the left of the screen. Turn it off or restart it from the pause menu." };
        grid.AddChild(_goals);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(110, 36) };
        back.Pressed += Back;
        buttons.AddChild(back);
        var start = new Button { Text = "Start", CustomMinimumSize = new Vector2(110, 36) };
        start.Pressed += Start;
        buttons.AddChild(start);
    }

    private ulong RandomSeed() => (ulong)_random.NextInt64(1, 1_000_000_000);

    public void Open()
    {
        _name.Text = "New game";
        _supplies.Select((int)StartingSupplies.Standard);
        _seed.Text = RandomSeed().ToString();
        ShowSeed();
        Visible = true;
        _name.GrabFocus();
        _name.SelectAll();
    }

    private void ShowSeed()
    {
        var text = _seed.Text.Trim();
        _seedInfo.Text = text.Length == 0 ? "An empty seed picks a random one."
            : ulong.TryParse(text, out _) ? "The same seed always gives the same world. Share it to share the world."
            : $"Text works too: \"{text}\" is seed {NewGame.SeedFromText(text)}.";
    }

    private void Start()
    {
        var name = _name.Text.Trim();
        var seedText = _seed.Text.Trim();
        var seed = seedText.Length == 0 ? RandomSeed() : NewGame.SeedFromText(seedText);
        Visible = false;
        StartChosen?.Invoke(name.Length > 0 ? name : "New game", seed, _goals.ButtonPressed, (StartingSupplies)_supplies.Selected);
    }

    private void Back()
    {
        Visible = false;
        BackChosen?.Invoke();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || !@event.IsActionPressed("ui_cancel")) return;
        Back();
        GetViewport().SetInputAsHandled();
    }
}
