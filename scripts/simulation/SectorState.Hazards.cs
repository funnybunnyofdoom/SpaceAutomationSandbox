using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

public enum HazardKind { SolarFlare, MeteorShower, Comet }

/// <summary>Something happening, or about to happen, in a star system (#123). Forecast until <see cref="StartTick"/>.</summary>
public sealed class Hazard
{
    public Hazard(int id, HazardKind kind, BodyId star, BodyId body, long startTick, long endTick, ulong roll) =>
        (Id, Kind, Star, Body, StartTick, EndTick, Roll) = (id, kind, star, body, startTick, endTick, roll);

    public int Id { get; }
    public HazardKind Kind { get; }
    public BodyId Star { get; }

    /// <summary>Meteors: the planet they cross the neighbourhood of. A comet: its body, once it's here.</summary>
    public BodyId Body { get; internal set; }
    public long StartTick { get; }
    public long EndTick { get; }
    public ulong Roll { get; }
    public bool Begun { get; internal set; }

    /// <summary>A comet's leaving has been announced.</summary>
    public bool Warned { get; internal set; }

    /// <summary>Meteorites that came down, kg (meteors).</summary>
    public long FallenKg { get; internal set; }
}

/// <summary>A rock of a meteor shower. It's too small for the mass ledger; what comes down on a body is counted there.</summary>
public sealed class Meteor
{
    public Vector2D Position { get; internal set; }
    public Vector2D Velocity { get; internal set; }
    public long ExpireTick { get; internal set; }
}

/// <summary>
/// Hazards and events (#123): solar flares, meteor showers and comets. The creator's choices: costly, never fatal (parts wear down
/// to <see cref="HazardFloor"/> and no further, none break off); one about every two hours in each system at Normal, forecast
/// <see cref="ForecastTicks"/> ahead; ships with jobs shelter by default.
/// <list type="bullet">
/// <item><b>Flares:</b> panels in the light of the star give nothing, and parts in the open wear. A body's shadow, being docked, or a
/// charged shield protects.</item>
/// <item><b>Meteors:</b> rocks fly at craft near a planet. Moving, shields, weapons and docking help. Some come down on moons and
/// rocks and leave ore there.</item>
/// <item><b>Comets:</b> a body of rare ices, here for some hours, on a long ellipse past the star.</item>
/// </list>
/// Only the player's craft are affected; swarms and craft on an abstract leg aren't. Nothing here depends on the observer.
/// </summary>
public sealed partial class SectorState
{
    public const long ForecastTicks = 5 * 60 * FixedStepClock.TicksPerSecond;

    /// <summary>Ships with jobs go to shelter this long before a flare or meteors start.</summary>
    public const long ShelterLeadTicks = 3 * 60 * FixedStepClock.TicksPerSecond;

    /// <summary>Parts wear down to this and no further.</summary>
    public const double HazardFloor = 0.4;

    /// <summary>How far from a planet's surface its meteors fly.</summary>
    public const double MeteorZoneM = 60_000;

    /// <summary>The furthest a ship goes for shelter.</summary>
    public const double ShelterRangeM = 60_000;

    public const double MeteorSpeedMps = 150, MeteorRadiusM = 6, MeteorDamage = 0.5;

    /// <summary>Comets' bodies have ids from here (see <see cref="PointBodyIdBase"/>).</summary>
    public const uint CometBodyIdBase = 2_000_000;

    private const int MeteorEveryTicks = 5 * FixedStepClock.TicksPerSecond;
    private const int MeteorFlightS = 20;
    private const int FlareWearTicks = 30 * FixedStepClock.TicksPerSecond;
    private const double FlareWear = 0.1;
    private const double MeteorShotChance = 0.5;
    private const long CometLeavingTicks = 10 * 60 * FixedStepClock.TicksPerSecond;

    private readonly List<Hazard> _hazards = new();
    private readonly List<Meteor> _meteors = new();
    private readonly SortedDictionary<(uint Star, HazardKind Kind), long> _hazardNext = new();
    private int _nextHazardId = 1;

