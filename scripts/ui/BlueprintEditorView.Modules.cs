using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>The module palette (#113): groups of parts to stamp, the player's own and a few built-in ones. Part of large-ship building.</summary>
public partial class BlueprintEditorView
{
    private VBoxContainer _partsBox = null!, _modulesBox = null!, _moduleList = null!;
    private HBoxContainer _paletteTabs = null!;
    private Button _partsTab = null!, _modulesTab = null!, _saveModule = null!, _renameModule = null!, _deleteModule = null!;
    private LineEdit _moduleName = null!;
    private readonly Dictionary<Button, ModuleEntry> _moduleButtons = new();
    private ModuleEntry? _module;

    private void BuildModules(VBoxContainer left)
    {
        _paletteTabs = new HBoxContainer { Visible = false };
        left.AddChild(_paletteTabs);
        _partsTab = Tab("Parts", false);
        _modulesTab = Tab("Modules", true);
        left.AddChild(_partsBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill });
        left.AddChild(_modulesBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false });

        _moduleName = new LineEdit { PlaceholderText = "Module name", MaxLength = ShipModule.MaxNameLength };
        _moduleName.TextChanged += _ => { _pendingConfirm = null; RefreshModuleButtons(); };
        _moduleName.TextSubmitted += _ => _moduleName.ReleaseFocus();
        _modulesBox.AddChild(_moduleName);
        var row = new HFlowContainer();
        _modulesBox.AddChild(row);
        row.AddChild(_saveModule = ActionButton("Save selection", SaveModule));
        row.AddChild(_renameModule = ActionButton("Rename", RenameModule));
        row.AddChild(_deleteModule = ActionButton("Delete", DeleteModule));
        _saveModule.TooltipText = "Saves the selected parts as a module with this name";
        _renameModule.TooltipText = "Gives the chosen module this name";
        _deleteModule.TooltipText = "Deletes the chosen module's file. Designs made with it keep their parts.";
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _modulesBox.AddChild(scroll);
        _moduleList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _moduleList.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_moduleList);

        Button Tab(string text, bool modules)
        {
            var button = new Button { Text = text, ToggleMode = true, ButtonPressed = !modules, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            button.Pressed += () => ShowModules(modules);
            _paletteTabs.AddChild(button);
            return button;
        }
    }

    private void ShowModules(bool modules)
    {
        _partsBox.Visible = !modules;
        _modulesBox.Visible = modules;
        _partsTab.SetPressedNoSignal(!modules);
        _modulesTab.SetPressedNoSignal(modules);
        if (modules) ListModules(_module?.Id);
    }

    private void ListModules(string? choose = null)
    {
        foreach (var child in _moduleList.GetChildren()) { _moduleList.RemoveChild(child); child.QueueFree(); }
        _moduleButtons.Clear();
        _module = null;
        foreach (var entry in ModuleLibrary.List(_catalog))
        {
            var button = new Button { ToggleMode = true, FocusMode = _usingButtons ? FocusModeEnum.All : FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 56), Disabled = entry.Module is null };
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            row.SetAnchorsPreset(LayoutPreset.FullRect);
            (row.OffsetLeft, row.OffsetRight) = (6, -6);
            button.AddChild(row);
            row.AddChild(new DesignPicture { Design = entry.Module?.Design, CustomMinimumSize = new Vector2(48, 48), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            var text = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            text.AddThemeConstantOverride("separation", 0);
            row.AddChild(text);
            text.AddChild(Line(entry.Module?.Name ?? entry.Id, Colors.White));
            if (entry.Module is { } module)
            {
                var stats = module.Design.ComputeStats(_catalog);
                var locked = module.Cells.Select(c => LockedBy(c.PartId)).Where(t => t is not null).Distinct().ToList();
                text.AddChild(Line((locked.Count > 0 ? "[locked]  " : "") + $"{module.Width} x {module.Height}  {stats.TotalMassKg / 1000:0.#} t{Does(stats)}", UiTheme.Of(locked.Count > 0 ? Severity.Warning : Severity.Muted)));
                button.TooltipText = $"{module.Name}{(entry.IsBuiltIn ? " (built-in)" : "")}\n" + string.Join("\n", StatRows(stats).Select(r => r.Text))
                                     + "\nCost " + _model.CostText(module.Cells).TrimStart(';', ' ') + (locked.Count > 0 ? $"\nCan be planned with; to build it, research {string.Join(", ", locked)}" : "")
                                     + "\nClick to stamp; drag to repeat it in a row.";
            }
            else
            {
                text.AddChild(Line("Can't be used", UiTheme.Of(Severity.Bad)));
                button.TooltipText = entry.Problem;
            }
            button.Pressed += () => ChooseModule(entry == _module && _grid.Stamp is not null ? null : entry); // again: stop
            _moduleButtons[button] = entry;
            _moduleList.AddChild(button);
            if (entry.Id != choose || entry.IsBuiltIn || _module is not null) continue;
            _module = entry;
            button.SetPressedNoSignal(true);
        }
        RefreshModuleButtons();

        static Label Line(string text, Color colour) => new()
        {
            Text = text, ThemeTypeVariation = "HudText", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Ignore, Modulate = colour,
        };
    }

    /// <summary>What a module does, in a few words.</summary>
    private static string Does(ShipStats s) => string.Concat(new[]
    {
        s.TotalThrustN > 0 ? $", {s.TotalThrustN / 1000:0} kN" : s.NonForwardThrustN > 0 ? $", {s.NonForwardThrustN / 1000:0} kN" : "",
        s.PowerGenerationKW + s.GeneratorKW > 0 ? $", +{s.PowerGenerationKW + s.GeneratorKW:0} kW" : "",
        s.CrewCapacity > 0 ? $", {s.CrewCapacity} crew" : "",
        s.CargoCapacityKg > 0 ? $", {s.CargoCapacityKg / 1000:0} t cargo" : "",
        s.MiningRateKgPerS > 0 ? $", mines {s.MiningRateKgPerS:0} kg/s" : "",
        s.DockingPorts.Count > 0 ? $", {s.DockingPorts.Count} port{(s.DockingPorts.Count == 1 ? "" : "s")}" : "",
    });

    private void ChooseModule(ModuleEntry? entry)
    {
        _module = entry;
        _pendingConfirm = null;
        _moduleName.ReleaseFocus();
        _grid.SetStamp(entry?.Module?.Cells);
        if (entry?.Module is { } module)
        {
            _moduleName.Text = module.Name;
            SetStatus($"{module.Name}: click to stamp, drag to repeat. {InputActions.Key1(InputActions.EditRotate)} turns it, {InputActions.Key1(InputActions.EditFlip)} mirrors it, right click stops.");
        }
        RefreshModuleButtons();
    }

    private void RefreshModuleButtons()
    {
        foreach (var (button, entry) in _moduleButtons) button.SetPressedNoSignal(entry == _module);
        _saveModule.Disabled = _model.Selection.Count == 0;
        _renameModule.Disabled = _deleteModule.Disabled = _module is null or { IsBuiltIn: true } or { Module: null };
    }

    /// <summary>The module of this name and these parts, or null with the reason shown.</summary>
    private ShipModule? Named(Func<ShipModule> make)
    {
        try
        {
            var module = make();
            if (!ModuleLibrary.IsBuiltInId(module.Id)) return module;
            SetStatus($"'{module.Name}' is a built-in module. Pick another name.", ErrorColor);
        }
        catch (FormatException e) { SetStatus(e.Message, ErrorColor); }
        return null;
    }

    private void SaveModule()
    {
        if (Named(() => new ShipModule(_moduleName.Text, _model.Selected)) is not { } module) return;
        if (ModuleLibrary.Exists(module.Id) && !Confirmed("module:" + module.Id, $"A module '{module.Id}' already exists. Press again to overwrite it; designs made with it stay as they are.")) return;
        Store(() => ModuleLibrary.Save(module), $"Saved {module.Name}: {module.Cells.Count} parts.", module.Id);
    }

    private void RenameModule()
    {
        if (_module is not { Module: { } old, Id: var oldId } || Named(() => old.Renamed(_moduleName.Text)) is not { } module) return;
        if (module.Id != oldId && ModuleLibrary.Exists(module.Id) && !Confirmed("rename:" + module.Id, $"A module '{module.Id}' already exists. Press again to overwrite it.")) return;
        Store(() =>
        {
            ModuleLibrary.Save(module);
            if (module.Id != oldId) ModuleLibrary.Delete(oldId);
        }, $"{old.Name} is now {module.Name}.", module.Id);
    }

    private void DeleteModule()
    {
        if (_module is not { Module: { } module, Id: var id } || !Confirmed("delete:" + id, $"Delete the module {module.Name} for good? Designs made with it keep their parts. Press again to delete it.")) return;
        _grid.SetStamp(null);
        Store(() => ModuleLibrary.Delete(id), $"{module.Name} deleted.", null);
    }

    private void Store(Action write, string done, string? choose)
    {
        try
        {
            write();
            SetStatus(done, OkColor);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { SetStatus($"Failed: {e.Message}", ErrorColor); }
        ListModules(choose);
    }
}
