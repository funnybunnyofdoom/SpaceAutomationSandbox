using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Makes and changes the player's own races (#117). Points show what it costs as it's chosen; a game started with it keeps its own copy.</summary>
public partial class RaceCreatorScreen : Control
{
    private readonly Func<PartCatalog?> _catalog;
    private readonly Action<string, (string, Action)[]> _ask;

    private RaceDefinition _race = new();
    private RaceDefinition? _saved; // as it was opened or last saved; null for a new one
    private Image? _picture; // chosen, not saved yet
    private string? _pictureFrom; // the race it was copied from, for its picture
    private string _asOpened = "";
    private bool _filling;

    private OptionButton _from = null!, _portrait = null!;
    private LineEdit _name = null!, _description = null!, _shipName = null!, _yardName = null!;
    private ColorPickerButton _accent = null!;
    private SpinBox _crew = null!;
    private CheckBox _robots = null!, _supplies = null!, _civilization = null!;
    private readonly Dictionary<string, CheckBox> _cultures = new();
    private readonly Dictionary<string, (HSlider Slider, Label Value)> _traits = new(), _stock = new(), _wants = new(), _opposes = new();
    private readonly Dictionary<string, CheckBox> _tech = new(), _blocked = new(), _favours = new();
    private readonly Dictionary<string, (HSlider Slider, Label Value)> _civTraits = new();
    private OptionButton _startShip = null!, _startYard = null!;
    private List<ShipDesign?> _ships = new(), _yards = new();
    private VBoxContainer _fields = null!;
    private TextureRect _card = null!;
    private Label _cardName = null!, _points = null!, _effects = null!, _status = null!;
    private InfoList _costs = null!;
    private Button _delete = null!, _export = null!, _save = null!;
    private FileDialog? _files;
    private Action<string>? _picked;
    private bool _built;

    /// <summary>Back to the New Game screen, with the race to choose there (its id), if any.</summary>
    public event Action<string?>? Closed;

    /// <summary>The races on disk changed.</summary>
    public event Action? Changed;

    public RaceCreatorScreen(Func<PartCatalog?> catalog, Action<string, (string, Action)[]> ask)
    {
        (_catalog, _ask) = (catalog, ask);
        Name = "RaceCreatorScreen";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
    }

    public RaceCreatorScreen() : this(() => null, (_, _) => { }) { }

