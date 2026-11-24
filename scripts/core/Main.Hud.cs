using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>The graphical HUD's contents (#101): amounts become bars, every other line gets an icon, and arrows at the edge point off screen.</summary>
public partial class Main
{
    /// <summary>The scale of the approach speed bar; the mark on it is the safe landing speed.</summary>
    private const double ApproachScaleMps = 30;

    /// <summary>Icons by how a line starts; the first match counts.</summary>
    private static readonly (string Start, string Icon)[] LineIcons =
    {
        ("KIT", "kit"), ("DAMAGE", "hull"), ("SHIPYARD WORK", "shipyard"), ("TUGS", "docked"), ("WARP", "gate"), ("system:", "star"), ("gravity", "gravity"),
        ("FLYING TO", "target"), ("CRUISE", "speed"), ("cruise", "speed"), ("AUTOPILOT (mining", "mining"), ("AUTOPILOT (freight", "freight"), ("AUTOPILOT (survey", "survey"),
        ("AUTOPILOT (salvage", "salvage"), ("AUTOPILOT (trade", "trade"), ("AUTOPILOT", "job"), ("MINING", "mining"), ("ANCHORED", "landed"), ("LANDED", "landed"),
        ("DOCKED", "docked"), ("PARKED", "orbit"), ("On a fixed orbit", "orbit"), ("weapons", "weapon"), ("shield", "shield"), ("antimatter", "mat_antimatter"),
        ("quantum", "speed"), ("salvage", "salvage"), ("drives", "mat_fuel"), ("crew", "crew"), ("power", "power"), ("Research", "research"),
        ("WRECK", "danger"), ("site", "survey"), ("nearest", "planet"), ("slag", "mat_slag"), ("kits", "kit"), ("TIME", "clock"), ("DEBUG", "debug"),
    };

    private static readonly string[] BadWords = { "DANGER", "WRECK", "FAILING", "DAMAGE" };
    private static readonly string[] WarningWords = { "NO POWER", "DRY", "STOPPED", "NO SHELLS", "NO MISSILES", "ANGRY", "waiting", "no power", "not enough" };

    private static HudRow Row(string line)
    {
        var severity = BadWords.Any(line.Contains) ? Severity.Bad : WarningWords.Any(line.Contains) ? Severity.Warning : Severity.Normal;
        var icon = LineIcons.FirstOrDefault(i => line.StartsWith(i.Start, StringComparison.Ordinal)).Icon;
        return new HudRow(severity == Severity.Bad ? "danger" : severity == Severity.Warning ? "warn" : icon ?? "idle", line, severity);
    }

