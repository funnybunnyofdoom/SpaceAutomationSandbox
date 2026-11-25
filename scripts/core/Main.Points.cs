using System;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>Points of interest (#122) in the world and on the map, and the HUD's line about the one you're at.</summary>
public partial class Main
{
    private static readonly Color PointColor = new(0.95f, 0.8f, 0.45f);
    private static readonly Color FieldColor = new(0.45f, 0.75f, 1f);

    private DiscoveriesPanel? _discoveriesPanel;

    private void ChoosePoint(int point, string choice)
    {
        if (_player is not { } pilot) return;
        ShowStatus(_sector.ChoosePoint(pilot.Id, point, choice).Message);
        _changedSinceSave = true;
    }

    private bool ShowsPoints => !_mapMode || _mapPanel is not { ShowsDiscoveries: false };

    private void DrawPoints(double renderTimeS)
    {
        if (_sector.Points.Count == 0 || !ShowsPoints || (_mapMode ? MapStar() : _player is { } p0 ? _sector.StarAt(p0.Position) : null) is not { } star) return;
        var ui = _uiRoot.Scale.X;
        var font = Mathf.RoundToInt(13 * ui * _settings.TextScale);
        var half = GetViewportRect().Size / 2;
        var view = new Rect2(-half, half * 2).Grow(40);
        if (!_mapMode)
            foreach (var (point, field) in _sector.EnergyFields())
                if (_sector.PointPosition(point, renderTimeS) is { } at)
                    DrawArc(ToPixels(at), (float)field.RangeM * _pixelsPerMetre, 0, Mathf.Tau, 96, new Color(FieldColor, 0.35f + 0.15f * MathF.Sin(Time.GetTicksMsec() / 400f)), 2);
        foreach (var point in _sector.Points)
        {
            if (_sector.StarOf(point.Parent)?.Id != star.Id) continue;
            if (!point.Found)
            {
                // Hidden: only a hint, and only on the map.
                if (!_mapMode || _sector.PointHint(point, renderTimeS) is not { } hint) continue;
                var centre = ToPixels(hint.Centre);
                var radius = (float)hint.RadiusM * _pixelsPerMetre;
                if (!view.Grow(radius).HasPoint(centre)) continue;
                if (radius > 6) DashedArc(centre, radius, new Color(PointColor, 0.55f), 1.5f * ui);
                MapIcon("discovery", centre + (radius > 14 ? Vector2.Zero : new Vector2(0, -14) * ui), 16 * ui, new Color(1, 1, 1, 0.6f));
                continue;
            }
            if (_sector.PointPosition(point, point.CraftId.IsNone ? renderTimeS : _sector.TimeS) is not { } place) continue;
            var spot = !point.CraftId.IsNone && _sector.Exists(new TargetRef(point.CraftId, BodyId.None)) ? ToPixels(InterpolatedPosition(_sector.GetCraft(point.CraftId))) : ToPixels(place);
            if (!view.HasPoint(spot)) continue;
            var kind = _sector.KindOf(point);
            var muted = new Color(1, 1, 1, point.Done ? 0.4f : 1);
            if (kind?.Type == PointType.Ruin)
            {
                // On the ground, standing up from it.
                var up = (spot - ToPixels(_sector.GetPositionAt(point.Parent, renderTimeS))).Normalized();
                MapIcon("ruin", spot + up * 10 * ui, (_mapMode ? 16 : 24) * ui, muted);
                if (!point.Done || _mapMode) MapText(spot + up * 10 * ui + new Vector2(16, 4) * ui, point.Name, font, new Color(PointColor, muted.A));
            }
            else if (!point.Done)
            {
                if (_mapMode) MapIcon(DiscoveriesPanel.Icon(kind), spot + new Vector2(0, -12) * ui, 16 * ui);
                DrawArc(spot, Mathf.Max(10 * ui, 22 * ui * Mathf.Min(1, _pixelsPerMetre)), 0, Mathf.Tau, 32, new Color(PointColor, 0.8f), 1.5f * ui);
                if (_mapMode) MapText(spot + new Vector2(14, 4) * ui, point.Name, font, PointColor);
            }
        }
    }

    /// <summary>The line about a point of interest near the ship, or signs of one.</summary>
    private HudRow? PointRow(CraftState p)
    {
        PointOfInterest? nearest = null;
        var best = 8_000.0;
        var signs = false;
        foreach (var point in _sector.Points)
        {
            if (point.Done || _sector.KindOf(point) is not { } kind) continue;
            if (!point.Found)
            {
                signs |= kind.Type != PointType.Ruin && _sector.PointHint(point, _sector.TimeS) is { } hint && hint.Centre.DistanceTo(p.Position) < hint.RadiusM;
                continue;
            }
            if (_sector.PointPosition(point, _sector.TimeS) is { } at && at.DistanceTo(p.Position) is var d && d < best) (nearest, best) = (point, d);
        }
        if (nearest is null) return signs ? new HudRow("discovery", "Signs of something near here: keep looking", Severity.Muted, Tip: "The map shows roughly where") : null;
        var what = _sector.KindOf(nearest)!;
        var blocker = _sector.PointBlocker(p, nearest);
        var key = InputActions.Key1(InputActions.Discoveries);
        Action open = () => { if (_discoveriesPanel is { Visible: false }) Toggle(_discoveriesPanel, _discoveriesPanel.Open); };
        if (what.Type != PointType.Derelict && !nearest.Studied)
            return blocker is null ? new HudRow(DiscoveriesPanel.Icon(what), $"{(what.Type == PointType.Ruin ? "studying" : "scanning")} {nearest.Name}", Severity.Good, DiscoveriesPanel.Progress(nearest, what), Click: open)
                : new HudRow(DiscoveriesPanel.Icon(what), $"{nearest.Name}: {blocker}", Click: open);
        return new HudRow(DiscoveriesPanel.Icon(what), blocker is null ? $"{nearest.Name}: {key} to decide" : $"{nearest.Name}: {blocker}", blocker is null ? Severity.Good : Severity.Normal, Click: open);
    }

    /// <summary>How many found points can be decided from here.</summary>
    private int PointsToDecide() => _player is not { } p ? 0 : _sector.Points.Count(x => x.Found && !x.Done && _sector.PointChoices(p, x).Any(c => c.Blocker is null));

    /// <summary>Dev/test hook <c>--open-discoveries</c>: some points found, and the ship beside a derelict (or, with <c>=ruin</c>, landed at ruins).</summary>
    private void DemoPoints(string kind)
    {
        if (_player is null) return;
        var type = kind switch { "ruin" => PointType.Ruin, "anomaly" => PointType.Anomaly, _ => PointType.Derelict };
        _sector.DebugFindPoints(_player.Id, p => p.Id % 2 == 0 || _sector.KindOf(p)?.Type == type);
        if (_sector.Points.FirstOrDefault(p => p.Found && _sector.KindOf(p)?.Type == type) is { } at) _sector.DebugGoToPoint(_player.Id, at.Id);
        _discoveriesPanel?.Open();
    }
}
