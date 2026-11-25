using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>Hazards (#123) in the world, on the map and in the HUD.</summary>
public partial class Main
{
    private static readonly Color FlareColor = new(1f, 0.62f, 0.2f);
    private static readonly Color MeteorColor = new(0.72f, 0.6f, 0.5f);
    private static readonly Color CometColor = new(0.65f, 0.85f, 1f);

    /// <summary>What's forecast or under way in the system the ship is in, worst first.</summary>
    private IEnumerable<HudRow> HazardRows()
    {
        if (_player is not { } p || _sector.Hazards.Count == 0 || _sector.StarAt(p.Position) is not { } star) yield break;
        foreach (var hazard in _sector.Hazards.Where(h => h.Star == star.Id).OrderBy(h => h.Kind == HazardKind.Comet).ThenBy(h => h.StartTick))
        {
            if (_sector.HazardText(hazard) is not { } text) continue;
            var comet = hazard.Kind == HazardKind.Comet;
            var reached = _sector.InReach(hazard, p.Position);
            var safe = !comet && hazard.Begun && reached
                ? p.IsDocked ? ". You're docked: safe" : hazard.Kind == HazardKind.SolarFlare && _sector.InShadowAt(p.Position) ? ". You're in shadow: safe" : p.ShieldCharge > 0 ? ". Your shield takes it" : ". You're exposed"
                : "";
            yield return new HudRow(comet ? "comet" : hazard.Kind == HazardKind.SolarFlare ? "star" : "asteroid", text + safe,
                comet ? Severity.Normal : hazard.Begun && reached && safe.EndsWith("exposed") ? Severity.Bad : Severity.Warning,
                Tip: hazard.Kind switch
                {
                    HazardKind.SolarFlare => "Panels in the light give nothing and parts in the open wear. A body's shadow, docking or a shield protects.",
                    HazardKind.MeteorShower => "Rocks fly at craft there. Dock, move, raise shields, or shoot them.",
                    _ => "Rare ices to mine while it's here.",
                }, Link: new TargetRef(CraftId.None, hazard.Body.IsNone ? hazard.Star : hazard.Body));
        }
    }

    private void DrawHazards(double renderTimeS)
    {
        if (_sector.Hazards.Count == 0) return;
        if ((_mapMode ? MapStar() : _player is { } p ? _sector.StarAt(p.Position) : null) is not { } star) return;
        var ui = _uiRoot.Scale.X;
        foreach (var hazard in _sector.Hazards.Where(h => h.Star == star.Id))
            switch (hazard.Kind)
            {
                case HazardKind.SolarFlare when _mapMode:
                    var sun = ToPixels(_sector.GetPositionAt(star.Id, renderTimeS));
                    DashedArc(sun, 26 * ui, FlareColor, 2 * ui);
                    if (hazard.Begun) DashedArc(sun, 34 * ui, new Color(FlareColor, 0.6f), 2 * ui);
                    MapIcon("danger", sun + new Vector2(0, -30) * ui, 16 * ui);
                    break;
                case HazardKind.SolarFlare when hazard.Begun && _player is { } ship && !_sector.InShadowAt(ship.Position):
                    var size = GetViewportRect().Size; // a steady warm light, no flashing (#68)
                    DrawRect(new Rect2(-size / 2, size), new Color(FlareColor, 0.07f));
                    break;
                case HazardKind.MeteorShower when _sector.Exists(new TargetRef(CraftId.None, hazard.Body)):
                    if (!_mapMode) break;
                    var centre = ToPixels(_sector.GetPositionAt(hazard.Body, renderTimeS));
                    var radius = (float)(_sector.GetBody(hazard.Body).RadiusM + SectorState.MeteorZoneM) * _pixelsPerMetre;
                    if (radius > 8) DashedArc(centre, radius, new Color(Palette.Warning, hazard.Begun ? 0.9f : 0.5f), 2 * ui);
                    MapIcon("danger", centre + new Vector2(0, -Mathf.Max(radius, 14 * ui)), 16 * ui);
                    break;
                case HazardKind.Comet when hazard.Begun && _sector.Exists(new TargetRef(CraftId.None, hazard.Body)):
                    DrawComet(_sector.GetBody(hazard.Body), star, renderTimeS, ui);
                    break;
            }
        if (_mapMode) return;
        foreach (var meteor in _sector.Meteors)
        {
            var at = ToPixels(meteor.Position - meteor.Velocity * ((1 - _warp.Alpha) * FixedStepClock.TickSeconds));
            var radius = Mathf.Max(2.5f, (float)SectorState.MeteorRadiusM * _pixelsPerMetre);
            var back = new Vector2((float)meteor.Velocity.X, (float)meteor.Velocity.Y).Normalized() * -Mathf.Max(14f, radius * 4);
            DrawLine(at, at + back, new Color(MeteorColor, 0.4f), Mathf.Max(1f, radius * 0.7f));
            DrawCircle(at, radius, MeteorColor);
        }
    }

    /// <summary>A tail away from the star, and its mark on the map.</summary>
    private void DrawComet(CelestialBodyState comet, CelestialBodyState star, double renderTimeS, float ui)
    {
        var at = ToPixels(_sector.GetPositionAt(comet.Id, renderTimeS));
        var away = (at - ToPixels(_sector.GetPositionAt(star.Id, renderTimeS))).Normalized();
        if (!away.IsFinite()) return;
        var radius = Mathf.Max(3f, (float)comet.RadiusM * _pixelsPerMetre);
        var length = Mathf.Max(60 * ui, (float)comet.RadiusM * 14 * _pixelsPerMetre);
        var side = new Vector2(-away.Y, away.X);
        var pale = new Color(CometColor, 0);
        DrawPolygon(new[] { at + side * radius, at + away * length + side * radius * 2.2f, at + away * length - side * radius * 2.2f, at - side * radius },
            new[] { new Color(CometColor, 0.35f), pale, pale, new Color(CometColor, 0.35f) });
        if (_mapMode) MapIcon("comet", at, 16 * ui);
    }

    /// <summary>Dev/test hook <c>--hazard=flare|meteors|comet</c>: one now, where the ship is.</summary>
    private void DemoHazard(string kind)
    {
        if (_player is not { } p) return;
        var which = kind switch { "comet" => HazardKind.Comet, "meteors" => HazardKind.MeteorShower, _ => HazardKind.SolarFlare };
        var hazard = _sector.DebugHazard(which);
        _sector.Step();
        if (which == HazardKind.Comet && hazard is { Begun: true })
        {
            var comet = _sector.GetBody(hazard.Body);
            var at = _sector.GetPosition(comet.Id);
            p.Position = p.PreviousPosition = at + new Vector2D(comet.RadiusM + 300, 0);
            p.Velocity = _sector.GetVelocityAt(comet.Id, _sector.TimeS);
        }
        if (which == HazardKind.MeteorShower) for (var i = 0; i < 12 * FixedStepClock.TicksPerSecond; i++) _sector.Step();
    }
}