    /// <summary>Forecast and under way, in id order.</summary>
    public IReadOnlyList<Hazard> Hazards => _hazards;
    public IReadOnlyList<Meteor> Meteors => _meteors;

    /// <summary>When the next of each kind is due in each system (saved).</summary>
    public IReadOnlyDictionary<(uint Star, HazardKind Kind), long> HazardNext => _hazardNext;
    public int NextHazardId => _nextHazardId;

    internal void RestoreHazard(Hazard hazard)
    {
        if (hazard.Id >= _nextHazardId || _hazards.Any(h => h.Id == hazard.Id)) throw new FormatException($"Hazard {hazard.Id} has an invalid number.");
        _hazards.Add(hazard);
    }

    internal void RestoreHazardState(int nextId) => _nextHazardId = Math.Max(1, nextId);
    internal void RestoreHazardNext(BodyId star, HazardKind kind, long tick) => _hazardNext[(star.Value, kind)] = tick;
    internal void RestoreMeteor(Meteor meteor) => _meteors.Add(meteor);

    public static string OptionOf(HazardKind kind) => kind switch { HazardKind.SolarFlare => WorldOptions.SolarFlares, HazardKind.MeteorShower => WorldOptions.MeteorShowers, _ => WorldOptions.Comets };

    /// <summary>Hours between hazards of one kind in one system, on average; with three kinds, one every 2 h at Normal.</summary>
    public static double MeanHours(WorldAmount amount) => amount switch { WorldAmount.Rare => 18, WorldAmount.Often => 2.25, _ => 6 };

    private DeterministicRandom HazardRandom(uint star, HazardKind kind) =>
        new(Seed ^ (star * 0x9E3779B97F4A7C15UL) ^ ((ulong)(kind + 1) * 0xD1B54A32D192ED03UL) ^ ((ulong)Tick * 0x2545F4914F6CDD1DUL));

    // --- Scheduling ---

    private void StepHazards(double endS)
    {
        if (Tick % FixedStepClock.TicksPerSecond == 0) ScheduleHazards();
        for (var i = _hazards.Count - 1; i >= 0; i--)
        {
            var hazard = _hazards[i];
            if (!hazard.Begun && Tick >= hazard.StartTick) BeginHazard(hazard);
            if (!hazard.Begun) continue;
            if (hazard.Kind == HazardKind.Comet && !_byId.ContainsKey(hazard.Body)) { _hazards.RemoveAt(i); continue; } // mined away
            if (Tick >= hazard.EndTick) { EndHazard(hazard); _hazards.RemoveAt(i); continue; }
            switch (hazard.Kind)
            {
                case HazardKind.SolarFlare: StepFlare(hazard); break;
                case HazardKind.MeteorShower: StepShower(hazard); break;
                case HazardKind.Comet when !hazard.Warned && Tick >= hazard.EndTick - CometLeavingTicks:
                    hazard.Warned = true;
                    if (Concerns(hazard)) AddEvent($"{GetBody(hazard.Body).Name} leaves in 10 minutes.", EventKind.Discovery, new TargetRef(CraftId.None, hazard.Body));
                    break;
            }
        }
        if (_meteors.Count > 0) StepMeteors(endS);
    }

    private void ScheduleHazards()
    {
        foreach (var star in _stars)
            foreach (var kind in new[] { HazardKind.SolarFlare, HazardKind.MeteorShower, HazardKind.Comet })
            {
                var amount = Options[OptionOf(kind)];
                var key = (star.Id.Value, kind);
                if (amount == WorldAmount.Off) { _hazardNext.Remove(key); continue; }
                var mean = MeanHours(amount) * 3600 * FixedStepClock.TicksPerSecond;
                // A first one, or the option now has more of them: drawn (again).
                if (!_hazardNext.TryGetValue(key, out var due) || due - Tick > 1.5 * mean + 1)
                {
                    _hazardNext[key] = Tick + (long)(mean * HazardRandom(key.Value, kind).Range(0.5, 1.5));
                    continue;
                }
                if (Tick < due) continue;
                var rng = HazardRandom(key.Value, kind);
                _hazardNext[key] = Tick + (long)(mean * rng.Range(0.5, 1.5));
                if (_hazards.All(h => h.Kind != kind || h.Star != star.Id)) Forecast(star, kind, rng);
            }
    }