    private void Build(PartCatalog catalog)
    {
        _built = true;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 20);
        AddChild(margin);
        var panel = new PanelContainer();
        margin.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);
        var head = new HBoxContainer();
        box.AddChild(head);
        var title = new Label { Text = "RACE CREATOR", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 22);
        head.AddChild(title);
        head.AddChild(new Label { Text = "Start from a copy of" });
        _from = new OptionButton();
        _from.ItemSelected += i => { if (_catalog() is { } c && i < c.Races.Races.Count) Replace(() => CopyOf(c.Races.Races[(int)i])); };
        head.AddChild(_from);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 18);
        box.AddChild(body);

        // --- What can be changed ---
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        body.AddChild(scroll);
        _fields = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_fields);
        Section("Who they are");
        var who = Grid(2);
        _name = Text(who, "Name", RaceCatalog.MaxName, t => _race with { Name = t });
        _description = Text(who, "Description", RaceCatalog.MaxDescription, t => _race with { Description = t });
        who.AddChild(new Label { Text = "Picture" });
        var pictureRow = new HBoxContainer();
        who.AddChild(pictureRow);
        _portrait = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _portrait.ItemSelected += i => ChoosePortrait(_portrait.GetItemMetadata((int)i).AsString());
        pictureRow.AddChild(_portrait);
        pictureRow.AddChild(Press("From a file...", "A PNG, JPG or WebP picture. It's cropped to 4:5 and copied, so the original can move.", () => Pick(FileDialog.FileModeEnum.OpenFile, "*.png,*.jpg,*.jpeg,*.webp;Pictures", "", TakePicture)));
        who.AddChild(new Label { Text = "Colour" });
        _accent = new ColorPickerButton { CustomMinimumSize = new Vector2(0, 28), EditAlpha = false };
        _accent.ColorChanged += c => Set(_race with { Accent = "#" + c.ToHtml(false) });
        who.AddChild(_accent);
        _shipName = Text(who, "First ship's name", RaceCatalog.MaxName, t => _race with { ShipName = t });
        _yardName = Text(who, "Shipyard's name", RaceCatalog.MaxName, t => _race with { ShipyardName = t });

        Section("Traits");
        var traits = Grid(3);
        foreach (var trait in RaceTraits.All)
            _traits[trait.Id] = Slide(traits, trait.Name, RaceTraits.Limit, 5, v => _race with { Traits = With(_race.Traits, trait.Id, (int)v, 0) });

        Section("At the start");
        var start = Grid(2);
        start.AddChild(new Label { Text = "Crew" });
        _crew = new SpinBox { MinValue = 0, MaxValue = RaceFiles.MaxCrew, Step = 1 };
        _crew.ValueChanged += v => Set(_race with { Crew = (int)v });
        start.AddChild(_crew);
        _supplies = Check(start, "Food and water", on => _race with { Supplies = on });
        _robots = Check(start, "Robot bay instead of the habitat", on => _race with { ShipyardParts = on ? new() { ["habitat"] = "robot_bay" } : new(), Crew = on ? 0 : Math.Max(2, _race.Crew), Supplies = !on || _race.Supplies });
        start.AddChild(new Label { Text = "Starting ship" }); // #125
        _startShip = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "One of your ship designs, or the usual" };
        _startShip.ItemSelected += i => Set(Starting(ship: true, (int)i));
        start.AddChild(_startShip);
        start.AddChild(new Label { Text = "Starting shipyard" });
        _startYard = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "One of your station designs with a fabricator and a docking port, or the usual" };
        _startYard.ItemSelected += i => { Set(Starting(ship: false, (int)i)); Fill(); };
        start.AddChild(_startYard);
        var stock = Grid(3);
        foreach (var material in NewGame.StartingStockKg.Keys)
            _stock[material] = Slide(stock, $"More or less {catalog.Materials.Get(material).Name.ToLowerInvariant()}, t", RaceFiles.MaxStockKg / 1000, 1, v => _race with { StockKg = With(_race.StockKg, material, (long)v * 1000, 0) });

        Section("Known technology");
        var techs = Grid(2);
        foreach (var tech in catalog.Research.Techs)
        {
            var check = new CheckBox { Text = $"{tech.Name}  ({RacePoints.Tech(tech):0})", TooltipText = tech.Description, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            check.Toggled += on => Set(_race with { Tech = catalog.Research.Techs.Select(t => t.Id).Where(id => id == tech.Id ? on : _race.Tech.Contains(id)).ToList() });
            techs.AddChild(check);
            _tech[tech.Id] = check;
        }

        Section("How civilizations see them");
        foreach (var law in Laws.All)
        {
            _fields.AddChild(new Label { Text = law.Name, TooltipText = law.Effect, MouseFilter = MouseFilterEnum.Pass });
            var views = Grid(3);
            _wants[law.Id] = Slide(views, "   Those that want it", RaceFiles.MaxLeaning, 5, v => Lean(law.Id, l => l with { Wants = v }));
            _opposes[law.Id] = Slide(views, "   Those against it", RaceFiles.MaxLeaning, 5, v => Lean(law.Id, l => l with { Opposes = v }));
            var blocked = new CheckBox { Text = "They can't pass this law" };
            blocked.Toggled += on => Set(_race with { BlockedLaws = With(_race.BlockedLaws, law.Id, on ? "Your people won't have it." : "", "") });
            _fields.AddChild(blocked);
            _blocked[law.Id] = blocked;
        }

        Section("As a civilization you can meet");
        _civilization = new CheckBox { Text = "Civilizations in your new games can be of this race" };
        _civilization.Toggled += on => { Set(_race with { Civilization = on }); Fill(); };
        _fields.AddChild(_civilization);
        var cultures = Grid(3);
        foreach (var culture in Culture.All)
        {
            var check = new CheckBox { Text = culture.Name, TooltipText = "Such a civilization may have this culture. None ticked: any." };
            check.Toggled += on => Set(_race with { Cultures = Culture.All.Select(c => c.Id).Where(id => id == culture.Id ? on : _race.Cultures.Contains(id)).ToList() });
            cultures.AddChild(check);
            _cultures[culture.Id] = check;
        }

        _fields.AddChild(new Label { Text = "How its civilizations behave (costs no points: it's about others)", ThemeTypeVariation = "HudText", Modulate = new Color(1, 1, 1, 0.7f) });
        var civ = Grid(3);
        foreach (var trait in CivilizationTraits.All)
            _civTraits[trait.Id] = Slide(civ, trait.Name, RaceTraits.Limit, 5, v => _race with { CivTraits = With(_race.CivTraits, trait.Id, (int)v, 0) });
        _fields.AddChild(new Label { Text = $"Goods they pay {(CivilizationTraits.FavourFactor - 1) * 100:0}% more for (up to {CivilizationTraits.MaxFavours})" });
        var favours = Grid(4);
        foreach (var material in catalog.Materials.Materials.Where(m => m.Value >= 1))
        {
            var check = new CheckBox { Text = material.Name, Icon = UiIcons.Material(material.Id) };
            check.Toggled += on =>
            {
                if (on && _race.Favours.Count >= CivilizationTraits.MaxFavours) { check.SetPressedNoSignal(false); Say($"Up to {CivilizationTraits.MaxFavours} goods.", Severity.Warning); return; }
                Set(_race with { Favours = catalog.Materials.Materials.Select(m => m.Id).Where(id => id == material.Id ? on : _race.Favours.Contains(id)).ToList() });
            };
            favours.AddChild(check);
            _favours[material.Id] = check;
        }

        // --- What it comes to ---
        var rightScroll = new ScrollContainer { CustomMinimumSize = new Vector2(390, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(rightScroll);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rightScroll.AddChild(right);
        var card = new HBoxContainer();
        card.AddThemeConstantOverride("separation", 10);
        right.AddChild(card);
        _card = new TextureRect { CustomMinimumSize = new Vector2(144, 180), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, TextureFilter = TextureFilterEnum.LinearWithMipmaps };
        card.AddChild(_card);
        var beside = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.AddChild(beside);
        _cardName = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _cardName.AddThemeFontSizeOverride("font_size", 20);
        beside.AddChild(_cardName);
        _points = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        beside.AddChild(_points);
        right.AddChild(UiTheme.Header("Points"));
        right.AddChild(_costs = new InfoList());
        right.AddChild(UiTheme.Header("On the New Game screen"));
        _effects = new Label { ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        right.AddChild(_effects);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_status);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);
        buttons.AddChild(Press("Import...", "Takes in a race someone shared", () => Pick(FileDialog.FileModeEnum.OpenFile, "*.json;Race file", "", Import)));
        buttons.AddChild(_export = Press("Export...", "Writes this race, as last saved, to one file to share", () => Pick(FileDialog.FileModeEnum.SaveFile, "*.json;Race file", $"{_race.Id}.json", Export)));
        buttons.AddChild(_delete = Press("Delete", "Deletes this race for good. Games started with it keep their copy.", Delete));
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(Press("Back", "", Back));
        buttons.AddChild(_save = Press("Save", "", Save));
    }

    private void Section(string text) => _fields.AddChild(UiTheme.Header(text));

    private GridContainer Grid(int columns)
    {
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", 12);
        _fields.AddChild(grid);
        return grid;
    }

    private static Button Press(string text, string tip, Action press)
    {
        var button = new Button { Text = text, TooltipText = tip, CustomMinimumSize = new Vector2(96, 34) };
        button.Pressed += press;
        return button;
    }

    private LineEdit Text(Container grid, string label, int length, Func<string, RaceDefinition> change)
    {
        grid.AddChild(new Label { Text = label });
        var edit = new LineEdit { MaxLength = length, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        edit.TextChanged += t => Set(change(t));
        grid.AddChild(edit);
        return edit;
    }

    private CheckBox Check(Container grid, string label, Func<bool, RaceDefinition> change)
    {
        grid.AddChild(new Label { Text = label });
        var check = new CheckBox();
        check.Toggled += on => { Set(change(on)); Fill(); };
        grid.AddChild(check);
        return check;
    }

    private (HSlider, Label) Slide(Container grid, string label, double limit, double step, Func<double, RaceDefinition> change)
    {
        grid.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(230, 0) });
        var slider = new HSlider { MinValue = -limit, MaxValue = limit, Step = step, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(150, 24), TickCount = 3, TicksOnBorders = true, Scrollable = false };
        var value = new Label { CustomMinimumSize = new Vector2(60, 0), HorizontalAlignment = HorizontalAlignment.Right };
        slider.ValueChanged += v => { value.Text = $"{v:+0;-0;0}"; Set(change(v)); };
        grid.AddChild(slider);
        grid.AddChild(value);
        return (slider, value);
    }

    private static Dictionary<string, T> With<T>(Dictionary<string, T> all, string key, T value, T none) where T : notnull
    {
        var next = new Dictionary<string, T>(all);
        if (value.Equals(none)) next.Remove(key); else next[key] = value;
        return next;
    }

    private RaceDefinition Lean(string law, Func<RaceLeaning, RaceLeaning> change)
    {
        var next = change(_race.Leanings.GetValueOrDefault(law) ?? new RaceLeaning());
        return _race with { Leanings = With(_race.Leanings, law, next, new RaceLeaning()) };
    }

    // --- Opening ---

    /// <summary>Opens on a copy of a race (a new one), or on one of the player's own to change it.</summary>
    public void Open(RaceDefinition race)
    {
        if (_catalog() is not { } catalog) return;
        if (!_built) Build(catalog);
        ListRaces(catalog, race);
        if (race.IsCustom) Edit(race);
        else CopyOf(race);
        Visible = true;
        _name.GrabFocus();
        _name.SelectAll();
    }

    private void ListRaces(PartCatalog catalog, RaceDefinition current)
    {
        _from.Clear();
        _portrait.Clear();
        Portrait("Plain, in its colour", "plain");
        Portrait("Its own picture", "");
        foreach (var race in catalog.Races.Races)
        {
            _from.AddItem(race.Name);
            if (race.Id == current.Id) _from.Select(_from.ItemCount - 1);
            if (!race.IsCustom) Portrait($"{race.Name}'s", race.Id);
        }
        void Portrait(string text, string id)
        {
            _portrait.AddItem(text);
            _portrait.SetItemMetadata(_portrait.ItemCount - 1, id);
        }
    }

    private void Edit(RaceDefinition race)
    {
        (_race, _saved, _picture, _pictureFrom) = (race, race, null, null);
        _asOpened = RaceFiles.ToJson(_race);
        Say("");
        Fill();
    }

    private void CopyOf(RaceDefinition race)
    {
        // A new race: notes and designs are the old one's own.
        _race = race with
        {
            Id = RaceFiles.CustomPrefix + "new", Name = $"{race.Name} copy", Notes = new(), GoalHints = new(), Order = 100, Civilization = false, CivilizationNames = new(), Ship = null, Shipyard = null, ShipDesign = null, ShipyardDesign = null, Patrol = null, Raider = null, TradeStation = null, SchemaVersion = RaceCatalog.SchemaVersion,
            Picture = race.IsCustom ? race.Picture : race.Id,
        };
        (_saved, _picture) = (null, null);
        _pictureFrom = race.IsCustom && race.Picture.Length == 0 && RaceLibrary.HasPicture(race.Id) ? race.Id : null;
        _asOpened = RaceFiles.ToJson(_race);
        Say("");
        Fill();
    }

    /// <summary>Shows the race in the fields.</summary>
    private void Fill()
    {
        if (_filling) return;
        _filling = true;
        if (_name.Text != _race.Name) _name.Text = _race.Name;
        if (_description.Text != _race.Description) _description.Text = _race.Description;
        if (_shipName.Text != _race.ShipName) _shipName.Text = _race.ShipName;
        if (_yardName.Text != _race.ShipyardName) _yardName.Text = _race.ShipyardName;
        _accent.Color = Color.HtmlIsValid(_race.Accent) && _race.Accent.Length > 0 ? new Color(_race.Accent) : UiTheme.Cyan;
        _crew.Value = _race.Crew;
        _robots.ButtonPressed = _race.ShipyardParts.ContainsKey("habitat");
        _supplies.ButtonPressed = _race.Supplies;
        _civilization.ButtonPressed = _race.Civilization;
        foreach (var (id, check) in _cultures) { check.ButtonPressed = _race.Cultures.Contains(id); check.Disabled = !_race.Civilization; }
        foreach (var (id, row) in _civTraits) Put(row, _race.CivTraits.GetValueOrDefault(id));
        foreach (var (id, check) in _favours) check.ButtonPressed = _race.Favours.Contains(id);
        ListDesigns();
        _robots.Disabled = _race.Shipyard is not null;
        foreach (var (id, row) in _traits) Put(row, _race.Traits.GetValueOrDefault(id));
        foreach (var (id, row) in _stock) Put(row, _race.StockKg.GetValueOrDefault(id) / 1000.0);
        foreach (var (id, row) in _wants) Put(row, _race.Leanings.GetValueOrDefault(id)?.Wants ?? 0);
        foreach (var (id, row) in _opposes) Put(row, _race.Leanings.GetValueOrDefault(id)?.Opposes ?? 0);
        foreach (var (id, check) in _tech) check.ButtonPressed = _race.Tech.Contains(id);
        foreach (var (id, check) in _blocked) check.ButtonPressed = _race.BlockedLaws.ContainsKey(id);
        for (var i = 0; i < _portrait.ItemCount; i++)
            if (_portrait.GetItemMetadata(i).AsString() == _race.Picture) _portrait.Select(i);
        _filling = false;
        Refresh();

        static void Put((HSlider Slider, Label Value) row, double value)
        {
            row.Slider.SetValueNoSignal(value);
            row.Value.Text = $"{value:+0;-0;0}";
        }
    }

    /// <summary>Your designs that could start a game (#125): ships that fly, stations with a fabricator and a docking port.</summary>
    private void ListDesigns()
    {
        if (_catalog() is not { } catalog) return;
        var all = new List<ShipDesign>();
        foreach (var entry in BlueprintLibrary.List())
            try { all.Add(BlueprintLibrary.Load(entry)); } catch (Exception) { /* unreadable: the editor says so */ }
        _ships = all.Where(d => d.Validate(catalog, DesignRole.Ship).Count == 0).Prepend(null).ToList();
        _yards = all.Where(d => d.Validate(catalog, DesignRole.Station).Count == 0 && d.ComputeStats(catalog) is { FabricationRateKgPerS: > 0, DockingPorts.Count: > 0 }).Prepend(null).ToList();
        Put(_startShip, _ships, _race.Ship, "The usual starter ship");
        Put(_startYard, _yards, _race.Shipyard, "The usual starter shipyard");

        static void Put(OptionButton options, List<ShipDesign?> designs, ShipDesign? chosen, string usual)
        {
            options.Clear();
            foreach (var design in designs) options.AddItem(design?.Name ?? usual);
            var at = chosen is null ? 0 : designs.FindIndex(d => d?.Id == chosen.Id);
            if (at < 0) { designs.Add(chosen); options.AddItem(chosen!.Name); at = designs.Count - 1; } // one no longer in the library
            options.Select(at);
        }
    }

    private RaceDefinition Starting(bool ship, int index)
    {
        var list = ship ? _ships : _yards;
        var design = index >= 0 && index < list.Count ? list[index] : null;
        var json = design?.ToJson();
        return ship ? _race with { Ship = design, ShipDesign = json }
            : _race with { Shipyard = design, ShipyardDesign = json, ShipyardParts = design is null ? _race.ShipyardParts : new() };
    }

    private void Set(RaceDefinition race)
    {
        if (_filling) return;
        _race = race;
        Say("");
        Refresh();
    }

    private bool IsDirty => _picture is not null || RaceFiles.ToJson(_race) != _asOpened;

    private void Refresh()
    {
        if (_catalog() is not { } catalog) return;
        var total = RacePoints.Total(_race, catalog);
        var over = total > RacePoints.Budget;
        _card.Texture = RaceLibrary.Portrait(_race with { Id = _pictureFrom ?? _race.Id }, _race.Picture.Length == 0 ? _picture : null);
        _cardName.Text = _race.Name + (over ? "  (unbalanced)" : "");
        _points.Text = $"{total:0.##} of {RacePoints.Budget:0} points" + (over ? $": {total - RacePoints.Budget:0.##} over. It can be played, and is marked unbalanced." : ".");
        _points.AddThemeColorOverride("font_color", UiTheme.Of(over ? Severity.Warning : Severity.Good));
        _costs.Set(RacePoints.Items(_race, catalog).OrderByDescending(i => i.Points).Select(i => new HudRow(i.Points > 0 ? "ok" : "warn", $"{i.Points:+0.##;-0.##}  {i.What}", i.Points > 0 ? Severity.Normal : Severity.Muted)).ToList());
        _effects.Text = _race.Description + string.Concat(SectorState.Describe(_race, catalog).Select(e => $"\n•  {e}"));
        _delete.Disabled = _export.Disabled = _saved is null;
        var problem = RaceFiles.Problem(_race);
        _save.Disabled = problem is not null;
        _save.TooltipText = problem ?? "";
        if (problem is not null) Say(problem, Severity.Warning);
    }

    private void Say(string text, Severity severity = Severity.Muted)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", UiTheme.Of(severity));
    }

    // --- Pictures ---

    private void ChoosePortrait(string id)
    {
        if (id.Length == 0 && _picture is null && _pictureFrom is null && !(_saved is { } s && RaceLibrary.HasPicture(s.Id)))
        {
            Fill(); // back to what it was
            Say("Choose a picture from a file first.", Severity.Warning);
            return;
        }
        Set(_race with { Picture = id });
    }

    private void TakePicture(string path)
    {
        if (RaceLibrary.ReadPicture(path, out var problem) is not { } image) { Say(problem, Severity.Bad); return; }
        _picture = image;
        _race = _race with { Picture = "" };
        Fill();
        Say($"Picture taken in: {image.GetWidth()} x {image.GetHeight()}.", Severity.Good);
    }

    // --- Files ---

    private void Pick(FileDialog.FileModeEnum mode, string filter, string name, Action<string> picked)
    {
        if (_files is null)
        {
            _files = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true };
            _files.FileSelected += path => _picked?.Invoke(path);
            AddChild(_files);
        }
        _picked = picked;
        _files.FileMode = mode;
        _files.ClearFilters();
        var parts = filter.Split(';');
        _files.AddFilter(parts[0].Replace(",", ", "), parts[1]);
        _files.CurrentFile = name;
        _files.PopupCentered(new Vector2I(800, 560));
    }

    private void Save()
    {
        if (RaceFiles.Problem(_race) is { } problem) { Say(problem, Severity.Warning); return; }
        Try(() =>
        {
            var race = _race with { Name = _race.Name.Trim(), Description = _race.Description.Trim() };
            if (_saved is null) race = race with { Id = RaceFiles.NewId(race.Name, RaceLibrary.Exists) };
            race = RaceCatalog.Check(race, _catalog()!, RaceLibrary.LawIds);
            RaceLibrary.Save(race, race.Picture.Length == 0 ? _picture : null, _pictureFrom);
            Changed?.Invoke();
            Edit(race);
            ListRaces(_catalog()!, race);
            Fill();
            Say($"{race.Name} saved.", Severity.Good);
        });
    }

    private void Import(string path) => Replace(() => Try(() =>
    {
        var race = RaceLibrary.Import(path, _catalog()!);
        Changed?.Invoke();
        Edit(race);
        ListRaces(_catalog()!, race);
        Fill();
        Say($"{race.Name} imported.", Severity.Good);
    }));

    private void Export(string path) => Try(() =>
    {
        if (_saved is not { } race) return;
        RaceLibrary.Export(race, path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json");
        Say($"{race.Name} exported{(IsDirty ? " as it was last saved" : "")}.", Severity.Good);
    });

    private void Delete()
    {
        if (_saved is not { } race) return;
        _ask($"Delete the race {race.Name} for good? Games started with it keep their own copy.", new (string, Action)[]
        {
            ("Delete", () => Try(() =>
            {
                RaceLibrary.Delete(race.Id);
                Changed?.Invoke();
                Visible = false;
                Closed?.Invoke(null);
            })),
        });
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is FormatException or System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Say(e.Message, Severity.Bad); }
    }

    /// <summary>Does it, asking first if that would lose changes.</summary>
    private void Replace(Action action)
    {
        if (!IsDirty) { action(); return; }
        _ask($"{_race.Name} has changes that aren't saved. Discard them?", new (string, Action)[] { ("Discard", action) });
    }

    private void Back() => Replace(() =>
    {
        Visible = false;
        Closed?.Invoke(_saved?.Id);
    });

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || !@event.IsActionPressed("ui_cancel")) return;
        Back();
        GetViewport().SetInputAsHandled();
    }
}
