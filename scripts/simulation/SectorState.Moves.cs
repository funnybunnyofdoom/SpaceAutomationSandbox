using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>A body's move to a new orbit round the same parent (#127): when, how much rock it throws, and how much has gone.</summary>
public sealed record BodyMove(long StartTick, long EndTick, long ReactionKg, long ThrownKg);

/// <summary>What moving a body would take, or why it can't.</summary>
public sealed record MovePlan(double DeltaVMps, long ReactionKg, int Drivers, double ThrowKgPerS, double Seconds, double PowerKW, IReadOnlyList<string> Problems)
{
    public bool CanStart => Problems.Count == 0;
}

/// <summary>
/// Moving worlds (#127; the creator's choices: to a new orbit round the same parent for now; pushed by mass drivers anchored on the body, which throw
/// its own rock; what's on it and round it comes along, and a move that would come near another body's orbit is refused; not a weapon, and
/// civilizations object). A move is a smooth change of the body's orbit set when it starts, a pure function of time (see
/// <see cref="CircularOrbit.Move"/>), so it's the same at any speed of time and saved exactly. Rock leaves the body as it goes, by
/// the share of the move done, into the ejected-mass ledger. If its drivers lose power the move stalls where it is, on a circular orbit there.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>How fast mass drivers throw rock, m/s: what sets how much rock a move takes.</summary>
    public const double DriverExhaustMps = 10_000;

    /// <summary>A spiral between circular orbits costs this many times the difference in their speeds.</summary>
    public const double SpiralFactor = 1.5;

    /// <summary>Kept from any other body's orbit, beyond both bodies' radii; and from your stations on rails round the same parent.</summary>
    public const double MoveClearanceM = 3_000;

    private readonly Dictionary<BodyId, BodyMove> _moves = new();

    public IReadOnlyDictionary<BodyId, BodyMove> Moves => _moves;

    internal void RestoreMove(BodyId body, BodyMove move) => _moves[body] = move;

    /// <summary>Mass drivers anchored on a body: how many, how fast they throw, and the power they want.</summary>
    public (int Count, double KgPerS, double KW, List<CraftState> Craft) DriversOn(CelestialBodyState body)
    {
        var (count, rate, kw) = (0, 0.0, 0.0);
        var craft = new List<CraftState>();
        if (Parts is null) return (0, 0, 0, craft);
        foreach (var c in _crafts)
        {
            if (!IsPlayers(c) || c.IsWreck || !c.IsAnchored || c.LandedOnBodyId != body.Id) continue;
            var any = false;
            foreach (var cell in c.Design.CellArray)
                if (Parts.Get(cell.PartId) is { MassDriverKgPerS: > 0 } part)
                {
                    var health = c.HealthList.GetValueOrDefault(cell.Pos, 1);
                    (count, rate, kw, any) = (count + 1, rate + part.MassDriverKgPerS * health, kw + part.PowerUseKW, true);
                }
            if (any) craft.Add(c);
        }
        return (count, rate, kw, craft);
    }

    /// <summary>The body's speed on a circle of this radius, by its own orbit's speed and Kepler's law (as the game sets orbits' periods).</summary>
    private static double OrbitSpeed(CircularOrbit orbit, double radiusM) => 2 * Math.PI * orbit.RadiusM / orbit.PeriodS * Math.Sqrt(orbit.RadiusM / radiusM);

    /// <summary>What moving a body to a new orbit radius would take, and what stands in the way.</summary>
    public MovePlan PlanMove(BodyId bodyId, double radiusM)
    {
        var problems = new List<string>();
        var body = GetBody(bodyId);
        if (body.Orbit is not { } orbit || !_byId.TryGetValue(orbit.Parent, out var parent) || body.Kind == BodyKind.Star) return new(0, 0, 0, 0, 0, 0, new[] { "It doesn't orbit anything that can be changed." });
        if (!IsTechUnlocked("planetary_engineering")) problems.Add("Research Planetary engineering first.");
        if (_moves.ContainsKey(bodyId)) problems.Add("It's moving already.");
        if (orbit.Eccentricity > 0 || orbit.Move is not null && !_moves.ContainsKey(bodyId) && orbit.Move.EndS > TimeS) problems.Add("Its orbit can't be changed.");
        if (!body.IsDismantlable) problems.Add("Only rocky or icy bodies have rock for mass drivers to throw.");
        var from = orbit.RadiusM;
        if (!(radiusM > 0) || Math.Abs(radiusM - from) < 100) problems.Add("Choose an orbit at least 100 m from where it is.");
        if (radiusM < parent.RadiusM + body.RadiusM + MoveClearanceM) problems.Add($"Too close to {parent.Name}.");
        var (low, high) = (Math.Min(from, radiusM), Math.Max(from, radiusM));
        foreach (var other in _bodies)
        {
            if (other == body || other.Orbit is not { } o || o.Parent != parent.Id) continue;
            var reach = body.RadiusM + other.RadiusM + MoveClearanceM;
            if (o.RadiusM + reach > low && o.RadiusM - reach < high) { problems.Add($"It would come near {other.Name}'s orbit."); break; }
        }
        foreach (var point in _points)
            if (point.Orbit is { } p && p.Parent == parent.Id && p.RadiusM + body.RadiusM + MoveClearanceM > low && p.RadiusM - body.RadiusM - MoveClearanceM < high) { problems.Add($"It would come near {point.Name}."); break; }
        foreach (var craft in _crafts)
            if (IsPlayers(craft) && craft.RailOrbit is { } rail && rail.Parent == parent.Id && rail.RadiusM + body.RadiusM + MoveClearanceM > low && rail.RadiusM - body.RadiusM - MoveClearanceM < high)
            { problems.Add($"It would pass through {craft.Name}'s orbit."); break; }

        var deltaV = SpiralFactor * Math.Abs(OrbitSpeed(orbit, from) - OrbitSpeed(orbit, radiusM));
        var reaction = (long)Math.Ceiling(body.MassKg * (1 - Math.Exp(-deltaV / DriverExhaustMps)));
        var (count, rate, kw, _) = DriversOn(body);
        if (count == 0) problems.Add($"Anchor mass drivers on {body.Name}.");
        if (reaction > body.BulkKg) problems.Add($"It would take {reaction / 1000.0:N0} t of rock, more than it has to spare.");
        var seconds = rate > 0 ? reaction / rate : double.PositiveInfinity;
        if (seconds < 60) seconds = 60; // never a jump
        return new(deltaV, reaction, count, rate, seconds, kw, problems);
    }

    /// <summary>Starts a move: its orbit becomes the move, and rock begins to leave it.</summary>
    public ActionResult StartMove(BodyId bodyId, double radiusM)
    {
        var plan = PlanMove(bodyId, radiusM);
        if (!plan.CanStart) return new(false, plan.Problems[0]);
        var body = GetBody(bodyId);
        var orbit = body.Orbit!;
        var start = Tick + 1;
        var end = start + (long)Math.Ceiling(plan.Seconds * FixedStepClock.TicksPerSecond);
        body.Orbit = CircularOrbit.Moving(orbit, start * FixedStepClock.TickSeconds, end * FixedStepClock.TickSeconds, radiusM);
        ResetMotionCache();
        _moves[bodyId] = new BodyMove(start, end, plan.ReactionKg, 0);
        Object(body, 30);
        AddEvent($"{body.Name} is moving to a new orbit, {radiusM / 1000:0.#} km from {GetBody(orbit.Parent).Name}: {plan.Seconds / 3600:0.#} h.", EventKind.Milestone, TargetRef.Of(body));
        return new(true, $"{body.Name} is under way: {plan.Seconds / 3600:0.#} h, throwing {plan.ReactionKg / 1000.0:N0} t of its rock.");
    }

    /// <summary>A civilization whose system it's in takes great offence; conservationists anywhere, some.</summary>
    private void Object(CelestialBodyState body, double grievance)
    {
        var owner = ClaimantOf(body.Id);
        if (owner is not null)
        {
            owner.Grievance = Math.Min(100, owner.Grievance + grievance);
            AddEvent($"The {owner.Name} protest: you're moving {body.Name} in their system.", EventKind.Civilization, new TargetRef(CraftId.None, owner.HomeStarId));
        }
        foreach (var faction in _factions.Where(f => f != owner && f.Culture.Id == "conservationist"))
            faction.Grievance = Math.Min(100, faction.Grievance + grievance / 3);
    }

    /// <summary>Each tick: moving bodies throw their share of rock while their drivers have power; without it they stall.</summary>
    private void StepMoves()
    {
        if (_moves.Count == 0) return;
        foreach (var (id, move) in _moves.ToList())
        {
            if (!_byId.TryGetValue(id, out var body)) { _moves.Remove(id); continue; }
            if (Tick < move.StartTick) continue;
            if (Tick >= move.EndTick) { Finish(body, move, move.ReactionKg - move.ThrownKg); continue; }
            var (_, _, kw, drivers) = DriversOn(body);
            var powered = drivers.Count > 0;
            foreach (var craft in drivers) powered &= UseMachinePower(craft, kw / drivers.Count * FixedStepClock.TickSeconds);
            var due = (long)(move.ReactionKg * (double)(Tick + 1 - move.StartTick) / (move.EndTick - move.StartTick)) - move.ThrownKg;
            if (!powered) { Stall(body, move); continue; }
            Throw(body, due);
            _moves[id] = move with { ThrownKg = move.ThrownKg + due };
        }
    }

    private void Throw(CelestialBodyState body, long kg)
    {
        kg = Math.Min(kg, body.BulkKg);
        if (kg <= 0) return;
        body.TakeBulk(kg);
        _ejectedKg[body.BulkMaterialId] = _ejectedKg.GetValueOrDefault(body.BulkMaterialId) + kg; // thrown off: out of the sector
    }

    private void Finish(CelestialBodyState body, BodyMove move, long rest)
    {
        Throw(body, rest);
        body.Orbit = body.Orbit!.Settled();
        ResetMotionCache();
        _moves.Remove(body.Id);
        AddEvent($"{body.Name} is in its new orbit.", EventKind.Milestone, TargetRef.Of(body));
        ReachMilestone("moved_world");
    }

    /// <summary>Its drivers lost power: it stays on a circular orbit where it is.</summary>
    private void Stall(CelestialBodyState body, BodyMove move)
    {
        body.Orbit = body.Orbit!.StoppedAt(TimeS);
        ResetMotionCache();
        _moves.Remove(body.Id);
        AddEvent($"{body.Name}'s move stalled: its mass drivers have no power. It stays where it is.", EventKind.Danger, TargetRef.Of(body));
    }
}
