using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>What the map adds to the world (#104): icons, freight routes, civilizations' borders and dangers; and the galaxy view.</summary>
public partial class Main
{
    private IReadOnlyList<MapRoute> _routes = Array.Empty<MapRoute>();
    private SectorState? _routesOf;
    private ulong _routesUntilMs;

    /// <summary>How far the map's middle is from its star; back to it when the whole system is fitted.</summary>
    private Vector2D _mapOffset = Vector2D.Zero;

    /// <summary>Keeps what's under a point (from the middle of the window) where it is while the map zooms.</summary>
    private void ZoomMapTo(Vector2 point, float before)
    {
        if (_galaxyView) return;
        var shift = 1.0 / before - 1.0 / _pixelsPerMetre;
        _mapOffset += new Vector2D(point.X * shift, point.Y * shift);
    }

    /// <summary>Dragging with the middle button moves the map.</summary>
    private bool DragMap(InputEvent @event)
    {
        if (!_mapMode || _galaxyView || @event is not InputEventMouseMotion motion || (motion.ButtonMask & MouseButtonMask.Middle) == 0) return false;
        _mapOffset -= new Vector2D(motion.Relative.X / _pixelsPerMetre, motion.Relative.Y / _pixelsPerMetre);
        return true;
    }

    private bool MapShows(CelestialBodyState body) => !_mapMode || _mapPanel is null || _mapPanel.Shows(body) || _player?.TargetBodyId == body.Id;
    private bool MapShows(CraftState craft) => !_mapMode || _mapPanel is null || _mapPanel.Shows(craft) || _player?.TargetCraftId == craft.Id;

    private IReadOnlyList<MapRoute> Routes()
    {
        if (ReferenceEquals(_routesOf, _sector) && Time.GetTicksMsec() < _routesUntilMs) return _routes;
        _routesOf = _sector;
        _routesUntilMs = Time.GetTicksMsec() + 1000;
        return _routes = _sector.MapRoutes();
    }

    private Vector2D PlaceOf(TargetRef target, double renderTimeS) =>
        !target.Craft.IsNone ? InterpolatedPosition(_sector.GetCraft(target.Craft)) : _sector.GetPositionAt(target.Body, renderTimeS);

    private static Color CivilizationColor(Faction f) => f.Disposition switch
    {
        Disposition.Hostile => Palette.Bad, Disposition.Wary => Palette.Warning, Disposition.Friendly => Palette.Good, _ => new Color(0.8f, 0.85f, 0.95f),
    };

    private void DashedArc(Vector2 centre, float radius, Color color, float width)
    {
        for (var i = 0; i < 48; i += 2) DrawArc(centre, radius, Mathf.Tau * i / 48, Mathf.Tau * (i + 1) / 48, 8, color, width);
    }

    private void DashedLine(Vector2 from, Vector2 to, Color color, float width, float dash = 10)
    {
        var length = from.DistanceTo(to);
        if (length < 1) return;
        var along = (to - from) / length;
        for (var d = 0f; d < length; d += dash * 2) DrawLine(from + along * d, from + along * Mathf.Min(length, d + dash), color, width);
    }

    private void MapIcon(string name, Vector2 at, float size, Color? tint = null)
    {
        if (UiIcons.Get(name) is { } icon) DrawTextureRect(icon, new Rect2((at - new Vector2(size, size) / 2).Round(), new Vector2(size, size)), false, tint ?? Colors.White);
    }

    private void MapText(Vector2 at, string text, int size, Color color)
    {
        DrawString(ThemeDB.FallbackFont, at + Vector2.One, text, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.85f));
        DrawString(ThemeDB.FallbackFont, at, text, HorizontalAlignment.Left, -1, size, color);
    }

    /// <summary>The system map's own marks, over the bodies and craft.</summary>
    private void DrawSystemMap(double renderTimeS)
    {
        if (!_mapMode || MapStar() is not { } star) return;
        var ui = _uiRoot.Scale.X;
        var font = Mathf.RoundToInt(13 * ui * _settings.TextScale);

        // A civilization's border, round the whole system.
        if (_sector.FactionAtStar(star.Id) is { } owner)
        {
            var extent = _sector.Bodies.Where(b => b.Kind != BodyKind.Star && b.Orbit is { } o && o.Parent == star.Id).Select(b => b.Orbit!.RadiusM + b.RadiusM).DefaultIfEmpty(100_000).Max() * 1.04;
            var centre = ToPixels(_sector.GetPositionAt(star.Id, renderTimeS));
            var radius = (float)extent * _pixelsPerMetre;
            DashedArc(centre, radius, new Color(CivilizationColor(owner), 0.7f), 2 * ui);
            var danger = owner.Disposition == Disposition.Hostile || !owner.RaidGateId.IsNone;
            var label = centre + new Vector2(-60 * ui, -radius - 10 * ui);
            MapIcon(danger ? "danger" : "star", label - new Vector2(14 * ui, 4 * ui), 16 * ui);
            MapText(label, $"{owner.Name}: {owner.Disposition.ToString().ToLowerInvariant()}{(owner.RaidGateId.IsNone ? "" : ", RAIDING")}", font, CivilizationColor(owner));
        }

        if (_mapPanel is { ShowsRoutes: true })
            foreach (var route in Routes())
            {
                if (!_sector.Exists(route.From) || !_sector.Exists(route.To) || _sector.SystemOf(route.From) != star.Id || _sector.SystemOf(route.To) != star.Id) continue;
                var from = ToPixels(PlaceOf(route.From, renderTimeS));
                var to = ToPixels(PlaceOf(route.To, renderTimeS));
                var length = from.DistanceTo(to);
                if (length < 6) continue;
                var stuck = route.Waiting * 2 > route.Ships;
                var color = stuck ? Palette.Warning : UiTheme.Accent;
                DrawLine(from, to, new Color(color, 0.45f), (1 + Mathf.Min(5, MathF.Log2(route.Ships))) * ui);
                // Marks travel from source to destination.
                var along = (to - from) / length;
                var step = 40 * ui;
                for (var d = (float)(Time.GetTicksMsec() / 1000.0 * 20 * ui % step); d < length; d += step) DrawCircle(from + along * d, 2 * ui, new Color(color, 0.9f));
                if (length > 60 * ui) MapText((from + to) / 2 + new Vector2(6, -4) * ui, $"{route.Ships:N0}{(stuck ? $" ({route.Waiting:N0} waiting)" : "")}", font, color);
            }

        // Stations and gates by kind; enemies and wrecks too.
        foreach (var (craft, at, radius) in _visibleCrafts)
        {
            if (radius > 12 * ui || !MapShows(craft) || craft == _player) continue;
            var enemy = _sector.IsEnemy(craft) && !craft.IsWreck;
            var name = enemy ? "danger" : craft.IsWreck ? "salvage" : SectorState.IsGate(craft) ? "gate" : craft.IsShipyard ? "shipyard" : craft.IsTradeStation ? "trade" : craft.Stats.TotalThrustN > 0 ? null : "orbit";
            if (name is null) continue;
            MapIcon(name, at, 16 * ui);
            if (SectorState.IsGate(craft) && !craft.IsWreck && GateState(craft) is var (state, _) && state != "open") MapText(at + new Vector2(10, 14) * ui, state, font, Palette.Warning);
        }
    }

    /// <summary>"open", "closed", "broken" or "unlinked", and the star it leads (or led) to.</summary>
    private (string State, BodyId Star) GateState(CraftState gate)
    {
        if (!gate.GateLostStarId.IsNone) return ("broken", gate.GateLostStarId);
        if (_sector.Crafts.FirstOrDefault(c => c.Id == gate.GateLinkId) is not { } far) return ("unlinked", BodyId.None);
        var star = _sector.StarAt(far.Position)?.Id ?? BodyId.None;
        return (far.IsWreck || gate.IsWreck ? "broken" : gate.GateClosed || far.GateClosed ? "closed" : "open", star);
    }

    /// <summary>
    /// The galaxy view (#21, #104): every known star system around the screen centre at its real direction and (scaled) distance,
    /// with its gate links (open, closed, broken), who claims it, dangers, what it holds and the ships on routes between systems.
    /// </summary>
    private void DrawGalaxy()
    {
        DrawRect(new Rect2(-GetViewportRect().Size, GetViewportRect().Size * 2), new Color(0.01f, 0.01f, 0.03f, 0.92f));
        var stars = _sector.Stars;
        if (stars.Count == 0) return;
        var centre = new Vector2D(stars.Average(s => _sector.GetPosition(s.Id).X), stars.Average(s => _sector.GetPosition(s.Id).Y));
        var far = stars.Max(s => _sector.GetPosition(s.Id).DistanceTo(centre));
        var size = GetViewportRect().Size;
        var ui = _uiRoot.Scale.X; // text follows the interface scale
        var font = Mathf.RoundToInt(14 * ui * _settings.TextScale);
        // Between the HUD and the panel.
        var left = (_hud.LeftWidth + 60) * ui;
        var right = (_panelsWidth + 60) * ui;
        var free = new Vector2(Mathf.Max(200, size.X - left - right), Mathf.Max(200, size.Y - 200 * ui));
        var middle = new Vector2((left - right) / 2, 10 * ui);
        var scale = far > 0 ? 0.42 * Mathf.Min(free.X, free.Y) / far : 0;
        Vector2 At(CelestialBodyState s)
        {
            var d = _sector.GetPosition(s.Id) - centre;
            return middle + new Vector2((float)(d.X * scale), (float)(d.Y * scale));
        }

        // Ships on routes between systems, joined by the pair of systems.
        var travel = new Dictionary<(uint, uint), int>();
        if (_mapPanel is { ShowsRoutes: true })
            foreach (var route in Routes())
                if (_sector.SystemOf(route.From) is { } a && _sector.SystemOf(route.To) is { } b && a != b)
                {
                    var pair = (Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));
                    travel[pair] = travel.GetValueOrDefault(pair) + route.Ships;
                }
        foreach (var gate in _sector.Crafts.Where(SectorState.IsGate))
        {
            var (state, to) = GateState(gate);
            if (_sector.StarAt(gate.Position) is not { } a || stars.FirstOrDefault(s => s.Id == to) is not { } b || a.Id == b.Id) continue;
            if (state != "broken" && a.Id.Value > b.Id.Value) continue; // each working pair once
            var (from, end) = (At(a), At(b));
            var note = (from + end) / 2 + new Vector2(8, -6) * ui;
            if (state == "open") DrawLine(from, end, new Color(0.75f, 0.5f, 1f, 0.8f), 2 * ui);
            else
            {
                var color = state == "broken" ? Palette.Bad : Palette.Warning;
                DashedLine(from, end, new Color(color, 0.8f), 2 * ui, 10 * ui);
                MapIcon(state == "broken" ? "danger" : "warn", (from + end) / 2, 16 * ui);
                MapText(note, state, font, color);
            }
            if (state == "open" && travel.TryGetValue((Math.Min(a.Id.Value, b.Id.Value), Math.Max(a.Id.Value, b.Id.Value)), out var ships)) MapText(note, $"{ships:N0} ships", font, UiTheme.Accent);
        }

        var here = _player is { } me ? _sector.StarAt(me.Position)?.Id : null;
        foreach (var star in stars)
        {
            var at = At(star);
            _starHits.Add((star.Id, at));
            var owner = _sector.FactionAtStar(star.Id);
            if (owner is not null) DashedArc(at, 30 * ui, new Color(CivilizationColor(owner), 0.8f), 2 * ui); // its border
            DrawGalaxyStar(star, at, (star.Id == here ? 12 : 9) * ui);
            if (star.Id == here) DrawArc(at, 18 * ui, 0, Mathf.Tau, 48, Colors.White, 1.5f * ui);
            var bodies = _sector.Bodies.Where(b => b.Id != star.Id && _sector.StarOf(b.Id)?.Id == star.Id).ToList();
            var craft = _sector.Crafts.Where(c => _sector.StarAt(c.Position)?.Id == star.Id).ToList();
            var enemies = craft.Count(c => !c.IsWreck && _sector.IsEnemy(c));
            var x = at.X - 60 * ui;
            var y = at.Y + 46 * ui;
            var type = star.StarClass switch { StarClass.Red => "red dwarf", StarClass.Blue => "blue star", _ => "yellow star" };
            MapText(new Vector2(x, y), $"{star.Name}{(star.Id == here ? "  (you are here)" : "")}, {type}", font, NameColor);
            y += font + 6 * ui;
            // Icons with counts.
            var row = x;
            void Count(string icon, string text, Color color)
            {
                MapIcon(icon, new Vector2(row + 8 * ui, y - 5 * ui), 16 * ui);
                MapText(new Vector2(row + 20 * ui, y), text, font, color);
                row += 28 * ui + ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1, font).X;
            }
            Count("planet", $"{bodies.Count}", NameColor);
            if (craft.Count(SectorState.IsPlayers) is > 0 and var mine) Count("fleet", $"{mine:N0}", NameColor);
            if (bodies.Any(b => b.DepositsKg.ContainsKey("exotic_ore"))) Count("mat_exotic_ore", "exotic ore", NameColor);
            if (bodies.Any(b => b.LifeFound)) Count("life", "life", Palette.Good);
            if (enemies > 0) Count("danger", $"{enemies} enem{(enemies == 1 ? "y" : "ies")}", Palette.Bad);
            if (owner is not null)
            {
                y += font + 6 * ui;
                row = x;
                Count(owner.Disposition == Disposition.Hostile || !owner.RaidGateId.IsNone ? "danger" : "star", $"{owner.Name}, {owner.Disposition.ToString().ToLowerInvariant()}{(owner.RaidGateId.IsNone ? "" : ", RAIDING")}", CivilizationColor(owner));
            }
        }
        MapText(new Vector2(-size.X / 2 + left - 30 * ui, size.Y / 2 - 20 * ui), $"Click a system to see its map ({InputActions.Key1(InputActions.Anchor)}: back)", font, NameColor);
    }
}
