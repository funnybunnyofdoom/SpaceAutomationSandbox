using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>The find panel, favourites and recent targets (#109), and the smart target keys (#110).</summary>
public partial class Main
{
    private const int MostRecent = 12;

    private FindPanel? _findPanel;

    /// <summary>Targets lately, newest first (this session).</summary>
    private readonly List<TargetRef> _recent = new();
    private SectorState? _recentOf;
    private (CraftId Pilot, CraftId Enemy) _fought;

    private (string Key, CraftId Pilot, TargetChoice Choice, ulong Until) _smart = ("", CraftId.None, new(Array.Empty<TargetRef>(), ""), 0);

    private IReadOnlyList<TargetRef> Recent => _recent;

    /// <summary>The same key again steps on; after a pause it chooses anew, the best first.</summary>
    private void SmartKey(string key, Func<CraftState, TargetChoice> choose)
    {
        if (_player is not { } p) return;
        var now = Time.GetTicksMsec();
        var again = _smart.Key == key && _smart.Pilot == p.Id && now < _smart.Until && _smart.Choice.Targets.Count > 0;
        var choice = again ? _smart.Choice : choose(p);
        _smart = (key, p.Id, choice, now + CycleHoldMs);
        var targets = choice.Targets.Where(t => t.Craft != p.Id || t.Craft.IsNone).ToList();
        var next = again ? _sector.NextTarget(targets, CurrentTarget, 1) : targets.FirstOrDefault(t => _sector.Exists(t), TargetRef.None);
        if (next.IsNone) { ShowStatus(choice.Targets.Count == 0 ? choice.Why : "Nothing left to target."); return; }
        ChooseTarget(next);
        var at = targets.IndexOf(next);
        ShowStatus($"Target: {TargetName(next)} ({(at == 0 || key == InputActions.TargetAlert ? AlertText(next) ?? choice.Why : $"{at + 1} of {targets.Count}")}).");
    }

    private string? AlertText(TargetRef target) => _smart.Key == InputActions.TargetAlert ? _sector.Alerts().FirstOrDefault(a => a.Target == target)?.Text.TrimEnd('.') : null;

    /// <summary>What needs you, worst first; also from the marks in the menu bar.</summary>
    private void TargetAlerts(params AlertKind[] kinds) => SmartKey(InputActions.TargetAlert, p =>
    {
        var alerts = _sector.Alerts().Where(a => a.Target.Craft != p.Id && (kinds.Length == 0 || kinds.Contains(a.Kind))).ToList();
        return new(alerts.Select(a => a.Target).Distinct().ToList(), alerts.Count == 0 ? "Nothing needs you." : alerts[0].Text.TrimEnd('.'));
    });

    /// <summary>Handles the target keys of #109 and #110; true when one was pressed.</summary>
    private bool FindingKey(Func<string, bool> pressed)
    {
        if (pressed(InputActions.Find)) Toggle(_findPanel, () => _findPanel!.Open());
        else if (pressed(InputActions.TargetGate)) SmartKey(InputActions.TargetGate, _sector.GateTargets); // before the plain key: they share one
        else if (pressed(InputActions.TargetDock)) SmartKey(InputActions.TargetDock, _sector.DockTargets);
        else if (pressed(InputActions.TargetSmart)) SmartKey(InputActions.TargetSmart, p => _sector.SmartTargets(p, _findPanel?.Ore));
        else if (pressed(InputActions.TargetEnemy)) SmartKey(InputActions.TargetEnemy, _sector.EnemyTargets);
        else if (pressed(InputActions.TargetWork)) SmartKey(InputActions.TargetWork, p => _sector.WorkTargets(p, _findPanel?.Ore));
        else if (pressed(InputActions.TargetAlert)) TargetAlerts();
        else if (pressed(InputActions.TargetBack)) TargetBack();
        else
        {
            for (var i = 0; i < SectorState.FavouriteSlots; i++)
            {
                if (pressed(InputActions.FavouriteStore(i))) { StoreFavourite(i); return true; }
                if (pressed(InputActions.FavouriteRecall(i))) { RecallFavourite(i); return true; }
            }
            return false;
        }
        return true;
    }

    private void StoreFavourite(int slot)
    {
        if (CurrentTarget.IsNone) { ShowStatus("Target something first, then keep it."); return; }
        _sector.SetFavourite(slot, CurrentTarget);
        _changedSinceSave = true;
        ShowStatus($"{TargetName(CurrentTarget)} is favourite {slot + 1}.");
    }

    private void RecallFavourite(int slot)
    {
        if (_sector.Favourites[slot] is not { } favourite) { ShowStatus($"Favourite {slot + 1} is empty ({InputActions.Key1(InputActions.FavouriteStore(slot))} keeps your target there)."); return; }
        if (!_sector.Exists(favourite.Target)) { ShowStatus($"{favourite.Name} (favourite {slot + 1}) is gone."); return; }
        if (favourite.Target.Craft == _player?.Id && !favourite.Target.Craft.IsNone) { ShowStatus($"You're flying {favourite.Name}."); return; }
        ChooseTarget(favourite.Target);
        ShowStatus($"Target: {TargetName(favourite.Target)}.");
    }

    private void TargetBack()
    {
        var last = _recent.FirstOrDefault(t => t != CurrentTarget && _sector.Exists(t) && (t.Craft.IsNone || t.Craft != _player?.Id), TargetRef.None);
        if (last.IsNone) { ShowStatus("No earlier target."); return; }
        ChooseTarget(last);
        ShowStatus($"Target: {TargetName(last)}.");
    }

    /// <summary>Each frame: notes a new target, whatever chose it, and takes the next enemy when one is destroyed (if that's on).</summary>
    private void WatchTarget()
    {
        if (!ReferenceEquals(_recentOf, _sector)) { _recent.Clear(); _recentOf = _sector; _fought = default; }
        if (_player is not { } p) return;
        var current = CurrentTarget;
        if (!current.IsNone && (_recent.Count == 0 || _recent[0] != current))
        {
            _recent.Remove(current);
            _recent.Insert(0, current);
            if (_recent.Count > MostRecent) _recent.RemoveAt(_recent.Count - 1);
        }
        var alive = !current.Craft.IsNone && _sector.Exists(current) && _sector.KindOf(_sector.GetCraft(current.Craft)) == TargetKind.Enemies;
        var lost = !_fought.Enemy.IsNone && _fought.Pilot == p.Id && !alive && (current.IsNone || current.Craft == _fought.Enemy)
                   && (!_sector.Exists(new TargetRef(_fought.Enemy, BodyId.None)) || _sector.GetCraft(_fought.Enemy).IsWreck);
        _fought = (p.Id, alive ? current.Craft : CraftId.None);
        if (!lost || !_settings.TargetNextEnemy || _sector.EnemyTargets(p) is not { Targets: { Count: > 0 } next }) return;
        ChooseTarget(next[0]);
        ShowStatus($"Destroyed. Next: {TargetName(next[0])}.");
    }
}