    private Hazard? Forecast(CelestialBodyState star, HazardKind kind, DeterministicRandom rng, long lead = ForecastTicks)
    {
        var body = BodyId.None;
        if (kind == HazardKind.MeteorShower)
        {
            var planets = _bodies.Where(b => b.Orbit?.Parent == star.Id && b.Kind is BodyKind.Planet or BodyKind.GasGiant && b.Orbit?.Parent == star.Id).ToList();
            if (planets.Count == 0) return null;
            var lived = planets.Where(b => _crafts.Any(c => IsPlayers(c) && !c.IsWreck && InMeteorZone(b, c.Position))).ToList(); // where something's going on, if anywhere
            var among = lived.Count > 0 ? lived : planets;
            body = among[(int)(rng.NextDouble() * among.Count)].Id;
        }
        var minutes = kind switch { HazardKind.SolarFlare => rng.Range(3, 6), HazardKind.MeteorShower => rng.Range(6, 10), _ => rng.Range(180, 270) };
        var start = Tick + lead;
        var hazard = new Hazard(_nextHazardId++, kind, star.Id, body, start, start + (long)(minutes * 60 * FixedStepClock.TicksPerSecond), rng.NextUInt64());
        _hazards.Add(hazard);
        if (lead > 0 && Concerns(hazard))
            AddEvent(kind switch
            {
                HazardKind.SolarFlare => $"{star.Name} will flare in 5 minutes: shadow, docking or shields protect.",
                HazardKind.MeteorShower => $"Meteors will cross {GetBody(body).Name}'s neighbourhood in 5 minutes: dock, move or raise shields.",
                _ => $"A comet will pass {star.Name} in 5 minutes.",
            }, kind == HazardKind.Comet ? EventKind.Discovery : EventKind.Danger, new TargetRef(CraftId.None, body.IsNone ? star.Id : body));
        return hazard;
    }

    /// <summary>DEBUG and tests: one of a kind in the first (or this) system, after this long.</summary>
    public Hazard? DebugHazard(HazardKind kind, long leadTicks = 0, CelestialBodyState? star = null, BodyId near = default)
    {
        var hazard = Forecast(star ?? _stars[0], kind, HazardRandom((star ?? _stars[0]).Id.Value, kind), leadTicks);
        if (hazard is not null && !near.IsNone && kind == HazardKind.MeteorShower) hazard.Body = near;
        return hazard;
    }

    /// <summary>The player has something in its system: worth telling.</summary>
    private bool Concerns(Hazard hazard) => _crafts.Any(c => IsPlayers(c) && !c.IsWreck && StarAt(c.Position)?.Id == hazard.Star);

    private static bool Dangerous(Hazard hazard) => hazard.Kind != HazardKind.Comet;

    private bool InMeteorZone(CelestialBodyState planet, Vector2D point) => GetPosition(planet.Id).DistanceTo(point) <= planet.RadiusM + MeteorZoneM;

    /// <summary>Is this place in a flare or meteor shower's reach?</summary>
    public bool InReach(Hazard hazard, Vector2D point) => hazard.Kind switch
    {
        HazardKind.SolarFlare => StarAt(point)?.Id == hazard.Star,
        HazardKind.MeteorShower => _byId.TryGetValue(hazard.Body, out var planet) && InMeteorZone(planet, point),
        _ => false,
    };

    private void BeginHazard(Hazard hazard)
    {
        hazard.Begun = true;
        var star = GetBody(hazard.Star);
        if (hazard.Kind == HazardKind.Comet) MakeComet(hazard, star);
        if (!Concerns(hazard)) return;
        switch (hazard.Kind)
        {
            case HazardKind.SolarFlare: AddEvent($"{star.Name} is flaring.", EventKind.Danger, TargetRef.Of(star)); break;
            case HazardKind.MeteorShower when _byId.TryGetValue(hazard.Body, out var planet): AddEvent($"Meteors are crossing {planet.Name}'s neighbourhood.", EventKind.Danger, TargetRef.Of(planet)); break;
            case HazardKind.Comet: AddEvent($"{GetBody(hazard.Body).Name} is passing: rare ices, for the next {(hazard.EndTick - Tick) / (3600.0 * FixedStepClock.TicksPerSecond):0.0} hours.", EventKind.Discovery, new TargetRef(CraftId.None, hazard.Body)); break;
        }
    }

