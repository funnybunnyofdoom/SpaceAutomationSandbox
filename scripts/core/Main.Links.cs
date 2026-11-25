using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>Getting around (#100): names in panels open what they stand for, the message log, and panels that belong together.</summary>
public partial class Main
{
    private const int MaxMessages = 200;

    /// <summary>What the status line said this session, for the log.</summary>
    private readonly List<SectorEvent> _messages = new();
    private SectorState? _messagesOf;

    private IReadOnlyList<SectorEvent> Messages
    {
        get
        {
            if (!ReferenceEquals(_messagesOf, _sector)) _messages.Clear(); // another game
            _messagesOf = _sector;
            return _messages;
        }
    }

    private void Remember(string message)
    {
        if (_sector is null || InMenu) return;
        _ = Messages;
        if (_messages.Count > 0 && _messages[^1].Text == message) return; // said again
        _messages.Add(new SectorEvent(_sector.Tick, message, EventKind.Message));
        if (_messages.Count > MaxMessages) _messages.RemoveAt(0);
    }

    /// <summary>A name was clicked: what can be done with it.</summary>
    private void OpenLink(TargetRef target)
    {
        if (InMenu) return;
        if (target.IsNone || !_sector.Exists(target)) { ShowStatus("That's gone."); return; }
        var name = TargetName(target);
        var items = new List<(string, bool, Action)>();
        var craft = target.Craft.IsNone ? null : _sector.GetCraft(target.Craft);
        var you = craft is not null && craft == _player;
        if (!you && _player is not null) items.Add(($"Target {name}", target != CurrentTarget, () => { if (_sector.Exists(target)) ChooseTarget(target); }));
        items.Add(("Show on the map", true, () => ShowOnMap(target)));
        if (craft is not null && SectorState.IsPlayers(craft) && !craft.IsWreck)
        {
            if (!you) items.Add(("Fly this craft", true, () => { if (_sector.Exists(target)) Pilot(target.Craft); }));
            if (craft.IsShipyard) items.Add(("Open its shipyard", true, () => _shipyardPanel?.Open(target.Craft)));
            items.Add(("Rename...", true, () => AskRename(target.Craft)));
            items.Add(("Show in the fleet", true, () => _fleetPanel?.OpenAt(name)));
        }
        if (_player is { } p && !you && _sector.FlyToBlocker(p, target) is null)
            items.Add(($"Fly to {name}", true, () => { if (_player is { } pilot && _sector.Exists(target)) { ShowStatus(_sector.FlyTo(pilot.Id, target).Message); _changedSinceSave = true; } }));
        // With keys or a controller the menu opens at what has focus.
        _clickAt = _navPanel is not null && GetViewport().GuiGetFocusOwner() is { } focused ? focused.GlobalPosition + new Vector2(20, 20) : GetViewport().GetMousePosition();
        Callable.From(() => ShowPopup(items)).CallDeferred(); // after the click that asked for it
    }

    /// <summary>Asks for a craft's new name (#115).</summary>
    private void AskRename(CraftId id)
    {
        if (_sector.Crafts.FirstOrDefault(c => c.Id == id) is not { IsWreck: false } craft || !SectorState.IsPlayers(craft)) return;
        var sector = _sector;
        _confirm.AskText($"Rename {craft.Name}", craft.Name, SectorState.MaxCraftName, "Rename",
            name => ReferenceEquals(sector, _sector) ? sector.RenameBlocker(id, name) : "That game is closed.",
            name => { ShowStatus(sector.RenameCraft(id, name).Message); _changedSinceSave = true; });
    }

    private void ShowOnMap(TargetRef target)
    {
        if (!_sector.Exists(target)) return;
        if (!_mapMode) ToggleMap();
        if (_sector.SystemOf(target) is not { } star) return;
        ViewSystem(star);
        var place = !target.Craft.IsNone ? _sector.GetCraft(target.Craft).Position : _sector.GetPosition(target.Body);
        if (target.Body == star && target.Craft.IsNone) return; // the whole system
        _mapOffset = place - _sector.GetPosition(star);
        var radius = !target.Craft.IsNone ? 2_000 : _sector.GetBody(target.Body).RadiusM;
        _pixelsPerMetre = Mathf.Clamp((float)(60 / Math.Max(radius, 1)), MinPixelsPerMetre, MaxPixelsPerMetre);
        if (!target.Craft.IsNone && target.Craft == _player?.Id) return;
        if (_player is not null) ChooseTarget(target); // marked on the map
    }

    /// <summary>Swaps a panel for one it's grouped with, in the same place.</summary>
    private void SwapPanel(Control? from, Control? to, Action open)
    {
        if (from is null || to is null) return;
        var keys = _navPanel == from;
        var at = _openPanels.IndexOf(from);
        from.Visible = false;
        _openPanels.Remove(from);
        _openPanels.Remove(to);
        open();
        if (at >= 0) _openPanels.Insert(Math.Min(at, _openPanels.Count), to);
        if (keys) EnterPanelNav(to, fromMenu: false);
    }

    /// <summary>A close button in a panel's corner.</summary>
    private void AddCloseButton(Control panel)
    {
        if (panel is not PanelContainer || panel == _settingsPanel) return; // settings has its own
        var corner = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd, SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        corner.AddThemeConstantOverride("margin_right", 10);
        var close = new Button { Text = "×", FocusMode = Control.FocusModeEnum.None, TooltipText = "Close", CustomMinimumSize = new Vector2(28, 26) };
        close.Pressed += () =>
        {
            if (panel == _mapPanel && _mapMode) ToggleMap();
            panel.Visible = false;
        };
        corner.AddChild(close);
        panel.AddChild(corner);
    }
}
