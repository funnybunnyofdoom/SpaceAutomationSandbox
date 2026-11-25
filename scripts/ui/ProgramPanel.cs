using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Builds programs (#120): the steps as a list, the chosen step's settings under it, and what a dry run with the craft you fly says of
/// each. Nothing changes for any ship until it's saved; then it changes for all that run it.
/// </summary>
public partial class ProgramPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private static readonly (StepKind Kind, string Name, string Icon)[] Kinds =
    {
        (StepKind.FlyTo, "Fly to", "speed"), (StepKind.Dock, "Dock", "docked"), (StepKind.Load, "Load", "cargo"), (StepKind.Unload, "Unload", "freight"), (StepKind.Mine, "Mine", "mining"), (StepKind.Wait, "Wait", "clock"),
        (StepKind.Recharge, "Recharge", "battery"), (StepKind.Refuel, "Refuel", "mat_fuel"), (StepKind.Repair, "Repair", "hull"), (StepKind.Message, "Message", "message"), (StepKind.If, "If ... otherwise", "job"),
    };

    private static readonly (TestKind Kind, string Name)[] Tests =
    {
        (TestKind.CargoPercent, "hold, % full"), (TestKind.CargoTonnes, "material aboard, t"), (TestKind.PowerPercent, "battery, %"), (TestKind.FuelTonnes, "fuel aboard, t"), (TestKind.HealthPercent, "health, %"),
        (TestKind.StepMinutes, "time in this step, min"), (TestKind.StationStockTonnes, "stock at a station, t"), (TestKind.StationFreePorts, "free ports at a station"),
    };

    private static readonly string[] Fallbacks = { "keep trying", "go to the next step", "go to the first step", "stop the job and say so" };

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _craft;
    private readonly MaterialCatalog _materials;
    private readonly OptionButton _list;
    private readonly LineEdit _name;
    private readonly VBoxContainer _steps, _editor;
    private readonly Label _status, _users;
    private readonly Button _save, _saveNew, _delete, _export, _run, _addYes, _addNo, _up, _down, _copy, _remove;
    private readonly List<(int Id, Button Row, Label Note)> _rows = new();
    private List<ProgramStep> _draft = new();
    private int _id, _selected;
    private bool _dirty;
    private string? _confirm;
    private SectorState? _of;
    private double _sinceRefresh;
    private FileDialog? _files;
    private Action<string>? _picked;

    /// <summary>Something in the game changed.</summary>
    public event Action? Changed;

    /// <summary>Back to the job panel.</summary>
    public event Action? JobsChosen;

    public ProgramPanel(Func<SectorState> sector, Func<CraftState?> craft, MaterialCatalog materials)
    {
        (_sector, _craft, _materials) = (sector, craft, materials);
        SetAnchorsPreset(LayoutPreset.RightWide);
        (OffsetLeft, OffsetRight, OffsetTop, OffsetBottom) = (-652, -12, 12, -12);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        AddChild(box);
        var title = new Label { Text = "PROGRAMS", ThemeTypeVariation = "SectionHeader" };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(UiTabs.Row(("job", "Job", () => JobsChosen?.Invoke()), ("edit_program", "Programs", null)));
        var top = new HBoxContainer();
        box.AddChild(top);
        _list = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(190, 0), ClipText = true };
        _list.ItemSelected += i => Choose(_list.GetItemId((int)i));
        top.AddChild(_list);
        _name = new LineEdit { PlaceholderText = "Program name", MaxLength = JobProgram.MaxName, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _name.TextChanged += _ => Edited();
        _name.TextSubmitted += _ => _name.ReleaseFocus();
        top.AddChild(_name);
        var files = new HFlowContainer();
        box.AddChild(files);
        files.AddChild(_save = Press("Save", "Saves it; ships that run it carry on with the new one, each at its place", () => Save(_id)));
        files.AddChild(_saveNew = Press("Save as new", "Saves it as another program; the ships keep the old one", () => Save(0)));
        files.AddChild(_run = Press("Run", "", Run));
        files.AddChild(Press("From its job", "Starts from the mining or freight job of the craft you fly", FromJob));
        files.AddChild(_delete = Press("Delete", "Deletes the program; no ship may be running it", Delete));
        files.AddChild(Press("Import...", "Takes in a program file; stations are found by name, the rest is left to choose", () => Pick(FileDialog.FileModeEnum.OpenFile, "", Import)));
        files.AddChild(_export = Press("Export...", "Writes the program to a file, for another game or someone else", () => Pick(FileDialog.FileModeEnum.SaveFile, $"{BlueprintEditor.SlugFromName(_name.Text)}.json", Export)));
        box.AddChild(_users = new Label { ThemeTypeVariation = "HudText", Modulate = new Color(1, 1, 1, 0.75f) });

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.1f, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _steps = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _steps.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_steps);

        var tools = new HFlowContainer();
        box.AddChild(tools);
        tools.AddChild(Add("Add step", "After the chosen step", s => ProgramEdit.AddAfter(_draft, _selected, s)));
        tools.AddChild(_addYes = Add("Inside: yes", "At the end of the chosen if's yes", s => ProgramEdit.AddInside(_draft, _selected, false, s)));
        tools.AddChild(_addNo = Add("Inside: otherwise", "At the end of the chosen if's otherwise", s => ProgramEdit.AddInside(_draft, _selected, true, s)));
        tools.AddChild(_up = Press("Up", "Moves the chosen step up", () => Change(ProgramEdit.Move(_draft, _selected, -1))));
        tools.AddChild(_down = Press("Down", "Moves the chosen step down", () => Change(ProgramEdit.Move(_draft, _selected, 1))));
        tools.AddChild(_copy = Press("Copy", "Copies the chosen step", () => Change(ProgramEdit.Copy(_draft, _selected))));
        tools.AddChild(_remove = Press("Remove", "Removes the chosen step, and what's inside it", () => Change(ProgramEdit.Remove(_draft, _selected))));

        var editorScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(editorScroll);
        _editor = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        editorScroll.AddChild(_editor);
        box.AddChild(_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 0) });
    }

    public ProgramPanel() : this(() => new SectorState(0), () => null, new MaterialCatalog(Array.Empty<MaterialDefinition>())) { }

    private static Button Press(string text, string tip, Action press)
    {
        var button = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None };
        button.Pressed += press;
        return button;
    }

    private MenuButton Add(string text, string tip, Func<ProgramStep, List<ProgramStep>> add)
    {
        var menu = new MenuButton { Text = text, TooltipText = tip, Flat = false, FocusMode = FocusModeEnum.None };
        foreach (var (_, name, icon) in Kinds) menu.GetPopup().AddIconItem(UiIcons.Get(icon), name);
        menu.GetPopup().IndexPressed += i =>
        {
            var id = ProgramEdit.NextId(_draft);
            var kind = Kinds[(int)i].Kind;
            var step = new ProgramStep
            {
                Id = id, Kind = kind, Seconds = kind == StepKind.Wait ? 30 : kind == StepKind.Recharge ? 60 : 0, Percent = kind == StepKind.Recharge ? 80 : 100, UntilDone = kind == StepKind.Unload,
                Until = kind == StepKind.If ? new ProgramCondition { Tests = new() { new ProgramTest { Kind = TestKind.CargoPercent, Value = 50 } } } : null,
            };
            var next = add(step);
            _selected = id;
            Change(next);
        };
        return menu;
    }

    // --- The program ---

    public void Open(int? program = null)
    {
        Visible = true;
        var sector = _sector();
        if (!ReferenceEquals(_of, sector)) { _of = sector; _dirty = false; Load(0); }
        if (program is { } id && !_dirty && sector.ProgramById(id) is not null) Load(id);
        else if (_draft.Count == 0 && !_dirty && sector.Programs.FirstOrDefault() is { } first) Load(first.Id);
        Relist();
        ShowSteps();
    }

    private void Relist()
    {
        _list.Clear();
        _list.AddItem("New program", 0);
        foreach (var p in _sector().Programs) _list.AddItem(p.Name, p.Id);
        for (var i = 0; i < _list.ItemCount; i++) if (_list.GetItemId(i) == _id) _list.Select(i);
    }

    private void Choose(int id)
    {
        if (_dirty && !Confirmed("choose" + id, "This program has changes that aren't saved. Choose again to discard them.")) { Relist(); return; }
        Load(id);
        Relist();
        ShowSteps();
    }

    private void Load(int id)
    {
        var program = _sector().ProgramById(id);
        _id = program?.Id ?? 0;
        _draft = program?.Steps.ToList() ?? new();
        _name.Text = program?.Name ?? "";
        _selected = _draft.FirstOrDefault()?.Id ?? 0;
        _dirty = false;
        _confirm = null;
        Say("");
    }

    private void Edited()
    {
        _dirty = true;
        _confirm = null;
        RefreshButtons();
    }

    private void Change(List<ProgramStep> steps)
    {
        _draft = steps;
        if (ProgramEdit.Find(_draft, _selected) is null) _selected = JobProgram.All(_draft).LastOrDefault()?.Id ?? 0;
        Edited();
        Say("");
        ShowSteps();
    }

    /// <summary>A setting of the chosen step: its row changes, its editor stays as it is.</summary>
    private void Set(ProgramStep step, bool rebuild = false)
    {
        _draft = ProgramEdit.Replace(_draft, step);
        Edited();
        if (rebuild) ShowSteps();
        else RefreshRows();
    }

    private bool Confirmed(string what, string question)
    {
        if (_confirm == what) { _confirm = null; return true; }
        _confirm = what;
        Say(question, Severity.Warning);
        return false;
    }

    private void Say(string text, Severity severity = Severity.Muted)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", UiTheme.Of(severity));
    }

    private bool Save(int id)
    {
        var result = _sector().SaveProgram(id, _name.Text, _draft, out var saved);
        Say(result.Message, result.Done ? Severity.Good : Severity.Bad);
        if (!result.Done) return false;
        _id = saved;
        _name.Text = _sector().ProgramById(saved)!.Name;
        _dirty = false;
        Changed?.Invoke();
        Relist();
        ShowSteps();
        return true;
    }

    private void Run()
    {
        if (_craft() is not { } craft) return;
        if ((_dirty || _id == 0) && !Save(_id)) return;
        var result = _sector().StartJob(craft.Id, new ProgramJob(_id));
        Say(result.Message, result.Done ? Severity.Good : Severity.Bad);
        if (result.Done) Changed?.Invoke();
        RefreshRows();
    }

    private void FromJob()
    {
        if (ProgramEdit.FromJob(_craft()?.Job) is not { } made) { Say("The craft you fly has no mining or freight job to start from.", Severity.Warning); return; }
        if (_dirty && !Confirmed("from", "This program has changes that aren't saved. Press again to discard them.")) return;
        Load(0);
        _name.Text = made.Name;
        _draft = made.Steps;
        _selected = 1;
        Edited();
        Relist();
        ShowSteps();
    }

    private void Delete()
    {
        if (_id == 0 || !Confirmed("delete", $"Delete the program {_name.Text} for good? Press again to delete it.")) return;
        var result = _sector().DeleteProgram(_id);
        if (result.Done) { Load(0); Changed?.Invoke(); }
        Relist();
        ShowSteps();
        Say(result.Message, result.Done ? Severity.Good : Severity.Bad);
    }

    private void Pick(FileDialog.FileModeEnum mode, string name, Action<string> picked)
    {
        if (_files is null)
        {
            _files = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true };
            _files.FileSelected += path => _picked?.Invoke(path);
            _files.AddFilter("*.json", "Program file");
            AddChild(_files);
        }
        _picked = picked;
        _files.FileMode = mode;
        _files.CurrentFile = name;
        _files.PopupCentered(new Vector2I(800, 560));
    }

    private void Import(string path)
    {
        try
        {
            if (new System.IO.FileInfo(path).Length > 1_000_000) throw new FormatException("The file is too big to be a program.");
            var sector = _sector();
            var (name, steps) = JobProgram.FromFile(System.IO.File.ReadAllText(path), _materials,
                n => sector.Crafts.FirstOrDefault(c => n.Length > 0 && SectorState.IsPlayers(c) && c.Name == n)?.Id.Value ?? 0, n => sector.Bodies.FirstOrDefault(b => n.Length > 0 && b.Name == n)?.Id.Value ?? 0);
            if (_dirty && !Confirmed("import", "This program has changes that aren't saved. Import again to discard them.")) return;
            Load(0);
            _name.Text = name;
            _draft = steps;
            _selected = steps[0].Id;
            Edited();
            Relist();
            ShowSteps();
            Say($"{name} read. Check its stations, then save it.", Severity.Good);
        }
        catch (Exception e) when (e is FormatException or System.IO.IOException or UnauthorizedAccessException) { Say(e.Message, Severity.Bad); }
    }

    private void Export(string path)
    {
        var sector = _sector();
        if (JobProgram.Problem(_name.Text, _draft, _materials) is { } why) { Say(why, Severity.Bad); return; }
        try
        {
            var text = new JobProgram(_id, _name.Text.Trim(), _draft).ToFile(i => sector.Crafts.FirstOrDefault(c => c.Id.Value == i)?.Name, i => sector.Bodies.FirstOrDefault(b => b.Id.Value == i)?.Name);
            System.IO.File.WriteAllText(path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json", text);
            Say("Exported.", Severity.Good);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Say(e.Message, Severity.Bad); }
    }

    // --- The steps ---

    private void ShowSteps()
    {
        foreach (var child in _steps.GetChildren()) { _steps.RemoveChild(child); child.QueueFree(); }
        _rows.Clear();
        if (_draft.Count == 0) _steps.AddChild(new Label { Text = "No steps yet: add one below, or start from the job of the craft you fly.", Modulate = new Color(1, 1, 1, 0.65f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(580, 0) });
        Rows(_draft, 0);
        ShowEditor();
        RefreshRows();

        void Rows(IReadOnlyList<ProgramStep> steps, int depth)
        {
            foreach (var step in steps)
            {
                var row = new HBoxContainer();
                row.AddChild(new Control { CustomMinimumSize = new Vector2(depth * 22, 0) });
                var button = new Button
                {
                    ToggleMode = true, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Icon = UiIcons.Get(Kinds.First(k => k.Kind == step.Kind).Icon), TextureFilter = TextureFilterEnum.Nearest,
                };
                var id = step.Id;
                button.Pressed += () => { _selected = id; ShowEditor(); RefreshRows(); };
                row.AddChild(button);
                var note = new Label { ThemeTypeVariation = "HudText", CustomMinimumSize = new Vector2(170, 0), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Pass };
                row.AddChild(note);
                _steps.AddChild(row);
                _rows.Add((id, button, note));
                if (step.Kind != StepKind.If) continue;
                Rows(step.Then, depth + 1);
                if (step.Otherwise.Count == 0) continue;
                var otherwise = new HBoxContainer();
                otherwise.AddChild(new Control { CustomMinimumSize = new Vector2(depth * 22 + 8, 0) });
                otherwise.AddChild(new Label { Text = "Otherwise", ThemeTypeVariation = "SectionHeader" });
                _steps.AddChild(otherwise);
                Rows(step.Otherwise, depth + 1);
            }
        }
    }

    /// <summary>The rows' words, what a dry run says of them, and which step the craft you fly is at.</summary>
    private void RefreshRows()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var craft = _craft();
        var checks = craft is null ? new Dictionary<int, StepCheck>() : sector.CheckProgram(craft, _draft).ToDictionary(c => c.StepId);
        var at = craft?.Job is ProgramJob { Active: true } job && job.ProgramId == _id && _id != 0 && !_dirty ? job.StepId : -1;
        var number = 0;
        foreach (var (id, row, note) in _rows)
        {
            number++;
            if (ProgramEdit.Find(_draft, id) is not { } step) continue;
            row.Text = $"{(id == at ? "▶ " : "")}{number}. {sector.Describe(step)}";
            row.TooltipText = sector.Describe(step);
            row.SetPressedNoSignal(id == _selected);
            var check = checks.GetValueOrDefault(id);
            note.Text = check?.Note ?? "";
            note.TooltipText = check is null ? "" : $"With {craft!.Name}, as things are now: {check.Note}";
            note.Modulate = UiTheme.Of(check is null ? Severity.Muted : check.Wrong ? Severity.Bad : check.Reached ? Severity.Good : Severity.Muted);
        }
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var users = _id == 0 ? 0 : _sector().ProgramUsers(_id);
        _users.Text = (_id == 0 ? "A new program" : users == 0 ? "No ship runs it" : $"{users} ship{(users == 1 ? " runs" : "s run")} it") + (_dirty ? ", with changes that aren't saved." : ".");
        _save.Text = _id != 0 && users > 0 ? $"Save for {users} ship{(users == 1 ? "" : "s")}" : "Save";
        _saveNew.Disabled = _id == 0;
        _delete.Disabled = _id == 0;
        _export.Disabled = _draft.Count == 0;
        var craft = _craft();
        _run.Disabled = craft is null || _draft.Count == 0;
        _run.Text = craft is null ? "Run" : $"Run on {craft.Name}";
        _run.TooltipText = "Saves it and starts it on the craft you fly";
        var step = ProgramEdit.Find(_draft, _selected);
        _addYes.Disabled = _addNo.Disabled = step?.Kind != StepKind.If;
        _up.Disabled = _down.Disabled = _copy.Disabled = _remove.Disabled = step is null;
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (!ReferenceEquals(_of, _sector())) { Open(); return; }
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) RefreshRows();
    }

    // --- The chosen step ---

    private ProgramStep? Step => ProgramEdit.Find(_draft, _selected);

    private void ShowEditor()
    {
        foreach (var child in _editor.GetChildren()) { _editor.RemoveChild(child); child.QueueFree(); }
        if (Step is not { } step) return;
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        _editor.AddChild(grid);
        switch (step.Kind)
        {
            case StepKind.FlyTo: Place(grid, step, bodies: true); break;
            case StepKind.Dock: Place(grid, step); break;
            case StepKind.Load:
                Place(grid, step, here: true);
                MaterialChoice(grid, step.Material, "Material", "(choose)", m => Set(Step! with { Material = m }));
                Number(grid, "Until aboard, t (0: as much as fits)", step.Tonnes, 0, 100_000, 0.5, v => Set(Step! with { Tonnes = v }));
                Check(grid, "Stay until it's all there", step.UntilDone, on => Set(Step! with { UntilDone = on }));
                break;
            case StepKind.Unload:
                Place(grid, step, here: true);
                MaterialChoice(grid, step.Material, "Material", "everything but fuel", m => Set(Step! with { Material = m }));
                Number(grid, "Keep aboard, t", step.Tonnes, 0, 100_000, 0.5, v => Set(Step! with { Tonnes = v }));
                Check(grid, "Stay until it's all gone", step.UntilDone, on => Set(Step! with { UntilDone = on }));
                break;
            case StepKind.Mine:
                Bodies(grid, step.BodyId, "Mine at", b => b.IsMineable, id => Set(Step! with { BodyId = id }, rebuild: true));
                MaterialChoice(grid, step.Material, "Material", "(choose)", m => Set(Step! with { Material = m }), _sector().Bodies.FirstOrDefault(b => b.Id.Value == step.BodyId)?.MineableMaterials.ToList());
                Number(grid, "Until the hold is, % full", step.Percent, 1, 100, 5, v => Set(Step! with { Percent = v }));
                break;
            case StepKind.Wait:
                Number(grid, "Seconds (0: no limit)", step.Seconds, 0, 86_400, 5, v => Set(Step! with { Seconds = v }));
                break;
            case StepKind.Recharge:
                Place(grid, step, here: true);
                Number(grid, "Until the battery is, %", step.Percent, 1, 100, 5, v => Set(Step! with { Percent = v }));
                Number(grid, "Seconds at most (0: no limit)", step.Seconds, 0, 86_400, 5, v => Set(Step! with { Seconds = v }));
                break;
            case StepKind.Refuel:
                Place(grid, step, here: true);
                Number(grid, "Until aboard, t (0: as much as fits)", step.Tonnes, 0, 100_000, 0.5, v => Set(Step! with { Tonnes = v }));
                break;
            case StepKind.Repair: Place(grid, step, nearestYard: true); break;
            case StepKind.Message:
            {
                grid.AddChild(new Label { Text = "Says" });
                var text = new LineEdit { Text = step.Text, MaxLength = JobProgram.MaxText, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(380, 0) };
                text.TextChanged += t => Set(Step! with { Text = t });
                grid.AddChild(text);
                break;
            }
        }
        if (step.Kind == StepKind.If) Condition("If", step.Until!, c => Step! with { Until = c }, required: true);
        if (step.Kind == StepKind.Wait) Condition("Until", step.Until, c => Step! with { Until = c });
        if (step.Kind == StepKind.If) return;
        Condition("Only if", step.OnlyIf, c => Step! with { OnlyIf = c });
        if (step.Kind is StepKind.Message or StepKind.Wait) return;
        var give = new GridContainer { Columns = 2 };
        give.AddThemeConstantOverride("h_separation", 12);
        _editor.AddChild(give);
        Number(give, "If it can't be done for, s (0: for ever)", step.GiveUpSeconds, 0, 86_400, 10, v => Set(Step! with { GiveUpSeconds = v }));
        give.AddChild(new Label { Text = "then" });
        var fallback = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var f in Fallbacks) fallback.AddItem(f);
        fallback.Select((int)step.Fallback);
        fallback.ItemSelected += i => Set(Step! with { Fallback = (StepFallback)i });
        give.AddChild(fallback);
    }

    private static void Number(Container grid, string label, double value, double from, double to, double step, Action<double> set)
    {
        grid.AddChild(new Label { Text = label });
        var box = new SpinBox { MinValue = from, MaxValue = to, Step = Math.Min(step, 1), CustomArrowStep = step, Value = value, CustomMinimumSize = new Vector2(130, 0) };
        box.ValueChanged += v => set(v);
        grid.AddChild(box);
    }

    private static void Check(Container grid, string label, bool on, Action<bool> set)
    {
        grid.AddChild(new Label { Text = label });
        var box = new CheckBox { ButtonPressed = on, FocusMode = FocusModeEnum.None };
        box.Toggled += v => set(v);
        grid.AddChild(box);
    }

    private IEnumerable<CraftState> Stations() => _sector().Crafts.Where(c => c.Stats.DockingPorts.Count > 0 && !c.IsWreck && SectorState.IsPlayers(c)).OrderBy(c => c.Stats.TotalThrustN > 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    private OptionButton Choice(Container grid, string? label)
    {
        if (label is not null) grid.AddChild(new Label { Text = label });
        var option = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(label is null ? 130 : 280, 0), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(option);
        return option;
    }

    /// <summary>A station, or for flying a body too.</summary>
    private void Place(Container grid, ProgramStep step, bool bodies = false, bool here = false, bool nearestYard = false)
    {
        var option = Choice(grid, "At");
        var places = new List<(uint Station, uint Body)>();
        void Item(string text, uint station, uint body, string icon)
        {
            option.AddIconItem(UiIcons.Get(icon), text);
            places.Add((station, body));
            if (station == step.StationId && body == step.BodyId) option.Select(option.ItemCount - 1);
        }
        Item(here ? "where it's docked" : nearestYard ? "the nearest shipyard" : "(choose)", 0, 0, "help");
        foreach (var c in Stations()) if (!nearestYard || c.IsShipyard) Item(c.Name, c.Id.Value, 0, c.IsShipyard ? "shipyard" : c.Stats.TotalThrustN > 0 ? "fleet" : "orbit");
        if (bodies) foreach (var b in _sector().Bodies.Where(b => b.Kind != BodyKind.Star)) Item(b.Name, 0, b.Id.Value, UiIcons.BodyIcon(b));
        if (option.Selected < 0 || places[option.Selected] != (step.StationId, step.BodyId)) Item("(gone)", step.StationId, step.BodyId, "warn");
        option.ItemSelected += i => Set(Step! with { StationId = places[(int)i].Station, BodyId = places[(int)i].Body });
    }

    private void Bodies(Container grid, uint value, string label, Func<CelestialBodyState, bool> which, Action<uint> set)
    {
        var option = Choice(grid, label);
        var ids = new List<uint> { 0 };
        option.AddItem("(choose)");
        foreach (var b in _sector().Bodies.Where(which))
        {
            option.AddIconItem(UiIcons.Get(UiIcons.BodyIcon(b)), b.AnySiteSurveyed ? b.Name : $"{b.Name} (unsurveyed)");
            ids.Add(b.Id.Value);
            if (b.Id.Value == value) option.Select(option.ItemCount - 1);
        }
        option.ItemSelected += i => set(ids[(int)i]);
    }

    private void MaterialChoice(Container grid, string value, string? label, string none, Action<string> set, List<string>? only = null)
    {
        var option = Choice(grid, label);
        var ids = new List<string> { "" };
        option.AddItem(none);
        foreach (var m in _materials.Materials.Where(m => only is null || only.Contains(m.Id) || m.Id == value))
        {
            option.AddIconItem(UiIcons.Material(m.Id), m.Name);
            ids.Add(m.Id);
            if (m.Id == value) option.Select(option.ItemCount - 1);
        }
        option.ItemSelected += i => set(ids[(int)i]);
    }

    /// <summary>A condition's tests; without one, a box to tick that begins one.</summary>
    private void Condition(string label, ProgramCondition? condition, Func<ProgramCondition?, ProgramStep> with, bool required = false)
    {
        var head = new HBoxContainer();
        _editor.AddChild(head);
        if (required) head.AddChild(UiTheme.Header(label));
        else
        {
            var on = new CheckBox { Text = label, ButtonPressed = condition is not null, FocusMode = FocusModeEnum.None };
            on.Toggled += v => Set(with(v ? new ProgramCondition { Tests = new() { new ProgramTest { Kind = TestKind.CargoPercent, Value = 50 } } } : null), rebuild: true);
            head.AddChild(on);
        }
        if (condition is null) return;
        if (condition.Tests.Count > 1)
        {
            var any = new OptionButton { FocusMode = FocusModeEnum.None };
            any.AddItem("all of these hold");
            any.AddItem("any of these holds");
            any.Select(condition.Any ? 1 : 0);
            any.ItemSelected += i => Set(with(condition with { Any = i == 1 }), rebuild: true);
            head.AddChild(any);
        }
        if (condition.Tests.Count < JobProgram.MaxTests) head.AddChild(Press("Add test", "", () => Set(with(condition with { Tests = condition.Tests.Append(new ProgramTest { Kind = TestKind.CargoPercent, Value = 50 }).ToList() }), rebuild: true)));
        for (var n = 0; n < condition.Tests.Count; n++)
        {
            var index = n;
            var test = condition.Tests[n];
            // The latest of the step's tests, not those of when this was built: a number may have changed since.
            void Put(Func<ProgramTest, ProgramTest> change, bool rebuild = false)
            {
                var now = Same(Step!) is { } c && index < c.Tests.Count ? c : condition;
                Set(with(now with { Tests = now.Tests.Select((t, i) => i == index ? change(t) : t).ToList() }), rebuild);
            }
            ProgramCondition? Same(ProgramStep step) => label == "Only if" ? step.OnlyIf : step.Until;
            var row = new HFlowContainer();
            _editor.AddChild(row);
            var kind = Choice(row, null);
            kind.CustomMinimumSize = new Vector2(170, 0);
            foreach (var (_, name) in Tests) kind.AddItem(name);
            kind.Select(Array.FindIndex(Tests, t => t.Kind == test.Kind));
            kind.ItemSelected += i => Put(t => t with { Kind = Tests[(int)i].Kind }, rebuild: true);
            if (test.Kind is TestKind.StationStockTonnes or TestKind.StationFreePorts)
            {
                var station = Choice(row, null);
                var ids = new List<uint> { 0 };
                station.AddItem("(station)");
                foreach (var c in Stations())
                {
                    station.AddItem(c.Name);
                    ids.Add(c.Id.Value);
                    if (c.Id.Value == test.StationId) station.Select(station.ItemCount - 1);
                }
                station.ItemSelected += i => Put(t => t with { StationId = ids[(int)i] });
            }
            if (test.Kind is TestKind.StationStockTonnes or TestKind.CargoTonnes) MaterialChoice(row, test.Material, null, "(material)", m => Put(t => t with { Material = m }));
            var below = new OptionButton { FocusMode = FocusModeEnum.None };
            below.AddItem("at least");
            below.AddItem("below");
            below.Select(test.Below ? 1 : 0);
            below.ItemSelected += i => Put(t => t with { Below = i == 1 });
            row.AddChild(below);
            var value = new SpinBox { MinValue = 0, MaxValue = 1_000_000, Step = 0.5, Value = test.Value, CustomMinimumSize = new Vector2(100, 0) };
            value.ValueChanged += v => Put(t => t with { Value = v });
            row.AddChild(value);
            if (condition.Tests.Count > 1 || !required)
                row.AddChild(Press("×", "Removes this test", () => Set(with(condition.Tests.Count == 1 ? null : (Same(Step!) ?? condition) with { Tests = (Same(Step!) ?? condition).Tests.Where((_, i) => i != index).ToList() }), rebuild: true)));
        }
    }
}