    private void EndHazard(Hazard hazard)
    {
        var concerns = Concerns(hazard);
        switch (hazard.Kind)
        {
            case HazardKind.SolarFlare:
                if (concerns) AddEvent($"{GetBody(hazard.Star).Name}'s flare is over.", EventKind.Other, new TargetRef(CraftId.None, hazard.Star));
                break;
            case HazardKind.MeteorShower:
                if (concerns && _byId.TryGetValue(hazard.Body, out var planet))
                    AddEvent($"The meteors near {planet.Name} have passed." + (hazard.FallenKg > 0 ? $" {hazard.FallenKg / 1000.0:0} t of ore came down on bodies there." : ""), EventKind.Other, TargetRef.Of(planet));
                break;
            default:
                var comet = GetBody(hazard.Body);
                if (concerns) AddEvent($"{comet.Name} has left.", EventKind.Discovery);
                var before = ComputeMassLedger().TotalKg;
                DetachFrom(comet);
                DiscoveredKg += ComputeMassLedger().TotalKg - before; // what's left of it goes with it
                break;
        }
    }

    // --- Wear ---

    /// <summary>Wears a part, down to <see cref="HazardFloor"/>: shields first, and armour as against weapons.</summary>
    private void Wear(CraftState craft, int cellIndex, double amount)
    {
        var cell = craft.Design.CellArray[cellIndex];
        amount = AbsorbWithShield(craft, amount);
        if (amount <= 0) return;
        amount /= Math.Max(1, Parts!.Get(cell.PartId).Toughness);
        var health = craft.HealthList.GetValueOrDefault(cell.Pos, 1);
        if (health <= HazardFloor) return;
        craft.HealthList[cell.Pos] = Math.Max(HazardFloor, health - amount);
        craft.HealthChanged(Parts);
    }

    private bool AnyFlare()
    {
        foreach (var hazard in _hazards) if (hazard.Kind == HazardKind.SolarFlare && hazard.Begun) return true;
        return false;
    }

    /// <summary>The star whose light this is, is flaring.</summary>
    public bool IsFlaring(Vector2D point)
    {
        foreach (var hazard in _hazards)
            if (hazard.Kind == HazardKind.SolarFlare && hazard.Begun && StarAt(point)?.Id == hazard.Star) return true;
        return false;
    }

    private void StepFlare(Hazard hazard)
    {
        if (Parts is null) return;
        foreach (var craft in _crafts)
        {
            if ((Tick + craft.Id.Value * 7) % FlareWearTicks != 0) continue; // each twice a minute, not all at once
            if (!IsPlayers(craft) || craft.IsWreck || craft.IsDocked || craft.Transit is not null || StarAt(craft.Position)?.Id != hazard.Star || InShadowAt(craft.Position)) continue;
            var rng = new DeterministicRandom(hazard.Roll ^ (craft.Id.Value * 0x9E3779B97F4A7C15UL) ^ (ulong)Tick);
            var cells = craft.Design.CellArray.Length;
            for (var n = Math.Clamp(cells / 6, 1, 12); n > 0; n--) Wear(craft, (int)(rng.NextDouble() * cells), FlareWear);
        }
    }

    // --- Meteors ---

