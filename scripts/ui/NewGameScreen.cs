using System;
using Godot;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
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
    public event Action<string, ulong, bool, StartingSupplies, RaceDefinition, WorldOptions>? StartChosen;
    public event Action? BackChosen;

    /// <summary>Make a race from this one, or change it if it's the player's (#117).</summary>
    public event Action<RaceDefinition>? CreatorChosen;

    private LineEdit _name = null!, _seed = null!;
    private Label _seedInfo = null!;
    private CheckBox _goals = null!;
    private OptionButton _supplies = null!;
    private readonly WorldOptionsBox _world = new(); // #119
    private WorldOptions _options = WorldOptions.Usual();
    private readonly Random _random = new();
    private readonly Func<RaceCatalog> _races;
    private readonly Func<RaceDefinition, IReadOnlyList<string>> _describe;
    private readonly Func<RaceDefinition, bool> _unbalanced;
    private HBoxContainer _raceRow = null!;
    private ScrollContainer _raceScroll = null!, _infoScroll = null!;
    private Label _raceInfo = null!, _suppliesTitle = null!;
    private readonly Dictionary<RaceDefinition, Button> _raceButtons = new();
    private Label _raceProblems = null!;
    private Button _creator = null!;
    private RaceDefinition _race = RaceCatalog.HumanOnly.Default;

    public NewGameScreen() : this(() => RaceCatalog.HumanOnly, _ => Array.Empty<string>(), _ => false) { }

    public NewGameScreen(Func<RaceCatalog> races, Func<RaceDefinition, IReadOnlyList<string>> describe, Func<RaceDefinition, bool> unbalanced)
    {
        _races = races;
        _describe = describe;
        _unbalanced = unbalanced;
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

        grid.AddChild(new Label { Text = "Race", SizeFlagsVertical = SizeFlags.ShrinkBegin }); // #97
        _raceRow = new HBoxContainer();
        _raceRow.AddThemeConstantOverride("separation", 8);
        var raceBox = new HBoxContainer();
        raceBox.AddThemeConstantOverride("separation", 14);
        _raceScroll = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _raceScroll.AddChild(_raceRow); // more races than fit (#116)
        raceBox.AddChild(_raceScroll);
        _raceInfo = new Label { ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320, 0), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        var besideRaces = new VBoxContainer();
        _infoScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(336, 0) };
        _infoScroll.AddChild(_raceInfo);
        besideRaces.AddChild(_infoScroll);
        _creator = new Button { TooltipText = "Opens the race creator" };
        _creator.Pressed += () => { Visible = false; CreatorChosen?.Invoke(_race); };
        besideRaces.AddChild(_creator);
        raceBox.AddChild(besideRaces); // beside the portraits, so that it fits a small window
        grid.AddChild(raceBox);
        _raceProblems = new Label { ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(460, 0) };

        grid.AddChild(_suppliesTitle = new Label { Text = "Food and water" });
        _supplies = new OptionButton { TooltipText = "What the starting shipyard has for its 4 crew" };
        _supplies.AddItem("Lean: about 3 days");
        _supplies.AddItem("Standard: about 17 days");
        _supplies.AddItem("Generous: about 52 days");
        grid.AddChild(_supplies);

        grid.AddChild(new Control());
        _goals = new CheckBox { Text = "Goal list for new players", ButtonPressed = true, TooltipText = "A checklist of the core loop on the left of the screen. Turn it off or restart it from the pause menu." };
        grid.AddChild(_goals);

        box.AddChild(_world);
        box.AddChild(_raceProblems); // not in the grid: hidden, it would shift it
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
        _world.Set(_options = WorldOptions.Usual());
        _seed.Text = RandomSeed().ToString();
        ShowSeed();
        ListRaces();
        Visible = true;
        _name.GrabFocus();
        _name.SelectAll();
    }

    /// <summary>Dev: opens the advanced options.</summary>
    public void ShowAdvanced() => _world.Expand();

    /// <summary>Back from the race creator: what was typed stays, and the race made there is chosen.</summary>
    public void Return(string? raceId)
    {
        ListRaces();
        if (_raceButtons.Keys.FirstOrDefault(r => r.Id == raceId) is { } race) ChooseRace(race);
        Visible = true;
        _raceButtons[_race].GrabFocus();
        _raceScroll.EnsureControlVisible(_raceButtons[_race]);
    }

    private void ListRaces()
    {
        foreach (var child in _raceRow.GetChildren()) { _raceRow.RemoveChild(child); child.QueueFree(); }
        _raceButtons.Clear();
        var group = new ButtonGroup();
        foreach (var race in _races().Races)
        {
            var button = new Button
            {
                Text = race.Name + (_unbalanced(race) ? " (unbalanced)" : ""), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = race.Name, ToggleMode = true, ButtonGroup = group, SizeFlagsVertical = SizeFlags.ShrinkBegin, Icon = RaceLibrary.Portrait(race), ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top, CustomMinimumSize = new Vector2(172, 240), TextureFilter = TextureFilterEnum.LinearWithMipmaps, // painted, not pixel art
            };
            button.AddThemeConstantOverride("icon_max_width", 160);
            button.Pressed += () => ChooseRace(race);
            _raceRow.AddChild(button);
            _raceButtons[race] = button;
        }
        _raceScroll.CustomMinimumSize = new Vector2(Math.Min(_raceButtons.Count, 4) * 180 - 8, 0); // four at once
        var problems = _races().Problems;
        _raceProblems.Visible = problems.Count > 0;
        _raceProblems.Text = string.Join("\n", problems.Select(p => $"Race not usable: {p}"));
        _raceProblems.AddThemeColorOverride("font_color", UiTheme.Of(Severity.Warning));
        ChooseRace(_races().Default);
    }

    private void ChooseRace(RaceDefinition race)
    {
        _race = race;
        _raceButtons[race].SetPressedNoSignal(true);
        _creator.Text = race.IsCustom ? $"Change {race.Name}..." : $"Make a race from {race.Name}...";
        _raceInfo.Text = race.Description + string.Concat(_describe(race).Select(e => $"\n•  {e}"));
        _supplies.Visible = _suppliesTitle.Visible = race.Supplies;
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
        StartChosen?.Invoke(name.Length > 0 ? name : "New game", seed, _goals.ButtonPressed, (StartingSupplies)_supplies.Selected, _race, _options);
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
