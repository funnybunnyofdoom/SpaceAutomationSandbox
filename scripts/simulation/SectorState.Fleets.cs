using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Civilization fleets (#89; the creator's choices). Every civilization keeps a small patrol in its own system, going round its
/// planets. Hostile ones attack what of yours they meet there; others leave you alone, and your weapons never fire at them unless you
/// order an attack (which makes them hostile). Friendly ones also fight civilizations that are their enemies. Their ships
/// never go through your gates. Lost ships are replaced after a while, from outside the sector (the mass ledger stays balanced).
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Ships a civilization keeps on patrol, and how long it takes to replace one.</summary>
    public const int FleetPatrolShips = 3;
    public const double FleetReplaceS = 600;

    /// <summary>Grievance from an unprovoked attack: they stay hostile for hours.</summary>
    public const double AttackGrievance = 100;

    private readonly List<CraftState> _foreign = new();

    public Faction? FactionById(int id)
    {
        foreach (var faction in _factions) if (faction.Id == id) return faction;
        return null;
    }

    /// <summary>Do this craft and the player's side fire on each other? Raiders always; patrols and trade stations only of hostile civilizations.</summary>
    public bool IsEnemy(CraftState craft) =>
        !IsPlayers(craft) && (!(craft.IsPatrol || craft.IsTradeStation || craft.IsStranded) || FactionById(craft.FactionId) is { Disposition: Disposition.Hostile });

    /// <summary>Two civilizations are enemies when their cultures want opposite laws.</summary>
    public static bool AreEnemies(Faction a, Faction b) =>
        a.Id != b.Id && Laws.All.Sum(l => a.Culture.StanceOn(l.Id) * b.Culture.StanceOn(l.Id)) < 0;

    /// <summary>Whether two craft fire on each other.</summary>
    private bool AreHostile(CraftState a, CraftState b)
    {
        if (a.FactionId == b.FactionId) return false;
        if (IsPlayers(a)) return IsEnemy(b);
        if (IsPlayers(b)) return IsEnemy(a);
        return FactionsFight(a.FactionId, b.FactionId);
    }

    /// <summary>Civilizations only fight each other when one that is friendly to you meets an enemy of its own.</summary>
    private bool FactionsFight(int a, int b) =>
        FactionById(a) is { } one && FactionById(b) is { } two && AreEnemies(one, two)
        && (one.Disposition == Disposition.Friendly || two.Disposition == Disposition.Friendly);

    /// <summary>Whether a shot from one side hits a craft (neutral ships aren't hit by accident).</summary>
    private bool ShotHits(Shot shot, CraftState craft) =>
        shot.FactionId != craft.FactionId
        && (shot.FactionId == 0 ? IsEnemy(craft) : IsPlayers(craft) || FactionsFight(shot.FactionId, craft.FactionId));

    /// <summary>The player orders an attack on a civilization that isn't hostile (the UI asks first): it becomes hostile.</summary>
    public ActionResult DeclareAttack(int factionId)
    {
        if (FactionById(factionId) is not { } faction) return new(false, "No such civilization.");
        if (faction.Disposition == Disposition.Hostile) return new(false, $"The {faction.Name} are already hostile.");
        faction.Grievance = AttackGrievance;
        faction.Attitude = -100;
        faction.Goodwill = 0;
        faction.TruceUntilTick = 0;
        _lastDisposition[faction.Id] = faction.Disposition;
        AddEvent($"You attacked the {faction.Name}. They are now hostile towards you.", EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
        return new(true, $"Weapons free on the {faction.Name}.");
    }

    /// <summary>Once a second: brings patrols up to strength, in the civilization's own system.</summary>
    private void StepFleets()
    {
        if (Parts is null || _stars.Count == 0) return;
        foreach (var faction in _factions)
        {
            if (faction.HomeStarId == _stars[0].Id || Tick < faction.NextPatrolTick) continue; // never in the home system
            var stops = PatrolStops(faction.HomeStarId);
            if (stops.Count == 0) continue;
            var ships = 0;
            foreach (var craft in _crafts) if (craft.IsPatrol && craft.FactionId == faction.Id && !craft.IsWreck) ships++;
            var size = PatrolSize(faction);
            if (ships >= size) continue;
            faction.NextPatrolTick = Tick + (long)((ships + 1 < size && faction.PatrolShipsBuilt < size ? 1 : FleetReplaceS / Civ(faction, CivilizationTraits.PatrolRebuild)) * FixedStepClock.TicksPerSecond);
            var stop = stops[^1]; // they come from the outermost planet
            var design = faction.PatrolShipsBuilt % FleetPatrolShips == FleetPatrolShips - 1 ? MissileDesignOf(faction, patrol: true) : GunDesignOf(faction, patrol: true);
            var stats = design.ComputeStats(Parts);
            var angle = faction.PatrolShipsBuilt * 0.05 + faction.Id;
            var position = GetPosition(stop.Id) + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (stop.RadiusM + PatrolAltitudeM + 500);
            var ship = new CraftState(AllocateCraftId(), $"{faction.Name} patrol {faction.PatrolShipsBuilt + 1}", design, stats, position, 0)
            {
                FactionId = faction.Id, IsPatrol = true, PatrolStop = stops.Count - 1, Velocity = GetVelocityAt(stop.Id, TimeS),
            };
            Arm(ship);
            AddCraft(ship);
            DiscoveredKg += (long)Math.Round(stats.TotalMassKg);
            faction.PatrolShipsBuilt++;
        }
    }

    // --- A civilization's race (#125) ---

    /// <summary>What a civilization trait of its race multiplies by: 1 for none.</summary>
    private static double Civ(Faction faction, string trait) => faction.Rules?.CivFactor(trait) ?? 1;

    /// <summary>Patrol ships a civilization keeps: 3, more or fewer by its race, 1 to 5.</summary>
    public static int PatrolSize(Faction faction) => Math.Clamp((int)Math.Round(FleetPatrolShips * Civ(faction, CivilizationTraits.PatrolShips), MidpointRounding.AwayFromZero), 1, 5);

    /// <summary>A raid's ships, by its race.</summary>
    public static int RaidShips(Faction faction, int usual) => Math.Max(1, (int)Math.Round(usual * Civ(faction, CivilizationTraits.RaidSize), MidpointRounding.AwayFromZero));

    private static ShipDesign GunDesignOf(Faction faction, bool patrol) => (patrol ? faction.Rules?.Patrol ?? faction.Rules?.Raider : faction.Rules?.Raider ?? faction.Rules?.Patrol) ?? RaiderDesign;

    /// <summary>Its own design with its guns swapped for missile launchers, or the usual missile raider.</summary>
    private static ShipDesign MissileDesignOf(Faction faction, bool patrol)
    {
        var own = GunDesignOf(faction, patrol);
        return own == RaiderDesign ? MissileRaiderDesign : new ShipDesign($"{own.Id}_missiles", $"{own.Name} (missiles)", own.Cells.Select(c => c.PartId == "autocannon" ? c with { PartId = "missile_launcher" } : c));
    }

    /// <summary>Shells or missiles for what it carries.</summary>
    private void Arm(CraftState ship)
    {
        var missiles = ship.Design.Cells.Any(c => c.PartId == "missile_launcher");
        var guns = ship.Design.Cells.Any(c => c.PartId == "autocannon");
        if (guns || !missiles) ship.Inventory.Add(ShellMaterial, RaiderShellsKg);
        if (missiles) ship.Inventory.Add(MissileMaterial, RaiderShellsKg);
        DiscoveredKg += ship.Inventory.TotalKg; // from outside, as the ship (added by the caller)
    }

    /// <summary>A patrol ship's tick: fight what it's hostile to nearby, else go round the planets.</summary>
    private void SteerPatrol(CraftState ship, Faction faction)
    {
        CraftState? target = null;
        var nearest = PatrolRangeM;
        foreach (var craft in _crafts)
        {
            if (craft.FactionId == ship.FactionId || !CanBeShot(craft)) continue;
            var d = craft.Position.DistanceTo(ship.Position);
            if (d < nearest && AreHostile(ship, craft)) { nearest = d; target = craft; }
        }
        if (target is not null) { Attack(ship, target); return; }
        var stops = PatrolStops(faction.HomeStarId);
        if (stops.Count == 0) { ship.Controls = new ShipControls(0, 0, true); return; }
        if (NearPatrolStop(ship, stops[ship.PatrolStop % stops.Count])) ship.PatrolStop = (ship.PatrolStop + 1) % stops.Count;
        FlyToPatrolStop(ship, stops[ship.PatrolStop % stops.Count]);
    }

    private Vector2D PatrolSpot(CraftState craft, CelestialBodyState stop)
    {
        var centre = GetPosition(stop.Id);
        var away = craft.Position - centre;
        return centre + away * ((stop.RadiusM + PatrolAltitudeM) / Math.Max(away.Length, 1e-9));
    }

    private bool NearPatrolStop(CraftState craft, CelestialBodyState stop) => PatrolSpot(craft, stop).DistanceTo(craft.Position) < PatrolArriveM;

    private void FlyToPatrolStop(CraftState craft, CelestialBodyState stop)
    {
        SetTarget(craft.Id, stop.Id);
        craft.Controls = NavigateTo(craft, PatrolSpot(craft, stop), GetVelocityAt(stop.Id, TimeS), BodyId.None, JobCruiseSpeedMps, 20);
    }
}