    private void StepShower(Hazard hazard)
    {
        if ((Tick - hazard.StartTick) % MeteorEveryTicks != 0 || !_byId.TryGetValue(hazard.Body, out var planet)) return;
        var rng = new DeterministicRandom(hazard.Roll ^ ((ulong)Tick * 0x9E3779B97F4A7C15UL));
        if (rng.NextDouble() < 0.15) { FallMeteorite(hazard, planet, rng); return; }
        var craft = Choose(c => IsPlayers(c) && !c.IsWreck && !c.IsDocked && c.Transit is null && !c.Cruising && InMeteorZone(planet, c.Position));
        if (craft is null) return;
        // It's aimed at where the craft will be if it carries on, with some luck either way.
        var soon = TimeS + MeteorFlightS;
        var at = craft.RailOrbit is { } rail ? RailPositionAt(rail, soon)
            : craft.IsLanded ? GetPositionAt(craft.LandedOnBodyId, soon) + craft.LandedOffset
            : craft.Position + craft.Velocity * MeteorFlightS;
        var nearest = NearestBody(craft.Position).Body;
        var out_ = at - GetPositionAt(nearest.Id, soon);
        var outward = Math.Atan2(out_.Y, out_.X);
        var bearing = outward + rng.Range(-1.1, 1.1); // from above, not through the ground
        var from = new Vector2D(Math.Cos(bearing), Math.Sin(bearing));
        var aside = new Vector2D(-from.Y, from.X) * (rng.Range(-3, 3) * Math.Max(20, craft.CollisionRadiusM));
        _meteors.Add(new Meteor
        {
            Position = at + aside + from * (MeteorSpeedMps * MeteorFlightS), Velocity = from * -MeteorSpeedMps, ExpireTick = Tick + 2 * MeteorFlightS * FixedStepClock.TicksPerSecond,
        });

        CraftState? Choose(Func<CraftState, bool> which)
        {
            var count = 0;
            CraftState? chosen = null;
            foreach (var c in _crafts) if (which(c) && rng.NextDouble() * ++count < 1) chosen = c;
            return chosen;
        }
    }

    /// <summary>A bigger one comes down on a moon or rock there and leaves its ore at the surface.</summary>
    private void FallMeteorite(Hazard hazard, CelestialBodyState planet, DeterministicRandom rng)
    {
        var bodies = _bodies.Where(b => b.IsMineable && b.IsDismantlable && (b.Id == planet.Id || b.Orbit?.Parent == planet.Id && b.Id.Value < PointBodyIdBase) && b.Sites.Count > 0).ToList();
        if (bodies.Count == 0) return;
        var body = bodies[(int)(rng.NextDouble() * bodies.Count)];
        var material = rng.NextDouble() < 0.2 ? "exotic_ore" : rng.NextDouble() < 0.5 ? "copper_ore" : "iron_ore";
        if (Parts is { } parts && !parts.Materials.Contains(material)) return;
        var kg = (long)rng.Range(1_000, material == "exotic_ore" ? 3_000 : 15_000);
        var before = ComputeMassLedger().TotalKg;
        body.AddMeteorite(material, kg, rng.Range(-Math.PI, Math.PI));
        DiscoveredKg += ComputeMassLedger().TotalKg - before;
        hazard.FallenKg += kg;
    }

    private void StepMeteors(double endS)
    {
        var positions = AllBodyPositions(endS);
        ShootMeteors();
        for (var m = _meteors.Count - 1; m >= 0; m--)
        {
            var meteor = _meteors[m];
            meteor.Position += meteor.Velocity * FixedStepClock.TickSeconds;
            var spent = Tick >= meteor.ExpireTick;
            for (var i = 0; !spent && i < _bodies.Count; i++) spent = positions[i].DistanceTo(meteor.Position) < _bodies[i].RadiusM;
            if (!spent && Parts is not null)
                foreach (var craft in _crafts)
                {
                    if (!IsPlayers(craft) || craft.IsWreck || craft.IsDocked || craft.Transit is not null || craft.Cruising) continue;
                    if (craft.Position.DistanceTo(meteor.Position) > craft.CollisionRadiusM + MeteorRadiusM + CellContactRadiusM) continue;
                    var (cell, at) = NearestCell(craft, meteor.Position);
                    if (at.DistanceTo(meteor.Position) > MeteorRadiusM + CellContactRadiusM) continue;
                    Make("hit", meteor.Position); // #129
                    Wear(craft, cell, MeteorDamage);
                    spent = true;
                    break;
                }
            if (spent) _meteors.RemoveAt(m);
        }
    }

