using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Points of interest (#122): derelict craft, ruins on bodies, and anomalies.
/// <list type="bullet">
/// <item>Placed when a system is made, from a stream of its own for each system and kind, so nothing else about the world
/// changes with them, and a world with more has the same ones and some besides.</item>
/// <item>The ship you fly, and scouts with a survey job, find them by passing near. The map has a vague hint before that.</item>
/// <item>Using one is done in person: a derelict beside you can be claimed, or left to the salvagers; a ruin is studied landed
/// by it; an anomaly is scanned from close by. Then there may be something to choose.</item>
/// <item>A derelict's and an anomaly's mass enters the world when it's found (<see cref="DiscoveredKg"/>).</item>
/// </list>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Anomalies' bodies have ids from here, so that finding one doesn't change the ids (and so the sites) of bodies made later.</summary>
    public const uint PointBodyIdBase = 1_000_000;

    private const int PointCheckTicks = 5;

    private readonly List<PointOfInterest> _points = new();

    /// <summary>What this world has more or less of (#119). Saved.</summary>
    public WorldOptions Options { get; set; } = new();

    /// <summary>Every point of interest, hidden ones too, in id order.</summary>
    public IReadOnlyList<PointOfInterest> Points => _points;

    private PointCatalog PointKinds => Parts?.Points ?? PointCatalog.Empty;

    public PointKind? KindOf(PointOfInterest point) => PointKinds.Find(point.KindId);

    public PointOfInterest? PointById(int id) => id >= 1 && id <= _points.Count ? _points[id - 1] : null;

    /// <summary>The found point of interest a target is, if it is one.</summary>
    public PointOfInterest? PointAt(TargetRef target)
    {
        if (target.IsNone) return null;
        foreach (var p in _points)
            if (p.Found && (!p.CraftId.IsNone && p.CraftId == target.Craft || !p.BodyId.IsNone && p.BodyId == target.Body)) return p;
        return null;
    }

    /// <summary>What to target to get there: the derelict, the anomaly's body, or the body a ruin is on. None: it's gone.</summary>
    public TargetRef TargetOf(PointOfInterest point)
    {
        var target = !point.CraftId.IsNone ? new TargetRef(point.CraftId, BodyId.None)
            : new TargetRef(CraftId.None, !point.BodyId.IsNone ? point.BodyId : point.Parent);
        return Exists(target) ? target : TargetRef.None;
    }

    /// <summary>Where it is, or null if that's gone.</summary>
    public Vector2D? PointPosition(PointOfInterest point, double timeS)
    {
        if (!point.CraftId.IsNone) return _craftsById.TryGetValue(point.CraftId, out var craft) ? craft.Position : null;
        if (!point.BodyId.IsNone) return _byId.ContainsKey(point.BodyId) ? GetPositionAt(point.BodyId, timeS) : null;
        if (!_byId.TryGetValue(point.Parent, out var parent)) return null;
        return GetPositionAt(parent.Id, timeS) + (point.Orbit?.OffsetAt(timeS) ?? new Vector2D(Math.Cos(point.BearingRad), Math.Sin(point.BearingRad)) * parent.RadiusM);
    }

    /// <summary>A hidden one's hint: a circle it's somewhere in.</summary>
    public (Vector2D Centre, double RadiusM)? PointHint(PointOfInterest point, double timeS)
    {
        if (point.Found || point.Done || KindOf(point) is not { } kind || PointPosition(point, timeS) is not { } at) return null;
        if (kind.Type == PointType.Ruin) return (GetPositionAt(point.Parent, timeS), GetBody(point.Parent).RadiusM + kind.HintM); // somewhere on it
        var rng = new DeterministicRandom(point.Roll ^ 0x48494E54UL);
        var angle = rng.Range(0, 2 * Math.PI);
        return (at + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (rng.Range(0.2, 0.75) * kind.HintM), kind.HintM);
    }

    // --- Placing ---

    /// <summary>Places a system's points of interest: 0 is home.</summary>
    internal void PlacePoints(CelestialBodyState star, int systemIndex)
    {
        var amount = Options[WorldOptions.PointsOfInterest];
        if (amount == WorldAmount.Off) return;
        foreach (var kind in PointKinds.Kinds)
        {
            var mean = amount switch { WorldAmount.Rare => kind.PerSystem.Rare, WorldAmount.Often => kind.PerSystem.Often, _ => kind.PerSystem.Normal };
            var rng = new DeterministicRandom(Seed ^ NewGame.SeedFromText("points " + kind.Id) ^ (0xD1B54A32D192ED03UL * (ulong)(systemIndex + 1)));
            var count = (int)Math.Floor(mean + rng.NextDouble());
            var places = _bodies.Where(b => b.Id.Value < PointBodyIdBase && StarOf(b.Id)?.Id == star.Id && (kind.Type == PointType.Ruin
                ? b.IsMineable && b.Kind is BodyKind.Planet or BodyKind.Moon
                : b.Kind is BodyKind.Planet or BodyKind.GasGiant)).ToList();
            if (places.Count == 0) continue;
            for (var i = 0; i < count; i++)
            {
                var one = new DeterministicRandom(rng.NextUInt64()); // each its own: what one draws doesn't move the next
                var parent = places[(int)(one.NextDouble() * places.Count)];
                var name = kind.Names[(int)(one.NextDouble() * kind.Names.Count)];
                var bearing = one.Range(-Math.PI, Math.PI);
                CircularOrbit? orbit = null;
                if (kind.Type != PointType.Ruin)
                {
                    // Clear of the moons and of other points of interest.
                    double? free = null;
                    for (var attempt = 0; attempt < 40 && free is null; attempt++)
                    {
                        var radius = parent.RadiusM + one.Range(kind.MinOrbitM, kind.MaxOrbitM);
                        var clear = _bodies.All(b => b.Orbit is not { } o || o.Parent != parent.Id || Math.Abs(o.RadiusM - radius) > b.RadiusM + 2_500)
                            && _points.All(p => p.Orbit is not { } o || o.Parent != parent.Id || Math.Abs(o.RadiusM - radius) > 1_500);
                        if (clear) free = radius;
                    }
                    if (free is not { } orbitRadius) continue;
                    orbit = new CircularOrbit(parent.Id, orbitRadius, CircularOrbitPeriodS(parent, orbitRadius, 3_600), one.Range(0, 2 * Math.PI));
                }
                var copies = _points.Count(p => p.Name == name || p.Name.StartsWith(name + " ", StringComparison.Ordinal));
                _points.Add(new PointOfInterest(_points.Count + 1, kind.Id, copies > 0 ? $"{name} {copies + 1}" : name, parent.Id, orbit, bearing, one.NextUInt64()));
            }
        }
    }

    internal void RestorePoint(PointOfInterest point)
    {
        if (point.Id != _points.Count + 1) throw new FormatException($"Point of interest {point.Id} is out of order.");
        _points.Add(point);
        if (!point.BodyId.IsNone && !point.Studied && _byId.TryGetValue(point.BodyId, out var body)) body.Unscanned = true;
    }

    // --- Finding ---

    private void StepPoints()
    {
        if (_points.Count == 0) return;
        ChargeFromFields();
        if (Tick % PointCheckTicks != 0) return;
        _craftsById.TryGetValue(PilotCraftId, out var pilot);
        var hidden = false;
        foreach (var point in _points)
        {
            if (point.Done) continue;
            if (!point.Found) { hidden = true; continue; }
            if (KindOf(point) is not { } kind) continue;
            if (TargetOf(point).IsNone) { Finish(point, kind.Type == PointType.Derelict ? "Salvaged." : "It's gone."); continue; }
            if (pilot is null || kind.Type == PointType.Derelict || point.Studied || InPlace(pilot, point, kind) is not null) continue;
            point.StudyTicks += PointCheckTicks;
            if (point.StudyTicks >= kind.StudySeconds * FixedStepClock.TicksPerSecond) Study(pilot, point, kind);
        }
        if (!hidden) return;
        if (pilot is not null) DetectAround(pilot);
        for (var i = 0; i < _crafts.Count; i++) // finding a derelict adds a craft, at the end
            if (_crafts[i] is { Job: SurveyJob { Active: true }, Transit: null } scout && scout.Id != PilotCraftId) DetectAround(scout);
    }

    private void DetectAround(CraftState craft)
    {
        if (craft.IsWreck) return;
        foreach (var point in _points)
        {
            if (point.Found || point.Done || KindOf(point) is not { } kind || PointPosition(point, TimeS) is not { } at) continue;
            if (kind.Type == PointType.Ruin)
            {
                // In sight: low enough, and above the horizon from there.
                var body = GetBody(point.Parent);
                var up = craft.Position - GetPosition(point.Parent);
                if (up.Length - body.RadiusM > kind.DetectM) continue;
                var gap = Math.Abs(Math.IEEERemainder(Math.Atan2(up.Y, up.X) - point.BearingRad, 2 * Math.PI));
                if (gap > Math.Max(Math.Acos(Math.Min(1, body.RadiusM / Math.Max(up.Length, body.RadiusM))), kind.UseM / Math.Max(body.RadiusM, 1))) continue;
            }
            else if (at.DistanceTo(craft.Position) > kind.DetectM) continue;
            FindPoint(craft, point, kind);
        }
    }

    private void FindPoint(CraftState by, PointOfInterest point, PointKind kind)
    {
        var before = ComputeMassLedger().TotalKg;
        point.Found = true;
        if (kind.Type == PointType.Derelict) MakeDerelict(point, kind);
        else if (kind.Type == PointType.Anomaly) MakeAnomaly(point, kind);
        DiscoveredKg += ComputeMassLedger().TotalKg - before; // it was out there all along
        var where = GetBody(point.Parent).Name;
        AddEvent(kind.Type switch
        {
            PointType.Derelict => $"{by.Name} found a derelict near {where}: {point.Name}.",
            PointType.Ruin => $"{by.Name} found ruins on {where}: {point.Name}.",
            _ => $"{by.Name} found something strange near {where}: {point.Name}.",
        }, EventKind.Discovery, TargetOf(point));
    }

    private void MakeDerelict(PointOfInterest point, PointKind kind)
    {
        var rng = new DeterministicRandom(point.Roll);
        var full = kind.Designs[(int)(rng.NextDouble() * kind.Designs.Count)];
        var parts = Parts!;
        var lost = new HashSet<GridPos>();
        var health = new List<(GridPos Pos, double Health)>();
        foreach (var cell in full.CellArray)
        {
            var (missing, damaged, worn) = (rng.NextDouble() < kind.Missing, rng.NextDouble() < kind.Damaged, rng.Range(0.2, 0.9));
            if (missing && !parts.Get(cell.PartId).IsControlSource && lost.Count < full.Cells.Count - 1) lost.Add(cell.Pos);
            else if (damaged) health.Add((cell.Pos, worn));
        }
        var design = lost.Count > 0 ? full.Keep(c => !lost.Contains(c.Pos)) : full;
        var craft = new CraftState(AllocateCraftId(), point.Name, design, design.ComputeStats(parts), Vector2D.Zero, rng.Range(0, 2 * Math.PI)) { IsWreck = true };
        if (lost.Count > 0) craft.IntactDesign = full;
        craft.PartsLost = lost.Count;
        foreach (var (pos, left) in health) craft.HealthList[pos] = left;
        AddCraft(craft);
        craft.HealthChanged(parts);
        craft.StoredEnergyKJ = 0;
        PutOnRails(craft.Id, point.Orbit!);
        var room = craft.HasCargoLimit ? (long)craft.CargoCapacityKg : 0;
        foreach (var goods in kind.Cargo)
        {
            var kg = Math.Min(room, goods.MinKg + (long)(rng.NextDouble() * (goods.MaxKg - goods.MinKg + 1)));
            if (kg <= 0) continue;
            craft.Inventory.Add(goods.Material, kg);
            room -= kg;
        }
        point.CraftId = craft.Id;
    }

    private void MakeAnomaly(PointOfInterest point, PointKind kind)
    {
        var rng = new DeterministicRandom(point.Roll);
        var find = FindOf(point, kind);
        var deposits = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var ore in find.Ore) deposits[ore.Material] = ore.MinKg + (long)(rng.NextDouble() * (ore.MaxKg - ore.MinKg + 1));
        if (deposits.Count == 0) deposits["silicate"] = 20_000;
        var radius = rng.Range(120, 220);
        var body = new CelestialBodyState(new BodyId(PointBodyIdBase + (uint)point.Id), point.Name, BodyKind.Asteroid,
            Math.Round(CelestialBodyState.MassFromRadius(radius, SectorGenerator.AsteroidDensityKgPerM3)), SectorGenerator.AsteroidDensityKgPerM3, true, point.Orbit, deposits) { Unscanned = true };
        AddBody(body);
        point.BodyId = body.Id;
    }

    /// <summary>What an anomaly is: fixed by its roll.</summary>
    private static PointOutcome FindOf(PointOfInterest point, PointKind kind)
    {
        var at = new DeterministicRandom(point.Roll ^ 0x46494E44UL).NextDouble() * kind.Outcomes.Sum(o => o.Weight);
        foreach (var outcome in kind.Outcomes)
            if ((at -= outcome.Weight) < 0) return outcome;
        return kind.Outcomes[^1];
    }

    /// <summary>What a scanned anomaly turned out to be.</summary>
    public PointOutcome? FoundIn(PointOfInterest point) =>
        point.Studied && KindOf(point) is { Type: PointType.Anomaly } kind ? FindOf(point, kind) : null;

    // --- Using ---

    /// <summary>Why a craft can't use one from where it is, or null.</summary>
    public string? PointBlocker(CraftState craft, PointOfInterest point) =>
        KindOf(point) is { } kind ? InPlace(craft, point, kind) : "This game's data doesn't have this kind.";

    private string? InPlace(CraftState craft, PointOfInterest point, PointKind kind)
    {
        if (TargetOf(point).IsNone || PointPosition(point, TimeS) is not { } at) return "It's gone.";
        if (craft.Cruising || craft.Transit is not null) return "Slow down first.";
        switch (kind.Type)
        {
            case PointType.Derelict:
                var wreck = GetCraft(point.CraftId);
                var gap = craft.Position.DistanceTo(at) - craft.CollisionRadiusM - wreck.CollisionRadiusM;
                return gap > kind.UseM ? $"Come within {kind.UseM:0} m of it." : (craft.Velocity - wreck.Velocity).Length > SalvageMaxSpeedMps ? "Match its speed." : null;
            case PointType.Ruin:
                if (craft.LandedOnBodyId != point.Parent) return $"Land on {GetBody(point.Parent).Name}, within {kind.UseM:0} m of it.";
                var along = craft.Position.DistanceTo(at);
                return along > kind.UseM ? $"Land within {kind.UseM:0} m of it ({along / 1000:0.0} km away)." : null;
            default:
                return craft.Position.DistanceTo(at) - GetBody(point.BodyId).RadiusM > kind.UseM ? $"Come within {kind.UseM:0} m of it." : null;
        }
    }

    private void Study(CraftState by, PointOfInterest point, PointKind kind)
    {
        point.Studied = true;
        if (kind.Type == PointType.Ruin)
        {
            AddEvent($"{by.Name} studied {point.Name}: decide what to keep.", EventKind.Discovery, TargetOf(point));
            return;
        }
        var body = GetBody(point.BodyId);
        body.Unscanned = false;
        body.Surveyed = true;
        var find = FindOf(point, kind);
        if (find.Find == AnomalyFind.Ore) Finish(point, $"A rich pocket: {string.Join(", ", body.DepositsKg.Select(d => $"{d.Value / 1000:0} t {MaterialName(d.Key)}"))}.");
        AddEvent($"{by.Name} scanned {point.Name}. {find.Text}", EventKind.Discovery, TargetOf(point));
    }

    private string MaterialName(string id) => id.Replace('_', ' ');

    private static void Finish(PointOfInterest point, string result) => (point.Done, point.Result) = (true, result);

    private static double ResearchIn(PointOfInterest point, PointKind kind) =>
        Math.Round(new DeterministicRandom(point.Roll ^ 0x52455345UL).Range(kind.MinResearch, kind.MaxResearch) / 5) * 5;

    /// <summary>The dearest technology that could be researched next and isn't above the ruin's limit.</summary>
    private TechDefinition? TechIn(PointKind kind) => ResearchAllUnlocked ? null : Tech.Techs
        .Where(t => !IsTechUnlocked(t.Id) && t.Cost <= kind.MaxTechCost && (t.Requires ?? new List<string>()).All(IsTechUnlocked))
        .OrderByDescending(t => t.Cost).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>What can be decided about one now, by the craft there; empty when there's nothing (yet).</summary>
    public IReadOnlyList<PointChoice> PointChoices(CraftState craft, PointOfInterest point)
    {
        if (!point.Found || point.Done || KindOf(point) is not { } kind) return Array.Empty<PointChoice>();
        var away = InPlace(craft, point, kind);
        switch (kind.Type)
        {
            case PointType.Derelict:
                if (!_craftsById.ContainsKey(point.CraftId)) return Array.Empty<PointChoice>();
                var shortfall = craft.Inventory.Shortfall(kind.ClaimKg);
                var cost = kind.ClaimKg.Count > 0 ? $"Takes {Describe(kind.ClaimKg)} from your hold for the first repairs. " : "";
                return new[]
                {
                    new PointChoice("claim", "Claim it", cost + "It's yours as it is: a shipyard or repair drones mend the rest.", away ?? (shortfall.Count > 0 ? $"Your hold lacks {Describe(shortfall)}." : null)),
                    new PointChoice("salvage", "Leave it to salvagers", "Ships with a salvage job will take it apart. With a salvage arm you can do it yourself at any time.", away),
                };
            case PointType.Ruin when point.Studied:
                var choices = new List<PointChoice> { new("research", "Keep the records", $"{ResearchIn(point, kind):0} research points.", away) };
                if (TechIn(kind) is { } tech) choices.Add(new("tech", $"Learn {tech.Name}", $"That technology is researched (it costs {tech.Cost:0} points).", away));
                return choices;
            case PointType.Anomaly when point.Studied:
                var find = FindOf(point, kind);
                var points = new PointChoice("research", "Study it fully", $"{ResearchIn(point, kind):0} research points." + (find.Find == AnomalyFind.Energy ? " The field is used up." : ""), away);
                return find.Find == AnomalyFind.Energy
                    ? new[] { new PointChoice("keep", "Leave the field", $"Your ships within {find.RangeM / 1000:0.0} km charge at up to {find.ChargeKW:0} kW, for good.", away), points }
                    : new[] { points };
            default: return Array.Empty<PointChoice>();
        }
    }

    /// <summary>Decides about one, in person.</summary>
    public ActionResult ChoosePoint(CraftId craftId, int pointId, string choiceId)
    {
        var craft = GetCraft(craftId);
        if (craftId != PilotCraftId) return new(false, "Only the ship you fly can do that.");
        if (PointById(pointId) is not { } point || KindOf(point) is not { } kind) return new(false, "There's nothing there.");
        if (PointChoices(craft, point).FirstOrDefault(c => c.Id == choiceId) is not { } choice) return new(false, "That can't be done here.");
        if (choice.Blocker is { } why) return new(false, why);
        switch (choiceId)
        {
            case "claim":
                var derelict = GetCraft(point.CraftId);
                if (!craft.Inventory.TryRemove(kind.ClaimKg)) return new(false, "Your hold lacks the materials.");
                foreach (var (material, kg) in kind.ClaimKg) _ejectedKg[material] = _ejectedKg.GetValueOrDefault(material) + kg; // used up, as any repair's
                derelict.IsWreck = false;
                Finish(point, "Claimed.");
                AddEvent($"You claimed {derelict.Name}.", EventKind.Discovery, TargetRef.Of(derelict));
                return new(true, $"{derelict.Name} is yours." + (NeedsRepair(derelict) ? " It needs repairs." : ""));
            case "salvage":
                Finish(point, "Left to the salvagers.");
                return new(true, $"{point.Name} is left to the salvagers.");
            case "research":
                var gained = ResearchIn(point, kind);
                ResearchPoints += gained;
                Finish(point, $"{gained:0} research points.");
                AddEvent($"{point.Name} gave {gained:0} research points.", EventKind.Discovery, TargetOf(point));
                return new(true, $"{gained:0} research points.");
            case "tech":
                var tech = TechIn(kind)!;
                _unlockedTech.Add(tech.Id);
                Finish(point, $"{tech.Name} learned.");
                AddEvent($"{point.Name} taught you {tech.Name}.", EventKind.Discovery, TargetOf(point));
                return new(true, $"{tech.Name} is researched.");
            default:
                point.FieldKept = true;
                Finish(point, "Its field charges ships.");
                return new(true, "The field stays: your ships near it charge.");
        }
    }

    /// <summary>A derelict not yet decided about: salvage jobs leave it alone.</summary>
    private bool IsUndecided(CraftState wreck)
    {
        foreach (var point in _points) if (point.CraftId == wreck.Id) return !point.Done;
        return false;
    }

    /// <summary>The energy fields: scanned, and not used up.</summary>
    public IEnumerable<(PointOfInterest Point, PointOutcome Field)> EnergyFields()
    {
        foreach (var point in _points)
            if (point.Studied && (!point.Done || point.FieldKept) && KindOf(point) is { Type: PointType.Anomaly } kind && FindOf(point, kind) is { Find: AnomalyFind.Energy } field)
                yield return (point, field);
    }

    private void ChargeFromFields()
    {
        foreach (var (point, field) in EnergyFields())
        {
            if (PointPosition(point, TimeS) is not { } at) continue;
            foreach (var craft in _crafts)
                if (craft.StoredEnergyKJ < craft.Stats.EnergyCapacityKJ && IsPlayers(craft) && !craft.IsWreck && craft.Transit is null && craft.Position.DistanceTo(at) <= field.RangeM)
                    craft.StoredEnergyKJ = Math.Min(craft.Stats.EnergyCapacityKJ, craft.StoredEnergyKJ + field.ChargeKW * FixedStepClock.TickSeconds);
        }
    }
}