    private HudCardData StatusCard(string text)
    {
        var rows = Lines(text).Select(l => Row(l) is { Icon: "idle" } plain ? plain with { Icon = "message" } : Row(l)).ToList();
        return rows.Count == 0 ? HudCardData.None : new HudCardData("Messages", rows);
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The ship you fly: what it's doing (<paramref name="lines"/>) with its amounts as bars.</summary>
    private HudCardData ShipCard(CraftState p, string lines)
    {
        var rows = new List<HudRow>();
        var heading = Mathf.PosMod(Mathf.RadToDeg((float)p.HeadingRad), 360f);
        rows.Add(new HudRow("speed", $"{p.SpeedMps:0.0} m/s    heading {heading:000}°"));
        rows.Add(new HudRow("gravity", $"{p.Stats.TotalMassKg / 1000:0.0} t    {p.Stats.TotalThrustN / 1000:0} kN    {p.Stats.TotalThrustN / p.MassKg:0.0} m/s²", Severity.Muted, Tip: "Mass, thrust and acceleration"));
        if (!p.IsLanded && !p.IsDocked && !p.IsOnRails && !p.Cruising && _sector.GravitySource(p.Position) is { } pull)
        {
            var speed = _sector.GetRelativeMotion(p.Id, pull.Id).RelativeSpeedMps;
            rows.Add(new HudRow("landed", $"{speed:0.0} m/s to {pull.Name}{(speed > SectorState.SafeImpactSpeedMps ? "  too fast to land" : "")}",
                speed > SectorState.SafeImpactSpeedMps ? Severity.Warning : Severity.Good, speed / ApproachScaleMps, SectorState.SafeImpactSpeedMps / ApproachScaleMps,
                $"Speed relative to {pull.Name}. Land under the mark ({SectorState.SafeImpactSpeedMps:0} m/s)."));
        }
        var power = SectorState.PowerStatus(p);
        if (p.Stats.EnergyCapacityKJ > 0)
            rows.Add(new HudRow("battery", $"{p.StoredEnergyKJ / 1000:0.0} / {p.Stats.EnergyCapacityKJ / 1000:0} MJ    +{p.Stats.PowerGenerationKW * p.SolarFactor:0} kW solar",
                p.PowerShort ? Severity.Bad : p.StoredEnergyKJ < 0.2 * p.Stats.EnergyCapacityKJ ? Severity.Warning : Severity.Normal, p.StoredEnergyKJ / p.Stats.EnergyCapacityKJ, Tip: power));
        if (p.HasCargoLimit && p.CargoCapacityKg > 0)
        {
            var held = p.Inventory.Kg.Where(m => m.Value > 0).OrderByDescending(m => m.Value).Select(m => $"{m.Key} {m.Value / 1000.0:0.##} t");
            rows.Add(new HudRow("cargo", $"{p.CargoKg / 1000.0:0.0} / {p.CargoCapacityKg / 1000.0:0} t cargo", Severity.Normal, (double)p.CargoKg / p.CargoCapacityKg, Tip: "Cargo: " + string.Join(", ", held)));
        }
        else if (p.CargoKg > 0) rows.Add(new HudRow("cargo", $"stores {p.CargoKg / 1000.0:0.0} t"));
        if (p.Cruising || p.Inventory[SectorState.FuelMaterial] > 0)
        {
            var fuel = p.Inventory[SectorState.FuelMaterial];
            var minutes = fuel / Math.Max(SectorState.CruiseFuelKgPerS(p), 1e-9) / 60;
            rows.Add(new HudRow("mat_fuel", $"{fuel / 1000.0:0.00} t fuel    {minutes:0} min cruise", minutes < 2 ? Severity.Warning : Severity.Normal, minutes / 30, Tip: "Full bar: 30 minutes of cruise"));
        }
        if (p.Stats.ShieldCapacity > 0)
            rows.Add(new HudRow("shield", $"shield {p.ShieldCharge / p.Stats.ShieldCapacity * 100:0}%", p.ShieldPowerless ? Severity.Bad : Severity.Normal, p.ShieldCharge / p.Stats.ShieldCapacity));
        if (SectorState.NeedsRepair(p))
        {
            var whole = (p.Stats.CellCount - p.PartHealth.Values.Sum(h => 1 - h)) / Math.Max(1, p.Stats.CellCount + SectorState.MissingParts(p));
            rows.Add(new HudRow("hull", $"hull {whole * 100:0}%", whole < 0.6 ? Severity.Bad : Severity.Warning, whole));
        }
        if (p.Stats.AntimatterCapacityKg > 0)
            rows.Add(new HudRow("mat_antimatter", $"antimatter {p.Inventory[SectorState.AntimatterMaterial]} / {p.Stats.AntimatterCapacityKg:0} kg", p.ContainmentFailS > 0 ? Severity.Bad : Severity.Normal,
                p.Inventory[SectorState.AntimatterMaterial] / p.Stats.AntimatterCapacityKg));
        if (p.Stats.QuantumDriveKW > 0)
            rows.Add(new HudRow("speed", $"quantum charge {p.QuantumChargeKJ / SectorState.QuantumChargeNeededKJ(p) * 100:0}%", Severity.Normal, p.QuantumChargeKJ / SectorState.QuantumChargeNeededKJ(p)));
        // The rest as it was, with an icon: what a bar already says is left out unless something is wrong.
        foreach (var line in Lines(lines))
        {
            var shown = line.StartsWith("power ") && line.Contains("MJ") && p.Stats.EnergyCapacityKJ > 0 || line.StartsWith("gravity ")
                        || line.StartsWith("shield ") || line.StartsWith("antimatter ") && p.Stats.AntimatterCapacityKg > 0;
            if (shown && !line.Contains("NO POWER") && !line.Contains("DANGER")) continue;
            rows.Add(line.StartsWith("power ") ? new HudRow("warn", "NO POWER: not everything can run", Severity.Bad, Tip: line) : Row(line));
        }
        return new HudCardData($"{p.Name}  ·  {p.Design.Name}", rows, p.Design);
    }

    /// <summary>Shipyards (building, storage, what's wrong), gates and research.</summary>
    private HudCardData StationsCard(string lines)
    {
        var rows = new List<HudRow>();
        var yards = _sector.Crafts.Where(c => c.IsShipyard).GroupBy(c => c.Name + ":").ToDictionary(g => g.Key, g => g.First());
        foreach (var line in Lines(lines))
        {
            var parts = line.Split("   ", StringSplitOptions.RemoveEmptyEntries);
            var yard = yards.FirstOrDefault(y => parts[0].StartsWith(y.Key, StringComparison.Ordinal)).Value;
            if (yard is null)
            {
                rows.Add(parts[0].Contains("warp gate") ? new HudRow("gate", line) : Row(line));
                continue;
            }
            var wrong = WarningWords.Any(line.Contains);
            if (yard.BuildQueue.Count > 0) rows.Add(new HudRow(wrong ? "warn" : "shipyard", parts[0], wrong ? Severity.Warning : Severity.Normal, yard.BuildQueue[0].Fraction, Tip: line));
            else rows.Add(new HudRow(wrong ? "warn" : "shipyard", parts[0], wrong ? Severity.Warning : Severity.Normal, Tip: line));
            foreach (var part in parts.Skip(1))
                rows.Add(part.StartsWith("storage") && yard.CargoCapacityKg > 0
                    ? new HudRow("cargo", part, yard.CargoKg >= 0.97 * yard.CargoCapacityKg ? Severity.Warning : Severity.Normal, (double)yard.CargoKg / yard.CargoCapacityKg)
                    : WarningWords.Any(part.Contains) ? new HudRow("warn", part, Severity.Warning)
                    : part.StartsWith("refin") ? new HudRow("idle", part, Severity.Muted) : Row(part) with { Severity = Severity.Muted });
        }
        const int most = 12;
        if (rows.Count > most)
        {
            var more = rows.Count - most;
            rows = rows.OrderBy(r => r.Severity is Severity.Warning or Severity.Bad ? 0 : 1).Take(most).ToList(); // what's wrong comes first
            rows.Add(new HudRow("message", $"{more} more: see Fleet and Power", Severity.Muted));
        }
        return rows.Count == 0 ? HudCardData.None : new HudCardData("Stations", rows);
    }

    private HudCardData TargetCard(CraftState p, string text)
    {
        var lines = Lines(text).ToList();
        if (lines.Count == 0) return HudCardData.None;
        var rows = new List<HudRow>();
        var craft = p.TargetCraftId.IsNone ? null : _sector.GetCraft(p.TargetCraftId);
        var body = craft is not null || p.TargetBodyId.IsNone ? null : _sector.GetBody(p.TargetBodyId);
        var heading = lines[0];
        if (heading.Length > 24 && heading.IndexOf(" (", StringComparison.Ordinal) is > 0 and var cut)
        {
            rows.Add(new HudRow("message", heading[(cut + 2)..].TrimEnd(')'), Severity.Muted));
            heading = heading[..cut];
        }
        foreach (var line in lines.Skip(1))
        {
            var motion = line.StartsWith("gap ") || line.StartsWith("surface ");
            if (!motion) { rows.Add(Row(line) with { Severity = line.StartsWith("nearest") ? Severity.Muted : Row(line).Severity }); continue; }
            var parts = line.Split("   ", StringSplitOptions.RemoveEmptyEntries);
            rows.Add(new HudRow("target", parts[0]));
            rows.Add(new HudRow("speed", string.Join("   ", parts.Skip(1)), line.Contains("MATCHED") ? Severity.Good : Severity.Normal));
        }
        rows.Add(CycleRow()); // #107
        return new HudCardData(heading, rows, craft?.Design, craft is not null ? null : body is null ? "target" : UiIcons.BodyIcon(body));
    }

    private HudCardData GoalsCard()
    {
        var text = GoalsText();
        if (text.Length == 0) return HudCardData.None;
        return new HudCardData("Goals", Lines(text).Select(l => l.StartsWith("[x]") ? new HudRow("ok", l[4..], Severity.Muted)
            : l.StartsWith("->") ? new HudRow("goal", l[2..].Trim()) : l.StartsWith("[ ]") ? new HudRow("idle", l[4..], Severity.Muted) : new HudRow("", l.Trim(), Severity.Muted)).ToList());
    }

    // --- Arrows at the edge of the view ---

    private static readonly Color StationArrow = new(0.6f, 0.8f, 1f);

    /// <summary>Arrows to what's off screen: the target, the nearest shipyard, and enemies in this system.</summary>
    private void DrawEdgeArrows(double renderTimeS)
    {
        if (_mapMode || _player is not { } p || !_hud.Visible) return;
        var half = GetViewportRect().Size / 2;
        var scale = _uiRoot.Scale.X;
        var left = _hud.Shown ? (_hud.LeftWidth + 24) * scale : 24 * scale;
        var free = new Rect2(-half.X + left, -half.Y + 64 * scale, 0, 0);
        free.End = new Vector2(half.X - (_panelsWidth + 24) * scale, half.Y - 24 * scale);
        if (free.Size.X < 80 || free.Size.Y < 80) return;
        var view = new Rect2(-half, half * 2);
        var drawn = new List<Vector2>();

        if (!p.TargetCraftId.IsNone) Arrow(ToPixels(InterpolatedPosition(_sector.GetCraft(p.TargetCraftId))), 0, MarkerColor, _sector.GetCraft(p.TargetCraftId).Name,
            _sector.GetRelativeMotionToCraft(p.Id, p.TargetCraftId).SurfaceDistanceM, TargetRef.Of(_sector.GetCraft(p.TargetCraftId)));
        else if (!p.TargetBodyId.IsNone)
        {
            var body = _sector.GetBody(p.TargetBodyId);
            Arrow(ToPixels(_sector.GetPositionAt(body.Id, renderTimeS)), (float)body.RadiusM * _pixelsPerMetre, MarkerColor, body.Name, _sector.GetRelativeMotion(p.Id, body.Id).SurfaceDistanceM, TargetRef.Of(body));
        }
        var here = _sector.StarAt(p.Position)?.Id;
        CraftState? yard = null;
        foreach (var craft in _sector.Crafts)
        {
            if (craft.IsShipyard && SectorState.IsPlayers(craft) && !craft.IsWreck && _sector.StarAt(craft.Position)?.Id == here
                && (yard is null || craft.Position.DistanceTo(p.Position) < yard.Position.DistanceTo(p.Position))) yard = craft;
        }
        if (yard is not null && yard.Id != p.Id && yard.Id != p.TargetCraftId)
            Arrow(ToPixels(InterpolatedPosition(yard)), 0, StationArrow, yard.Name, yard.Position.DistanceTo(p.Position), TargetRef.Of(yard));
        if (_sector.Factions.Count == 0) return;
        foreach (var enemy in _sector.Crafts.Where(c => !SectorState.IsPlayers(c) && !c.IsWreck && c.Id != p.TargetCraftId && _sector.IsEnemy(c) && _sector.StarAt(c.Position)?.Id == here)
                     .OrderBy(c => c.Position.DistanceTo(p.Position)).Take(4).ToList())
            Arrow(ToPixels(InterpolatedPosition(enemy)), 0, Palette.Bad, "! " + enemy.Name, enemy.Position.DistanceTo(p.Position), TargetRef.Of(enemy));

        void Arrow(Vector2 at, float radius, Color color, string name, double distance, TargetRef target)
        {
            if (view.Grow(radius).HasPoint(at)) return;
            var from = free.GetCenter();
            var direction = (at - from).Normalized();
            if (!direction.IsFinite() || direction == Vector2.Zero) return;
            var reach = Mathf.Min(direction.X != 0 ? free.Size.X / 2 / Mathf.Abs(direction.X) : float.MaxValue, direction.Y != 0 ? free.Size.Y / 2 / Mathf.Abs(direction.Y) : float.MaxValue);
            var tip = from + direction * reach;
            if (drawn.Any(d => d.DistanceTo(tip) < 60 * scale)) return; // a group: the nearest (or the target) speaks for it
            drawn.Add(tip);
            _arrowHits.Add((target, tip));
            var side = new Vector2(-direction.Y, direction.X) * 7 * scale;
            var back = tip - direction * 16 * scale;
            DrawColoredPolygon(new[] { tip, back + side, back - side }, color);
            var font = ThemeDB.FallbackFont;
            var size = Mathf.RoundToInt(13 * scale * _settings.TextScale);
            var text = $"{name}  {FormatDistance(distance)}";
            var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
            var place = back - direction * (14 * scale + Mathf.Abs(direction.X) * width / 2) - new Vector2(width / 2, -size / 3f);
            place.X = Mathf.Clamp(place.X, free.Position.X, Mathf.Max(free.Position.X, free.End.X - width));
            DrawString(font, place + Vector2.One, text, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.8f));
            DrawString(font, place, text, HorizontalAlignment.Left, -1, size, color);
        }
    }
}