    /// <summary>Guns, lasers and point defence shoot at rocks in reach; in a fight only point defence does.</summary>
    private void ShootMeteors()
    {
        foreach (var craft in _crafts)
        {
            var weapons = craft.Stats.WeaponList;
            if (weapons.Count == 0 || !IsPlayers(craft) || craft.IsWreck || craft.HoldFire || craft.IsDocked || !CanBeShot(craft) || !Operational(craft)) continue;
            DeterministicRandom? rng = null;
            foreach (var mount in weapons)
            {
                var weapon = mount.Weapon;
                if (weapon.Kind == WeaponKind.Missile || _raiders.Count > 0 && !weapon.PointDefense || weapon.NeedsArmedGates && !LawActive(Laws.ArmedGates)) continue;
                if (craft.WeaponReadyTicks.GetValueOrDefault(mount.Pos) > Tick) continue;
                var muzzle = MountPosition(craft, mount);
                var nearest = -1;
                var distance = weapon.RangeM;
                for (var i = 0; i < _meteors.Count; i++)
                    if (_meteors[i].Position.DistanceTo(muzzle) is var d && d < distance && InArc(craft, mount, _meteors[i].Position - muzzle)) (nearest, distance) = (i, d);
                if (nearest < 0 || !PayForShot(craft, weapon)) continue;
                craft.WeaponReadyTicks[mount.Pos] = Tick + Math.Max(1, (long)Math.Round(weapon.IntervalS * FixedStepClock.TicksPerSecond));
                _beams.Add(new Beam(Tick, muzzle, _meteors[nearest].Position, craft.FactionId));
                rng ??= new DeterministicRandom(Seed ^ (craft.Id.Value * 0xD1B54A32D192ED03UL) ^ ((ulong)Tick * 0x9E3779B97F4A7C15UL));
                if (rng.NextDouble() < MeteorShotChance * mount.Health) _meteors.RemoveAt(nearest);
            }
        }
    }

    // --- Comets ---

    private void MakeComet(Hazard hazard, CelestialBodyState star)
    {
        var rng = new DeterministicRandom(hazard.Roll);
        const double perihelion = 320_000, aphelion = 2_000_000, period = 24 * 3600;
        var axis = (perihelion + aphelion) / 2;
        // Nearest the star halfway through its visit.
        var middle = (hazard.StartTick + hazard.EndTick) / 2 * FixedStepClock.TickSeconds;
        var orbit = new CircularOrbit(star.Id, axis, period, -2 * Math.PI * (middle / period % 1)) { Eccentricity = (aphelion - perihelion) / (aphelion + perihelion), PeriapsisRad = rng.Range(-Math.PI, Math.PI) };
        var deposits = new Dictionary<string, long>
        {
            ["ice"] = (long)rng.Range(200_000, 500_000), ["methane_ice"] = (long)rng.Range(150_000, 400_000), ["deuterium"] = (long)rng.Range(1_000, 4_000), ["exotic_ore"] = (long)rng.Range(4_000, 15_000),
        };
        if (Parts is { } parts) foreach (var id in deposits.Keys.Where(id => !parts.Materials.Contains(id)).ToList()) deposits.Remove(id);
        var body = new CelestialBodyState(new BodyId(CometBodyIdBase + (uint)hazard.Id), $"Comet {hazard.Id}", BodyKind.Asteroid,
            Math.Round(CelestialBodyState.MassFromRadius(rng.Range(250, 450), SectorGenerator.IceDensityKgPerM3)), SectorGenerator.IceDensityKgPerM3, true, orbit, deposits);
        var before = ComputeMassLedger().TotalKg;
        AddBody(body);
        DiscoveredKg += ComputeMassLedger().TotalKg - before;
        hazard.Body = body.Id;
    }

