using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>A travel leg simulated abstractly (#17): from where it started to a point beside its target, arriving at a set tick.</summary>
public sealed record TransitLeg(long StartTick, long ArriveTick, Vector2D Start, BodyId TargetBody, CraftId TargetCraft, double AngleRad, double RadiusM);

/// <summary>
/// Cheaper simulation for craft far from the player (#17). The piloted craft is the observer; within
/// <see cref="ObserverRadiusM"/> of it everything runs in full detail. Further out:
/// <list type="bullet">
/// <item><b>Transit:</b> an automated craft starting a travel leg longer than <see cref="TransitMinLegM"/>
/// (to its rock, or to a station) doesn't fly it tick by tick. The leg gets a travel time (autopilot
/// cruise speed, or the cruise drive for long legs if it has the fuel, which is burned up front) and
/// the craft moves smoothly along it, then appears beside its target, matched to it, and its job
/// carries on as usual. If the observer comes within range it drops back into normal flight at once.</item>
/// <item><b>Dormant waiting:</b> a far-off automated craft whose job is waiting just moves with what it
/// holds station beside (no gravity, traffic or collision work).</item>
/// </list>
/// Craft in transit or dormant don't collide and aren't pulled by gravity; thrust in transit costs
/// no power (placeholder). A leg is only abstract if its straight line stays two radii clear of
/// every body but its target, so a craft dropping out of it mid-way never appears over a surface. Everything is decided from saved state, so a loaded game continues exactly.
/// Results differ in detail from full simulation (an abstract leg isn't the exact path), but mass is
/// conserved exactly and throughput stays close (tests compare the two).
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Full simulation within this distance of the piloted craft.</summary>
    public const double ObserverRadiusM = 20_000;

    /// <summary>Shorter legs are always flown.</summary>
    public const double TransitMinLegM = 3_000;

    /// <summary>Craft currently in transit or dormant (for diagnostics).</summary>
    public int AbstractCraftCount { get; private set; }

    private Vector2D? ObserverPosition =>
        !PilotCraftId.IsNone && _craftsById.TryGetValue(PilotCraftId, out var pilot) ? pilot.Position : null;

    /// <summary>
    /// Simulate everything in full, wherever the observer is (for tests that compare the two, and for
    /// debugging). A run setting, not saved.
    /// </summary>
    public bool FullDetailEverywhere { get; set; }

    private bool FarFromObserver(Vector2D point) =>
        !FullDetailEverywhere && (ObserverPosition is not { } observer || observer.DistanceTo(point) > ObserverRadiusM);

    /// <summary>Where a craft is (and how it moves) at a time, for craft on rails, landed or docked; otherwise now.</summary>
    private (Vector2D Position, Vector2D Velocity) CraftMotionAt(CraftState craft, double timeS)
    {
        if (craft.RailOrbit is { } rail) return (RailPositionAt(rail, timeS), RailVelocityAt(rail, timeS));
        if (craft.IsLanded) return (GetPositionAt(craft.LandedOnBodyId, timeS) + craft.LandedOffset, GetVelocityAt(craft.LandedOnBodyId, timeS));
        if (craft.IsDocked && _craftsById.TryGetValue(craft.DockedToCraftId, out var station)) return CraftMotionAt(station, timeS);
        return (craft.Position, craft.Velocity);
    }

    /// <summary>The leg's arrival point and velocity at a time (its target moves on).</summary>
    private (Vector2D Position, Vector2D Velocity) TransitGoal(TransitLeg leg, double timeS)
    {
        var (centre, velocity) = !leg.TargetCraft.IsNone && _craftsById.TryGetValue(leg.TargetCraft, out var target)
            ? CraftMotionAt(target, timeS)
            : (GetPositionAt(leg.TargetBody, timeS), GetVelocityAt(leg.TargetBody, timeS));
        return (centre + new Vector2D(Math.Cos(leg.AngleRad), Math.Sin(leg.AngleRad)) * leg.RadiusM, velocity);
    }

    /// <summary>
    /// The leg a craft's job is on now, if it's a travel leg that may go abstract: target and arrival
    /// spot. A target in another star system means the warp gate on the way there (#23); none if
    /// there's no route.
    /// </summary>
    private (BodyId Body, CraftId Craft, double Angle, double Radius)? TravelTarget(CraftState craft)
    {
        if (TravelTargetForJob(craft) is not { } leg) return null;
        if (JobTargetPoint(craft) is not { } point) return leg;
        var gate = GateHop(craft, point, out var noRoute);
        if (gate is null) return noRoute ? null : leg; // elsewhere with no route: no leg to fly
        var from = craft.Position - gate.Position;
        return (BodyId.None, gate.Id, Math.Atan2(from.Y, from.X), gate.CollisionRadiusM + craft.CollisionRadiusM + HoldDistanceM + 40);
    }

    /// <summary>The leg the job itself is on (its own target, wherever it is).</summary>
    private (BodyId Body, CraftId Craft, double Angle, double Radius)? TravelTargetForJob(CraftState craft)
    {
        CraftState? station = null;
        var yardId = craft.YardOrder?.YardId ?? craft.ServiceAt; // going to a yard for a refit (#56) or repairs (#3)
        if (!yardId.IsNone && _craftsById.TryGetValue(yardId, out var yard)) station = yard;
        else switch (craft.Job)
        {
            case SurveyJob { Active: true, State: SurveyJobState.Flying } scout when _byId.TryGetValue(scout.TargetBodyId, out var unsurveyed):
                // A scout surveys on the way in, so the leg ends where it's in range.
                var from = craft.Position - GetPosition(unsurveyed.Id);
                return (unsurveyed.Id, CraftId.None, Math.Atan2(from.Y, from.X), SurveyRadius(unsurveyed, craft));
            case TradeJob { Active: true, State: TradeJobState.ToHome } t1 when _craftsById.TryGetValue(t1.HomeId, out var homeStation):
                station = homeStation; break;
            case TradeJob { Active: true, State: TradeJobState.ToTrader } t2 when _craftsById.TryGetValue(t2.TraderId, out var trader):
                station = trader; break;
            case DefendJob { Active: true, State: DefendState.Rearming } r when _craftsById.TryGetValue(r.RearmAt, out var depot):
                station = depot; break;
            case DefendJob { Active: true, Mode: not DefendMode.Patrol, State: DefendState.OnStation } g when _craftsById.TryGetValue(g.PostId, out var post) && !post.IsWreck:
                station = post; break;
            case DefendJob { Active: true, Mode: DefendMode.Patrol, State: DefendState.OnStation } round when PatrolStops(round.StarId) is { Count: > 0 } stops:
                var stop = stops[round.PatrolIndex % stops.Count];
                var here = craft.Position - GetPosition(stop.Id);
                return (stop.Id, CraftId.None, Math.Atan2(here.Y, here.X), stop.RadiusM + PatrolAltitudeM);
            case SalvageJob { Active: true, State: SalvageJobState.ToWreck } s when _craftsById.TryGetValue(s.WreckId, out var wreck) && wreck.IsWreck:
                station = wreck; break;
            case SalvageJob { Active: true, State: SalvageJobState.ToDestination } s2 when _craftsById.TryGetValue(s2.DestinationId, out var d5):
                station = d5; break;
            case MiningJob { Active: true, State: MiningJobState.ToSource } mining when _byId.TryGetValue(mining.SourceBodyId, out var source):
                if (MineableSurveyed(source, mining.MaterialId) == 0) return null; // only surveyed sites count (#70), as the laws allow (#36)
                SurfacePoint(source, craft, JobHoverAltitudeM, mining); // picks this trip's spot if not yet
                return (source.Id, CraftId.None, mining.SpotBearingRad!.Value, source.RadiusM + craft.CollisionRadiusM + JobHoverAltitudeM);
            case MiningJob { Active: true, State: MiningJobState.ToDestination } m when _craftsById.TryGetValue(m.DestinationId, out var d):
                station = d; break;
            case HaulJob { Active: true, State: HaulJobState.ToPickup } h when _craftsById.TryGetValue(h.PickupId, out var p):
                station = p; break;
            case HaulJob { Active: true, State: HaulJobState.ToDropoff } h2 when _craftsById.TryGetValue(h2.DestinationId, out var d2):
                station = d2; break;
            case ServeRequestsJob { Active: true, State: HaulJobState.ToPickup, Contract: { } c } when _craftsById.TryGetValue(c.PickupId, out var p3):
                station = p3; break;
            case ServeRequestsJob { Active: true, State: HaulJobState.ToDropoff, Contract: { } c4 } when _craftsById.TryGetValue(c4.DestinationId, out var d4):
                station = d4; break;
        }
        if (station is null) return null;
        // Arrive just outside the station's holding ring, on the side we come from: the docking approach takes over there.
        var bearing = craft.Position - station.Position;
        return (BodyId.None, station.Id, Math.Atan2(bearing.Y, bearing.X), station.CollisionRadiusM + craft.CollisionRadiusM + HoldDistanceM + 40);
    }

    /// <summary>Starts an abstract leg if the craft is far from the observer, on a long leg whose end is far too.</summary>
    private bool TryStartTransit(CraftState craft)
    {
        if (craft.Transit is not null || !craft.IsAutomated || craft.IsDocked || craft.IsLanded || craft.IsOnRails || craft.Cruising) return false;
        // Checked once a second per craft (staggered by id), not every tick: the leg checks aren't free.
        if ((Tick + craft.Id.Value) % FixedStepClock.TicksPerSecond != 0) return false;
        if (!Operational(craft) || craft.Stats.TotalThrustN <= 0 || !FarFromObserver(craft.Position)) return false;
        if (TravelTarget(craft) is not { } target) return false;

        var leg = new TransitLeg(Tick, Tick, craft.Position, target.Body, target.Craft, target.Angle, target.Radius);
        var (goalNow, _) = TransitGoal(leg, TimeS);
        var distance = goalNow.DistanceTo(craft.Position);
        if (distance < TransitMinLegM) return false;

        // Travel time: at autopilot speed, or the cruise drive for long legs if the fuel is there. One refinement for the moving target.
        var accel = craft.Stats.TotalThrustN / craft.MassKg;
        var useCruise = distance > AutopilotCruiseMinDistanceM;
        double Duration(double d, bool cruise) => cruise
            ? (d - AutopilotCruiseArriveM) / CruiseSpeedMps + AutopilotCruiseArriveM / JobCruiseSpeedMps + 2 * JobCruiseSpeedMps / accel
            : d / JobCruiseSpeedMps + 2 * JobCruiseSpeedMps / accel;
        var seconds = Duration(distance, useCruise);
        var (goalLater, _) = TransitGoal(leg, TimeS + seconds);
        if (!FarFromObserver(goalLater)) return false; // it would arrive where it can be seen: fly it for real
        seconds = Duration(goalLater.DistanceTo(craft.Position), useCruise);

        if (useCruise)
        {
            var fuel = (long)Math.Ceiling(CruiseFuelKgPerS(craft) * ((goalLater.DistanceTo(craft.Position) - AutopilotCruiseArriveM) / CruiseSpeedMps));
            if (craft.Inventory[FuelMaterial] >= fuel)
            {
                craft.Inventory.TryRemove(new Dictionary<string, long> { [FuelMaterial] = fuel });
                _ejectedKg[FuelMaterial] = (_ejectedKg.TryGetValue(FuelMaterial, out var kg) ? kg : 0) + fuel;
            }
            else seconds = Duration(goalLater.DistanceTo(craft.Position), false); // no fuel: the slow way
        }

        craft.Transit = leg with { ArriveTick = Tick + Math.Max(1, (long)Math.Ceiling(seconds * FixedStepClock.TicksPerSecond)) };
        craft.Controls = ShipControls.Idle;
        return true;
    }

    /// <summary>Moves a craft along its abstract leg; arrives, or drops back to full flight if the observer comes near.</summary>
    private void StepTransit(CraftState craft)
    {
        var leg = craft.Transit!;
        var (goal, goalVelocity) = TransitGoal(leg, TimeS);
        craft.PreviousPosition = craft.Position;
        craft.PreviousHeadingRad = craft.HeadingRad;
        if (Tick >= leg.ArriveTick || !FarFromObserver(craft.Position))
        {
            if (Tick >= leg.ArriveTick) craft.Position = goal;
            craft.Velocity = goalVelocity; // matched to the target: the job (or the player watching) carries on from here
            craft.Transit = null;
            return;
        }
        var fraction = (double)(Tick - leg.StartTick) / (leg.ArriveTick - leg.StartTick);
        var position = leg.Start + (goal - leg.Start) * fraction;
        craft.Velocity = (position - craft.Position) * FixedStepClock.TicksPerSecond;
        craft.Position = position;
        var direction = goal - leg.Start;
        if (direction.Length > 1e-6) craft.HeadingRad = Math.Atan2(direction.X, -direction.Y);
    }

    /// <summary>Restores a saved abstract leg.</summary>
    public void RestoreTransit(CraftId craftId, TransitLeg? leg)
    {
        var craft = GetCraft(craftId);
        if (leg is not null)
        {
            if (leg.ArriveTick < leg.StartTick) throw new ArgumentException("Transit arrives before it starts.");
            if (leg.TargetCraft.IsNone) GetBody(leg.TargetBody); else GetCraft(leg.TargetCraft);
        }
        craft.Transit = leg;
    }

    /// <summary>
    /// Marks, for this tick, far-off automated craft whose job is waiting as dormant: they move with
    /// what they hold beside instead of being flown (see the flight loop). Recomputed every tick from saved state.
    /// </summary>
    private void MarkDormant()
    {
        var count = 0;
        foreach (var craft in _crafts)
        {
            craft.Dormant = craft.Transit is null && craft.IsAutomated && IsHolding(craft) && !craft.IsDocked && !craft.IsLanded
                            && !craft.IsOnRails && !craft.Cruising && FarFromObserver(craft.Position);
            if (craft.Dormant || craft.Transit is not null) count++;
        }
        AbstractCraftCount = count;
    }
}
