using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Development tuning: multipliers applied on top of part stats, so pacing can be tried in game
/// without editing data files. Part of the simulation state (saved), so a save replays the same way.
/// Defaults are 1 (no change). Game speed is not here: it only changes how fast ticks are run, not results.
/// </summary>
public sealed class SimulationTuning
{
    public const double Min = 0.01;
    public const double Max = 1_000;

    private double _drill = 1, _refining = 1, _fabrication = 1;

    public double DrillRateMultiplier { get => _drill; set => _drill = Clamp(value); }
    public double RefiningRateMultiplier { get => _refining; set => _refining = Clamp(value); }
    public double FabricationRateMultiplier { get => _fabrication; set => _fabrication = Clamp(value); }

    public bool IsDefault => _drill == 1 && _refining == 1 && _fabrication == 1;

    public void Reset() => _drill = _refining = _fabrication = 1;

    private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, Min, Max) : 1;

    public override string ToString() => $"drill ×{_drill:0.##}, refining ×{_refining:0.##}, fabrication ×{_fabrication:0.##}";
}

/// <summary>
/// Debug tools for development playtesting. The cheats deliberately break the "no mass created"
/// rule, so they are only reachable from the debug menu and are named as such.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Mass added by debug cheats since this sector was created or loaded (not saved), so diagnostics can tell it apart from a leak.</summary>
    public long DebugAddedKg { get; private set; }

    public SimulationTuning Tuning { get; } = new();

    /// <summary>CHEAT: adds material to a craft's inventory out of nothing (ignores capacity).</summary>
    public void DebugAddStock(CraftId craftId, string materialId, long kg)
    {
        if (kg <= 0) throw new ArgumentOutOfRangeException(nameof(kg));
        GetCraft(craftId).Inventory.Add(materialId, kg);
        DebugAddedKg += kg;
    }

    /// <summary>DEBUG: finds points of interest (#122), as if by this craft.</summary>
    public void DebugFindPoints(CraftId by, Func<PointOfInterest, bool> which)
    {
        foreach (var point in _points.Where(p => !p.Found && which(p)).ToList())
            if (KindOf(point) is { } kind) FindPoint(GetCraft(by), point, kind);
    }

    /// <summary>DEBUG: puts a craft beside a point of interest, or lands it there.</summary>
    public void DebugGoToPoint(CraftId craftId, int pointId)
    {
        if (PointById(pointId) is not { } point || KindOf(point) is not { } kind || PointPosition(point, TimeS) is not { } at) return;
        var craft = GetCraft(craftId);
        var parent = GetBody(point.Parent);
        var away = at - GetPosition(parent.Id);
        var up = away * (1 / away.Length);
        craft.Position = craft.PreviousPosition = at + up * (kind.Type == Construction.PointType.Anomaly ? 600 : 110);
        craft.Velocity = point.Orbit is { } orbit ? RailVelocityAt(orbit, TimeS) : GetVelocityAt(parent.Id, TimeS);
        craft.HeadingRad = craft.PreviousHeadingRad = Math.Atan2(up.X, -up.Y);
        if (kind.Type == Construction.PointType.Ruin) RestoreLanding(craft.Id, parent.Id, up * (parent.RadiusM + 15));
    }

    /// <summary>CHEAT: marks every body surveyed.</summary>
    public void DebugSurveyAll()
    {
        foreach (var body in _bodies) body.Surveyed = true;
    }

    /// <summary>DEBUG: records a crater (#28) without removing any mass (only to look at how craters draw).</summary>
    public void DebugDigCrater(BodyId bodyId, double bearingRad, long kg) => GetBody(bodyId).Dig(bearingRad, kg);

    /// <summary>
    /// DEBUG: damages one part as an impact would: sets its health, or breaks it off at 0 (its
    /// materials become debris). For tests and trying out repairs.
    /// </summary>
    public void DebugDamagePart(CraftId craftId, Construction.GridPos pos, double health)
    {
        var craft = GetCraft(craftId);
        if (Parts is null) throw new InvalidOperationException("No part catalog: this sector takes no damage.");
        var cell = craft.Design.Cells.First(c => c.Pos == pos);
        if (health <= 0) { BreakOff(craft, new List<Construction.DesignCell> { cell }); return; }
        if (health > 1) throw new ArgumentOutOfRangeException(nameof(health));
        if (health < 1) craft.HealthList[pos] = health; else craft.HealthList.Remove(pos);
        craft.Stats = craft.Design.ComputeStats(Parts, craft.HealthList);
    }

    /// <summary>Benchmarks and tests (#114): a weapon hit on a part, as combat does it.</summary>
    public void DebugHit(CraftId craftId, int cellIndex, double damage) => HitCell(GetCraft(craftId), cellIndex, damage);

    /// <summary>Benchmarks and tests (#114): a shot in flight from an enemy, harmful to the player's craft.</summary>
    public void DebugShoot(Vector2D position, Vector2D velocity, double lifeS, double damage = 0.01) => _shots.Add(new Shot { Position = position, Velocity = velocity, ExpireTick = Tick + (long)(lifeS * FixedStepClock.TicksPerSecond), Damage = damage, FactionId = -1 });
    /// <summary>Tests: a craft gone (#124).</summary>
    internal void RemoveCraftForTest(CraftId id) => RemoveCraft(GetCraft(id));

    /// <summary>Tests: mining counted in a claimed system (#124).</summary>
    internal void NoteMiningForTest(CelestialBodyState body, long kg) => NoteMining(body, kg);
    /// <summary>Tests: a civilization of this race (#125), or none.</summary>
    internal void SetFactionRaceForTest(Faction faction, Construction.RaceDefinition? race)
    {
        faction.Race = race;
        faction.RaceId = race?.Id ?? "";
        foreach (var craft in _crafts.Where(c => c.FactionId == faction.Id).ToList()) { DiscoveredKg -= (long)Math.Round(craft.Stats.TotalMassKg) + craft.Inventory.TotalKg; RemoveCraft(craft); }
        faction.PatrolShipsBuilt = 0;
        faction.NextPatrolTick = 0;
        faction.NextStationTick = 0;
    }

    /// <summary>DEBUG: a megastructure's stages done at once, from outside the sector (#126).</summary>
    public void DebugBuildMega(int id, int stages)
    {
        var project = _megas.First(m => m.Id == id);
        var kind = KindOf(project)!;
        for (var i = 0; i < stages && project.Stages < kind.Stages.Count; i++)
        {
            var kg = kind.Stages[project.Stages].MaterialsKg.Values.Sum();
            project.BuiltKg += kg;
            DiscoveredKg += kg;
            project.Stages++;
        }
        project.Working = false;
        project.WorkTicks = 0;
        if (_craftsById.TryGetValue(project.HubId, out var hub)) PostMegaRequests(project, kind, hub);
    }
}