    /// <summary>What a hazard is and how long, for the HUD. Null if its place is gone.</summary>
    public string? HazardText(Hazard hazard)
    {
        if (!_byId.TryGetValue(hazard.Star, out var star) || hazard.Kind == HazardKind.MeteorShower && !_byId.ContainsKey(hazard.Body)) return null;
        var seconds = Math.Max(0, ((hazard.Begun ? hazard.EndTick : hazard.StartTick) - Tick) / FixedStepClock.TicksPerSecond);
        var time = seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
        var what = hazard.Kind switch
        {
            HazardKind.SolarFlare => $"Solar flare from {star.Name}",
            HazardKind.MeteorShower => $"Meteors near {GetBody(hazard.Body).Name}",
            _ => _byId.TryGetValue(hazard.Body, out var comet) ? $"{comet.Name} is passing" : $"A comet comes to {star.Name}",
        };
        return hazard.Begun ? $"{what}: {time} left" : $"{what} in {time}";
    }

    public static bool IsComet(CelestialBodyState body) => body.Id.Value >= CometBodyIdBase;

    // --- Shelter ---

    /// <summary>The flare or meteors a craft should shelter from now, or null.</summary>
    private Hazard? Threat(CraftState craft)
    {
        foreach (var hazard in _hazards)
            if (Dangerous(hazard) && Tick >= hazard.StartTick - ShelterLeadTicks && InReach(hazard, craft.Position)) return hazard;
        return null;
    }

    /// <summary>
    /// Ships with jobs go and dock until it's over, unless told to keep working, shielded, landed, or there's no free port near. Returns true while
    /// it flies (or holds) the craft.
    /// </summary>
    private bool StepShelter(CraftState craft, ShipJob job)
    {
        if (_hazards.Count == 0 && craft.ShelterAt.IsNone) return false;
        var threat = craft.KeepWorking || craft.ShieldCharge > 0 || job is DefendJob ? null : Threat(craft);
        if (threat is null)
        {
            if (craft.ShelterAt.IsNone) return false;
            if (craft.DockedToCraftId == craft.ShelterAt) TryUndock(craft.Id);
            craft.ShelterAt = CraftId.None;
            return false;
        }
        var why = threat.Kind == HazardKind.SolarFlare ? "solar flare" : "meteors";
        if (craft.IsDocked)
        {
            craft.Controls = ShipControls.Idle;
            job.ServiceStatus = $"sheltering at {GetCraft(craft.DockedToCraftId).Name} ({why})";
            return true;
        }
        if (craft.IsLanded || !CanBreakForService(job)) return false;
        if (craft.ShelterAt.IsNone)
        {
            if ((Tick + craft.Id.Value) % FixedStepClock.TicksPerSecond != 0 || NearestShelter(craft) is not { } station) return false;
            craft.ShelterAt = station.Id;
        }
        if (!_craftsById.TryGetValue(craft.ShelterAt, out var shelter) || shelter.IsWreck || shelter.Stats.DockingPorts.Count == 0)
        {
            craft.ShelterAt = CraftId.None;
            return false;
        }
        if (FlyToDock(craft, shelter, claimBerth: true) == DockApproach.NoFreePort)
        {
            craft.ShelterAt = CraftId.None; // others were quicker
            return false;
        }
        job.ServiceStatus = craft.IsDocked ? $"sheltering at {shelter.Name} ({why})" : $"going to {shelter.Name} for shelter ({why})";
        return true;
    }

    private CraftState? NearestShelter(CraftState craft)
    {
        CraftState? best = null;
        var nearest = ShelterRangeM;
        foreach (var station in _crafts)
        {
            if (station.Id == craft.Id || station.IsWreck || !IsPlayers(station) || Movable(station) || station.Stats.DockingPorts.Count == 0) continue;
            var distance = station.Position.DistanceTo(craft.Position);
            if (distance >= nearest) continue;
            var taken = 0;
            foreach (var other in _crafts)
                if (other.Id != craft.Id && (other.DockedToCraftId == station.Id || other.DockClaimStation == station.Id && !other.IsDocked || other.ShelterAt == station.Id && !other.IsDocked && other.DockClaimStation != station.Id)) taken++;
            if (taken < station.Stats.DockingPorts.Count) (best, nearest) = (station, distance);
        }
        return best;
    }
}
