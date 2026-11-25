using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Targeting: stepping through one kind of thing, nearest first (#107), and pointing and clicking (#108). What can be clicked is what
/// was drawn this frame, so it stays cheap with thousands of craft in the game.
/// </summary>
public partial class Main
{
    private const int CycleLimit = 50, PickListLimit = 12;
    private const ulong CycleHoldMs = 6_000;
    private const float PickSlackPx = 10;

    private TargetKind _cycleKind = TargetKind.Stations;
    private TargetList _cycle = new(Array.Empty<TargetRef>(), 0);
    private (CraftId Craft, BodyId? Star, TargetKind Kind) _cycleOf;
    private ulong _cycleUntilMs;

    private static string KindName(TargetKind kind) => kind switch { TargetKind.Ships => "My ships", TargetKind.Others => "Other ships", _ => kind.ToString() };

    private TargetRef CurrentTarget => _player is { } p ? new TargetRef(p.TargetCraftId, p.TargetBodyId) : TargetRef.None;

    /// <summary>Steps to the next (or previous) target of the kind. The order holds while you keep stepping.</summary>
    private void CycleTarget(int step)
    {
        if (_player is not { } p) return;
        var of = (p.Id, _sector.StarAt(p.Position)?.Id, _cycleKind);
        if (Time.GetTicksMsec() > _cycleUntilMs || of != _cycleOf)
        {
            _cycle = _sector.TargetsOf(p, _cycleKind, CycleLimit);
            _cycleOf = of;
        }
        _cycleUntilMs = Time.GetTicksMsec() + CycleHoldMs;
        var next = _sector.NextTarget(_cycle.Targets, CurrentTarget, step);
        if (next.IsNone) { ShowStatus($"No {KindName(_cycleKind).ToLowerInvariant()} in this system ({InputActions.Key1(InputActions.TargetKindNext)}: another kind)."); return; }
        ChooseTarget(next);
    }

    /// <summary>Goes to the next kind that has something in it, and targets its nearest.</summary>
    private void NextTargetKind()
    {
        if (_player is not { } p) return;
        var kinds = Enum.GetValues<TargetKind>();
        for (var i = 1; i <= kinds.Length; i++)
        {
            var kind = kinds[((int)_cycleKind + i) % kinds.Length];
            if (_sector.TargetsOf(p, kind, 1).Total == 0) continue;
            _cycleKind = kind;
            _cycleUntilMs = 0;
            CycleTarget(1);
            // Already on one of them: make sure it's the nearest.
            if (_cycle.Targets.Count > 0) ChooseTarget(_cycle.Targets[0]);
            return;
        }
        ShowStatus("Nothing to target in this system.");
    }

    private void ChooseTarget(TargetRef target)
    {
        if (_player is not { } p || target.Craft == p.Id && !target.Craft.IsNone) return;
        _sector.SetTarget(p.Id, target);
        _changedSinceSave = true;
    }

    private void ClearTarget()
    {
        if (CurrentTarget.IsNone) return;
        ChooseTarget(TargetRef.None);
        ShowStatus("Target cleared.");
    }

    /// <summary>What's on screen closest to the way the ship points: a target without a mouse.</summary>
    private void TargetAhead()
    {
        if (_player is not { } p) return;
        var forward = new Vector2((float)p.Forward.X, (float)p.Forward.Y);
        var at = ToPixels(InterpolatedPosition(p));
        var best = TargetRef.None;
        var bestCos = 0.8f;
        foreach (var (target, where, _) in Pickable())
        {
            var to = where - at;
            if (to.Length() < 4) continue;
            var cos = forward.Dot(to.Normalized());
            if (cos > bestCos) { bestCos = cos; best = target; }
        }
        if (best.IsNone) ShowStatus("Nothing in view ahead.");
        else ChooseTarget(best);
    }

    /// <summary>The Target card's line: which kind Tab steps through, and where in it you are.</summary>
    private HudRow CycleRow()
    {
        var at = -1;
        var current = CurrentTarget;
        for (var i = 0; i < _cycle.Targets.Count; i++) if (_cycle.Targets[i] == current) at = i;
        var fresh = _cycleOf.Kind == _cycleKind && at >= 0;
        var place = fresh ? $"  {at + 1} of {_cycle.Total:N0}{(_cycle.Total > CycleLimit ? $" (nearest {CycleLimit})" : "")}" : "";
        return new HudRow("target", $"{InputActions.Key1(InputActions.TargetCycle)}: {KindName(_cycleKind)}{place}", Severity.Muted,
            Tip: $"{InputActions.Key1(InputActions.TargetCycle)} next, {InputActions.Key1(InputActions.TargetPrevious)} previous, nearest first.\n{InputActions.Key1(InputActions.TargetKindNext)} or click: another kind.\n{InputActions.Key1(InputActions.TargetClear)}: no target.",
            Click: _nextKind ??= NextTargetKind);
    }

    private Action? _nextKind;

    // --- Pointing and clicking ---

    /// <summary>Edge arrows drawn this frame, and star marks in the galaxy view.</summary>
    private readonly List<(TargetRef Target, Vector2 At)> _arrowHits = new();
    private readonly List<(BodyId Star, Vector2 At)> _starHits = new();
    private readonly List<(SwarmState Swarm, Rect2 Label)> _swarmHits = new();
    private TargetRef _hover = TargetRef.None;
    private SwarmState? _hoverSwarm;
    private PopupMenu? _popup;
    private readonly List<Action> _popupActions = new();

    /// <summary>What's drawn and can be targeted: where, and how big, in pixels from the centre.</summary>
    private IEnumerable<(TargetRef Target, Vector2 At, float Radius)> Pickable()
    {
        foreach (var (craft, at, radius) in _visibleCrafts)
            if (craft != _player) yield return (TargetRef.Of(craft), at, radius);
        var renderTimeS = RenderTimeS;
        var half = GetViewportRect().Size / 2;
        var view = new Rect2(-half, half * 2);
        foreach (var body in _sector.Bodies)
        {
            if (!MapShows(body)) continue;
            var at = ToPixels(_sector.GetPositionAt(body.Id, renderTimeS));
            var radius = (float)body.RadiusM * _pixelsPerMetre;
            if (view.Grow(radius).HasPoint(at)) yield return (TargetRef.Of(body), at, radius);
        }
    }

    /// <summary>Set by the --point dev hook, so a check doesn't depend on where the real mouse is.</summary>
    private Vector2? _devPointer;

    /// <summary>Where the last click was (window pixels): menus open there.</summary>
    private Vector2 _clickAt;

    private Vector2 MouseFromCentre => (_devPointer ?? GetViewport().GetMousePosition()) - GetViewportRect().Size / 2;

    /// <summary>The things under a point, best first: an edge arrow, then the smallest.</summary>
    private List<TargetRef> PickAt(Vector2 point)
    {
        var slack = PickSlackPx * Mathf.Max(1, _uiRoot.Scale.X);
        var arrow = _arrowHits.Where(a => a.At.DistanceTo(point) < slack * 1.6f).OrderBy(a => a.At.DistanceTo(point)).Select(a => a.Target).FirstOrDefault(TargetRef.None);
        if (!arrow.IsNone) return new List<TargetRef> { arrow };
        var hits = Pickable().Where(h => h.At.DistanceTo(point) <= Mathf.Max(h.Radius, 4) + (h.Radius < slack ? slack : 2))
            .OrderBy(h => h.Target.Craft.IsNone ? 1 : 0).ThenBy(h => Mathf.Max(h.Radius, slack)).ThenBy(h => h.At.DistanceTo(point)).ToList();
        // A craft big enough to point at precisely, with nothing smaller near it, is what was meant; so is one craft over a body.
        var crafts = hits.Where(h => !h.Target.Craft.IsNone).ToList();
        if (crafts.Count == 1) return new List<TargetRef> { crafts[0].Target };
        if (crafts.Count == 0) return hits.Take(1).Select(h => h.Target).ToList();
        if (crafts[0].Radius >= slack && crafts[0].At.DistanceTo(point) <= crafts[0].Radius && crafts[1].Radius > 1.5f * crafts[0].Radius)
            return new List<TargetRef> { crafts[0].Target };
        return hits.Select(h => h.Target).ToList();
    }

    private string TargetName(TargetRef target) =>
        !target.Craft.IsNone ? _sector.GetCraft(target.Craft).Name : _sector.GetBody(target.Body).Name;

    private string TargetLine(TargetRef target)
    {
        var distance = _player is not { } p ? "" : "  " + FormatDistance(Math.Max(0, !target.Craft.IsNone
            ? _sector.GetCraft(target.Craft).Position.DistanceTo(p.Position) - _sector.GetCraft(target.Craft).CollisionRadiusM
            : _sector.GetPosition(target.Body).DistanceTo(p.Position) - _sector.GetBody(target.Body).RadiusM));
        if (target.Craft.IsNone && _sector.GetBody(target.Body).OriginalName is { } was) distance += $"  (originally {was})"; // #109
        return TargetLabel(target) + distance;
    }

    /// <summary>What it is, without how far.</summary>
    private string TargetLabel(TargetRef target)
    {
        if (target.Craft.IsNone) return Describe(_sector.GetBody(target.Body));
        var craft = _sector.GetCraft(target.Craft);
        var kind = _sector.KindOf(craft) switch
        {
            TargetKind.Stations => SectorState.IsGate(craft) ? "warp gate" : craft.IsShipyard ? "shipyard" : "station",
            TargetKind.Ships => craft.Job is { Active: true } job ? $"{job.Kind}: {job.Status}" : "your ship",
            TargetKind.Enemies => "ENEMY",
            TargetKind.Wrecks => "wreck",
            _ => craft.IsTradeStation ? "trade station" : "other ship",
        };
        return $"{craft.Name} ({kind})";
    }

    /// <summary>The mouse may point into the world: no menu or editor, and not over a panel.</summary>
    private bool CanPoint => !InMenu && !ModalOpen && !EditorOpen && !_newGame.Visible && !_hud.HelpOpen && _settingsPanel is not { Visible: true } && _popup is not { Visible: true };

    private void UpdateHover()
    {
        _hover = TargetRef.None;
        _hoverSwarm = null;
        if (!CanPoint || _galaxyView && _mapMode || _devPointer is null && GetViewport().GuiGetHoveredControl() is not null) return;
        var point = MouseFromCentre;
        _hover = PickAt(point).FirstOrDefault(TargetRef.None);
        if (_hover.IsNone) _hoverSwarm = _swarmHits.Where(s => s.Label.HasPoint(point)).Select(s => s.Swarm).FirstOrDefault();
    }

    /// <summary>A ring round what the mouse is over, and what it is.</summary>
    private void DrawHover(double renderTimeS)
    {
        if (_hover.IsNone && _hoverSwarm is null || !CanPoint || !_hover.IsNone && !_sector.Exists(_hover)) return;
        string text;
        if (_hover.IsNone) text = $"{_hoverSwarm!.Name}: {_hoverSwarm.Count:N0} ships, {_hoverSwarm.Status}";
        else
        {
            var at = !_hover.Craft.IsNone ? ToPixels(InterpolatedPosition(_sector.GetCraft(_hover.Craft))) : ToPixels(_sector.GetPositionAt(_hover.Body, renderTimeS));
            var radius = (float)(!_hover.Craft.IsNone ? _sector.GetCraft(_hover.Craft).CollisionRadiusM : _sector.GetBody(_hover.Body).RadiusM) * _pixelsPerMetre;
            if (_hover == CurrentTarget) return; // it has its own marker, and its card
            if (radius < GetViewportRect().Size.X / 2) DrawArc(at, Mathf.Max(radius, 8) + 3, 0, Mathf.Tau, radius > 60 ? 256 : 64, new Color(UiTheme.Accent, 0.8f), 1.5f);
            text = TargetLine(_hover);
        }
        var font = ThemeDB.FallbackFont;
        var size = Mathf.RoundToInt(13 * _uiRoot.Scale.X * _settings.TextScale);
        var box = font.GetStringSize(text, HorizontalAlignment.Left, -1, size) + new Vector2(12, 6);
        var half = GetViewportRect().Size / 2;
        var corner = MouseFromCentre + new Vector2(18, 18);
        var left = -half.X + (_hud.Shown ? _hud.LeftWidth + 8 : 0) * _uiRoot.Scale.X;
        var right = half.X - _panelsWidth * _uiRoot.Scale.X; // not under a panel
        corner = new Vector2(Mathf.Clamp(corner.X, left, Mathf.Max(left, right - box.X)), Mathf.Min(corner.Y, half.Y - box.Y));
        DrawRect(new Rect2(corner, box), new Color(0.02f, 0.04f, 0.08f, 0.85f));
        DrawRect(new Rect2(corner, box), UiTheme.Edge, filled: false);
        DrawString(font, corner + new Vector2(6, 3 + font.GetAscent(size)), text, HorizontalAlignment.Left, -1, size, Colors.White);
    }

    /// <summary>A click in the world; true when it was used. Clicking nothing keeps the target (the creator's choice).</summary>
    private bool WorldClick(InputEventMouseButton click)
    {
        if (!click.Pressed || click.ButtonIndex is not (MouseButton.Left or MouseButton.Right) || !CanPoint) return false;
        _clickAt = click.Position;
        var point = click.Position - GetViewportRect().Size / 2;
        if (_mapMode && _galaxyView)
        {
            var star = _starHits.Where(s => s.At.DistanceTo(point) < 24 * _uiRoot.Scale.X).OrderBy(s => s.At.DistanceTo(point)).Select(s => (BodyId?)s.Star).FirstOrDefault();
            if (click.ButtonIndex == MouseButton.Left && star is { } id) { ViewSystem(id); return true; }
            return false;
        }
        if (_player is null) return false;
        var hits = PickAt(point);
        if (click.ButtonIndex == MouseButton.Right) { OpenTargetMenu(hits); return true; }
        if (hits.Count == 1) ChooseTarget(hits[0]);
        else if (hits.Count > 1) OpenPickList(hits, ChooseTarget);
        else if (_swarmHits.Where(s => s.Label.HasPoint(point)).Select(s => s.Swarm).FirstOrDefault() is { } swarm && _sector.Exists(new TargetRef(CraftId.None, swarm.SourceBodyId)))
        {
            // A swarm is one flow, not craft: its target is where it mines.
            ChooseTarget(new TargetRef(CraftId.None, swarm.SourceBodyId));
            ShowStatus($"{swarm.Name} mines here: {_sector.GetBody(swarm.SourceBodyId).Name} targeted.");
        }
        else return false;
        return true;
    }

    /// <summary>Several things under the mouse: a list to pick from.</summary>
    private void OpenPickList(List<TargetRef> hits, Action<TargetRef> pick)
    {
        var items = hits.Take(PickListLimit).Select(h => (TargetLine(h), true, (Action)(() => { if (_sector.Exists(h)) pick(h); }))).ToList();
        if (hits.Count > PickListLimit) items.Add(($"{hits.Count - PickListLimit} more: zoom in", false, () => { }));
        ShowPopup(items);
    }

    /// <summary>The right-click menu (the creator's choice: target, and fly to).</summary>
    private void OpenTargetMenu(List<TargetRef> hits)
    {
        if (hits.Count > 1) { OpenPickList(hits, h => Callable.From(() => OpenTargetMenu(new List<TargetRef> { h })).CallDeferred()); return; }
        var items = new List<(string, bool, Action)>();
        if (hits.Count == 1)
        {
            var target = hits[0];
            items.Add(($"Target {TargetName(target)}", target != CurrentTarget, () => { if (_sector.Exists(target)) ChooseTarget(target); }));
            var blocked = _sector.FlyToBlocker(_player!, target);
            items.Add(($"Fly to {TargetName(target)}{(blocked is null ? "" : $"   ({blocked.TrimEnd('.')})")}", blocked is null, () =>
            {
                if (_player is not { } p) return;
                ShowStatus(_sector.FlyTo(p.Id, target).Message);
                _changedSinceSave = true;
            }));
        }
        if (!CurrentTarget.IsNone) items.Add(("Clear target", true, ClearTarget));
        if (items.Count > 0) ShowPopup(items);
    }

    private void ShowPopup(List<(string Text, bool Enabled, Action Action)> items)
    {
        if (_popup is null)
        {
            _popup = new PopupMenu();
            _uiRoot.AddChild(_popup);
            _popup.IdPressed += id => { if (id < _popupActions.Count) _popupActions[(int)id](); };
        }
        _popup.Clear();
        _popupActions.Clear();
        foreach (var (text, enabled, action) in items)
        {
            _popup.AddItem(text, _popupActions.Count);
            _popup.SetItemDisabled(_popup.ItemCount - 1, !enabled);
            _popupActions.Add(action);
        }
        _popup.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(15 * _uiRoot.Scale.X * _settings.TextScale));
        _popup.ResetSize();
        var at = _clickAt;
        if (!GetViewport().GuiEmbedSubwindows) at += GetWindow().Position;
        _popup.Position = (Vector2I)at;
        _popup.Popup();
    }
}
