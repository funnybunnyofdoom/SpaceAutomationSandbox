using System;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>The menu bar (#106): its buttons, and what each one opens, needs and warns about.</summary>
public partial class Main
{
    private TopBar _topBar = null!;

    /// <summary>The menu bar's bottom edge in interface units; 0 while it's hidden. Panels start below it.</summary>
    private float TopBarBottom => _topBar.Visible ? _topBar.Position.Y + _topBar.Size.Y : 0;

    private void BuildTopBar()
    {
        TopBarItem Panel(string icon, string name, string? action, Control? panel, Action open, Func<string?>? blocked = null, Func<string?>? badge = null) =>
            new(icon, name, action, () => FromBar(panel, () => Toggle(panel, open)), () => panel is { Visible: true }, blocked ?? (() => panel is null ? "Part data failed to load" : null), badge);

        _topBar = new TopBar(new[]
        {
            new TopBarItem("pause", "Pause menu: save, load, quit", InputActions.ClosePanels, () => FromBar(null, OpenPause), () => _pause.Visible),
            new TopBarItem("editor", "Blueprint editor", InputActions.Editor, () => FromBar(null, OpenEditor), () => EditorOpen, () => _editor is null ? "Part data failed to load" : null),
            Panel("shipyard", "Shipyard", InputActions.Shipyard, _shipyardPanel, () => _shipyardPanel!.Open(),
                badge: () => _sector.Crafts.Any(c => c.IsShipyard && c.BuildQueue.Count > 0 && c.PowerShort && !c.LegacyFreePower) ? "A shipyard has no power" : null),
            Panel("fleet", "Fleet", InputActions.Fleet, _fleetPanel, () => _fleetPanel!.Open(), badge: () =>
                _sector.Crafts.Count(c => c.Job is { Active: true } job && job.Status.StartsWith("waiting")) is > 0 and var n ? $"{n} waiting" : null),
            Panel("swarm", "Swarms", null, _swarmPanel, () => _swarmPanel!.Open()),
            Panel("cargo", "Cargo", InputActions.Cargo, _cargoPanel, () => _cargoPanel!.Open(), () => _player is null ? "Fly a craft first" : null),
            Panel("trade", "Trade", null, _tradePanel, () => _tradePanel!.Open(), () => _player is { } p && _sector.DockedTrader(p) is not null ? null : "Dock at a civilization's trade station"),
            Panel("job", "Job", InputActions.Job, _jobPanel, () => _jobPanel!.Open(), () => _player is null ? "Fly a craft first" : null,
                () => _player?.Job is { Active: true } job && job.Status.StartsWith("waiting") ? job.Status : null),
            new TopBarItem("map", "Map", InputActions.Map, () => FromBar(_mapPanel, ToggleMap), () => _mapMode),
            Panel("research", "Research", InputActions.Research, _researchPanel, () => _researchPanel!.Open(), badge: () =>
                _sector.ResearchPoints > 0 && _catalog!.Research.Techs.Count(t => !_sector.IsTechUnlocked(t.Id) && _sector.ResearchBlocker(t.Id) is null) is > 0 and var n ? $"{n} can be researched" : null),
            Panel("power", "Power", InputActions.Power, _powerPanel, () => _powerPanel!.Open(),
                badge: () => _sector.Crafts.Count(c => SectorState.IsPlayers(c) && !c.IsWreck && c.PowerShort && !c.LegacyFreePower) is > 0 and var n ? $"{n} without power" : null),
            Panel("laws", "Laws and civilizations", InputActions.Laws, _lawsPanel, () => _lawsPanel!.Open(),
                badge: () => _sector.Factions.FirstOrDefault(f => !f.RaidGateId.IsNone) is { } raider ? $"The {raider.Name} are raiding" : null),
            Panel("life", "Native life", InputActions.Life, _lifePanel, () => _lifePanel!.Open()),
            Panel("message", "Message log", InputActions.Log, _logPanel, () => _logPanel!.Open(), badge: () => _logPanel!.NewEvents() is > 0 and var n ? $"{n} new" : null),
            Panel("milestones", "Milestones", InputActions.Milestones, _milestonesPanel, () => _milestonesPanel!.Open()),
            Panel("settings", "Settings", InputActions.Settings, _settingsPanel, () => _settingsPanel!.Open()),
            new TopBarItem("help", "Keys and help", InputActions.Help, () => FromBar(null, _hud.ToggleHelp), () => _hud.HelpOpen),
        }) { Visible = false };
        _uiRoot.AddChild(_topBar);
        _uiRoot.MoveChild(_topBar, 1); // above the HUD, below the menus
    }

    /// <summary>Presses a bar button; used with keys or a controller, focus moves on into the panel it opened.</summary>
    private void FromBar(Control? panel, Action press)
    {
        var withKeys = _navPanel == _topBar;
        if (withKeys) ExitPanelNav();
        press();
        if (withKeys && panel is { Visible: true }) EnterPanelNav(panel, fromMenu: false);
    }

    private void ToggleTopBar()
    {
        _settings.MenuBar = !_settings.MenuBar;
        _settings.Save();
        ShowStatus(_settings.MenuBar ? "Menu bar shown." : $"Menu bar hidden ({InputActions.Key1(InputActions.MenuBar)} shows it).");
    }
}
