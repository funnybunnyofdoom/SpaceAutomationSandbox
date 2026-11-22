using System;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Runs mining jobs: each tick, before movement, a craft with an active job gets its controls from
/// the job's state machine (via <see cref="Autopilot"/>) and uses the same actions a player has
/// (anchor, release, dock, undock, transfer). Needs no Godot node: an off-screen miner runs
/// exactly like a visible one. Placeholder numbers below are tuned for the starter sector.
/// </summary>
public sealed partial class SectorState
{
    public const double JobCruiseSpeedMps = 150;
    public const double JobHoverAltitudeM = 60;
    /// <summary>
    /// Climb this far from a surface before heading off. It must be well clear: once the craft brakes
    /// against its next target, the body it left keeps moving (the asteroid at ~60–150 m/s).
    /// </summary>
    public const double JobLiftAltitudeM = 1_000;
    public const double JobLandingSpeedMps = 3;
    public const double JobAvoidClearanceM = 400;
    public const double JobDetourAltitudeM = 1_500;

    /// <summary>
    /// A ship blocked at a station (no room to unload, too little to pick up) undocks, waits nearby and
    /// retries after this long, so it never holds a docking port others need to unblock it.
    /// </summary>
    public const double JobStationRetryS = 30;

    /// <summary>Gives a craft a mining job, refusing (with the reason) if it or the job can't work.</summary>
    public ActionResult StartJob(CraftId craftId, MiningJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        if (craft.Stats.MiningRateKgPerS <= 0) return new(false, $"{craft.Name} has no drill.");
        if (craft.Stats.CargoCapacityKg <= 0) return new(false, $"{craft.Name} has no cargo bay.");
        if (craft.Stats.AnchorClampCount <= 0) return new(false, $"{craft.Name} has no anchor clamp (needed to mine).");
        if (!_byId.TryGetValue(job.SourceBodyId, out var source) || !source.IsMineable)
            return new(false, "The source must be a mineable body.");
        if (!source.Surveyed)
            return new(false, $"{source.Name} hasn't been surveyed: fly a ship you pilot within {SurveyRangeM / 1000:0} km of it first.");
        if (!_craftsById.TryGetValue(job.DestinationId, out var destination) || destination.Id == craft.Id)
            return new(false, "The destination must be another craft.");
        if (destination.Stats.DockingPorts.Count == 0) return new(false, $"{destination.Name} has no docking port.");

        job.Active = true;
        job.WaitReason = null;
        job.State = craft.IsDocked && craft.DockedToCraftId == destination.Id && craft.Inventory[job.MaterialId] > 0
            ? MiningJobState.Unloading
            : craft.IsLanded && craft.LandedOnBodyId == source.Id ? MiningJobState.Landing
            : MiningJobState.ToSource;
        craft.Job = job;
        return new(true, $"{craft.Name}: mining {job.MaterialId} at {source.Name} for {destination.Name}.");
    }

    public ActionResult StopJob(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (craft.Job is not { Active: true } job) return new(false, $"{craft.Name} has no running job.");
        job.Active = false;
        craft.Controls = ShipControls.Idle;
        return new(true, $"{craft.Name}: job stopped.");
    }

    /// <summary>Restores a saved job exactly (no validation of the current situation; the job re-checks every tick).</summary>
    public void RestoreJob(CraftId craftId, ShipJob job) => GetCraft(craftId).Job = job;

    private void StepJobs()
    {
        foreach (var craft in _crafts)
        {
            switch (craft.Job)
            {
                case MiningJob { Active: true } mining: StepJob(craft, mining, allowResume: true); break;
                case HaulJob { Active: true } haul: StepHaul(craft, haul, allowResume: true); break;
                case ServeRequestsJob { Active: true } serve: StepServe(craft, serve, allowResume: true); break;
            }
            craft.Job?.NoteTick(Tick);
        }
    }

    private void StepJob(CraftState craft, MiningJob job, bool allowResume)
    {
        if (!_byId.TryGetValue(job.SourceBodyId, out var source)) { Wait(craft, job, job.State, "the source body is missing"); return; }
        if (!_craftsById.TryGetValue(job.DestinationId, out var destination)) { Wait(craft, job, job.State, "the destination is missing"); return; }
        var material = job.MaterialId;
        var held = craft.Inventory[material];

        switch (job.State)
        {
            case MiningJobState.Waiting:
                if (Tick < job.RetryAtTick) { craft.Controls = new ShipControls(0, 0, !craft.IsDocked); return; }
                // Try again from where we were; the step re-enters Waiting if still blocked.
                job.State = job.ResumeState;
                job.WaitReason = null;
                if (allowResume) StepJob(craft, job, allowResume: false);
                else craft.Controls = new ShipControls(0, 0, true);
                return;

            case MiningJobState.ToSource:
                if (craft.IsDocked) TryUndock(craft.Id);
                if (source.Deposit(material) == 0)
                {
                    if (held > 0) { job.State = MiningJobState.ToDestination; return; }
                    Wait(craft, job, MiningJobState.ToSource, $"{source.Name} has no {material} left");
                    return;
                }
                if (craft.IsLanded)
                {
                    if (craft.LandedOnBodyId == source.Id) { job.State = MiningJobState.Landing; return; }
                    Lift(craft, job, MiningJobState.ToSource);
                    return;
                }
                SetTarget(craft.Id, source.Id);
                var hover = SurfacePoint(source, craft, JobHoverAltitudeM);
                craft.Controls = NavigateTo(craft, hover, GetVelocityAt(source.Id, TimeS), source.Id, JobCruiseSpeedMps, 0);
                if (hover.DistanceTo(craft.Position) < 40 && (craft.Velocity - GetVelocityAt(source.Id, TimeS)).Length < 2)
                    job.State = MiningJobState.Landing;
                return;

            case MiningJobState.Landing:
                if (craft.IsLanded)
                {
                    if (craft.LandedOnBodyId != source.Id) { Lift(craft, job, MiningJobState.ToSource); return; }
                    craft.Controls = ShipControls.Idle;
                    var anchor = craft.IsAnchored ? new ActionResult(true, "") : TryAnchor(craft.Id);
                    if (anchor.Done) job.State = MiningJobState.Mining;
                    else if (craft.Stats.CanAnchor && !AnchorTouchesGround(craft)) Lift(craft, job, MiningJobState.ToSource); // wrong side down: try again
                    else Wait(craft, job, MiningJobState.Landing, anchor.Message);
                    return;
                }
                SetTarget(craft.Id, source.Id);
                craft.Controls = LandOnAnchor(craft, source);
                return;

            case MiningJobState.Mining:
                craft.Controls = ShipControls.Idle;
                if (!craft.IsAnchored) { job.State = MiningJobState.Landing; return; }
                var full = craft.FreeCargoKg == 0 || craft.Inventory.TotalKg >= job.ReturnFraction * craft.CargoCapacityKg;
                if (full || source.Deposit(material) == 0)
                {
                    if (held > 0) { Lift(craft, job, MiningJobState.ToDestination); return; }
                    Wait(craft, job, MiningJobState.Mining,
                        full ? "the hold is full of other cargo" : $"{source.Name} has no {material} left");
                }
                return;

            case MiningJobState.Lifting:
                Lift(craft, job, job.ResumeState);
                return;

            case MiningJobState.ToDestination:
                if (craft.IsLanded) { Lift(craft, job, MiningJobState.ToDestination); return; }
                if (craft.IsDocked && craft.DockedToCraftId == destination.Id) { job.State = MiningJobState.Unloading; return; }
                switch (FlyToDock(craft, destination))
                {
                    case DockApproach.Docked: job.State = MiningJobState.Unloading; break;
                    case DockApproach.NoFreePort: Wait(craft, job, MiningJobState.ToDestination, $"no free docking port at {destination.Name}"); break;
                }
                return;

            case MiningJobState.Unloading:
                craft.Controls = ShipControls.Idle;
                if (!craft.IsDocked) { job.State = MiningJobState.ToDestination; return; }
                var amount = MaxTransferKg(craft.Id, destination.Id, material);
                if (amount > 0 && TryTransfer(craft.Id, destination.Id, material, amount).Done) job.DeliveredKg += amount;
                if (craft.Inventory[material] == 0)
                {
                    job.CompletedCycles++;
                    job.State = MiningJobState.ToSource;
                }
                else if (amount == 0)
                {
                    TryUndock(craft.Id); // don't hold the port while blocked
                    Wait(craft, job, MiningJobState.ToDestination, $"{destination.Name} is full", JobStationRetryS);
                }
                return;
        }
    }

    /// <summary>
    /// Final descent onto a body with the anchor clamp down (#42). Ships only thrust forward, so:
    /// nose-first to build a gentle descent (up to <see cref="JobLandingSpeedMps"/>), then turn the
    /// clamp towards the surface and coast down onto it; brake if descending too fast or sliding.
    /// </summary>
    private ShipControls LandOnAnchor(CraftState craft, CelestialBodyState body)
    {
        var centre = GetPosition(body.Id);
        var bodyVelocity = GetVelocityAt(body.Id, TimeS);
        var down = centre - craft.Position;
        var downUnit = down * (1 / Math.Max(down.Length, 1e-9));
        var relative = craft.Velocity - bodyVelocity;
        var descent = relative.X * downUnit.X + relative.Y * downUnit.Y;
        var lateral = (relative - downUnit * descent).Length;

        if (descent > JobLandingSpeedMps + 1 || lateral > 2) return new ShipControls(0, 0, true); // stop relative to the body
        if (descent < 0.5)
            return Autopilot.Approach(craft, centre, bodyVelocity, JobLandingSpeedMps, arrivalSpeed: JobLandingSpeedMps); // nose down, build descent
        var heading = AnchorDownHeading(craft, centre);
        var turn = heading is { } h
            ? Math.Clamp(FlightModel.WrapAngle(h - craft.HeadingRad) / (FlightModel.TurnRateRadPerS * FixedStepClock.TickSeconds), -1, 1)
            : 0;
        return new ShipControls(0, turn, false); // coast down, clamp first
    }

    private void Wait(CraftState craft, MiningJob job, MiningJobState resume, string reason, double retryAfterS = 0)
    {
        job.RetryAtTick = Tick + (long)(retryAfterS * FixedStepClock.TicksPerSecond);
        job.ResumeState = resume;
        job.WaitReason = reason;
        job.State = MiningJobState.Waiting;
        craft.Controls = new ShipControls(0, 0, true); // hold still relative to the current target
    }

    /// <summary>Releases the anchor and climbs straight away from the surface, then continues with <paramref name="next"/>.</summary>
    private void Lift(CraftState craft, MiningJob job, MiningJobState next)
    {
        job.State = MiningJobState.Lifting;
        job.ResumeState = next;
        if (craft.IsAnchored && !TryRelease(craft.Id).Done) { Wait(craft, job, MiningJobState.Lifting, "can't release the anchor"); return; }
        var (body, altitude) = NearestBody(craft.Position);
        var outward = craft.Position - GetPosition(body.Id);
        if (!craft.IsLanded && altitude - craft.CollisionRadiusM > JobLiftAltitudeM)
        {
            job.State = next;
            craft.Controls = ShipControls.Idle;
            return;
        }
        craft.Controls = Autopilot.ThrustAlong(craft, outward * (1 / Math.Max(outward.Length, 1e-9)));
    }

    /// <summary>Point above (or, with negative altitude, just inside) the body's surface on the craft's side.</summary>
    private Vector2D SurfacePoint(CelestialBodyState body, CraftState craft, double altitudeM)
    {
        var centre = GetPosition(body.Id);
        var outward = craft.Position - centre;
        var unit = outward.Length > 1e-9 ? outward * (1 / outward.Length) : new Vector2D(0, -1);
        return centre + unit * (body.RadiusM + craft.CollisionRadiusM + altitudeM);
    }

    /// <summary>Seconds ahead the autopilot looks for an impact along its current velocity.</summary>
    public const double JobImpactHorizonS = 6;

    /// <summary>
    /// Approach a goal safely:
    /// 1. If the ship's current velocity would hit a body other than <paramref name="ignore"/> within
    ///    <see cref="JobImpactHorizonS"/>, thrust straight away from that body first (#41).
    /// 2. If the straight path to the goal passes too close to a body, circle it high above its surface:
    ///    aim for a waypoint up to 45° further round, choosing the biggest step whose own path is clear
    ///    (down to straight up, which always is), re-checked every tick.
    /// 3. Otherwise fly straight at the goal.
    /// </summary>
    private ShipControls NavigateTo(CraftState craft, Vector2D goal, Vector2D goalVelocity, BodyId ignore, double maxSpeed, double arrivalSpeed)
    {
        if (CruiseLeg(craft, goal, goalVelocity) is { } cruise) return cruise; // long legs between planets (#53)
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Id == ignore) continue;
            var centre = BodyPositionAt(i, TimeS);
            var relative = craft.Velocity - BodyVelocityAt(i, TimeS);
            if (WillHit(craft.Position, relative, centre, body.RadiusM + craft.CollisionRadiusM + 20, JobImpactHorizonS))
            {
                var away = craft.Position - centre;
                return Autopilot.ThrustAlong(craft, away * (1 / Math.Max(away.Length, 1e-9)));
            }
        }

        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Id == ignore) continue;
            var centre = BodyPositionAt(i, TimeS);
            var clearance = body.RadiusM + craft.CollisionRadiusM + JobAvoidClearanceM;
            if (SegmentClears(craft.Position, goal, centre, clearance)) continue;

            var fromAngle = Math.Atan2(craft.Position.Y - centre.Y, craft.Position.X - centre.X);
            var toAngle = Math.Atan2(goal.Y - centre.Y, goal.X - centre.X);
            var turn = FlightModel.WrapAngle(toAngle - fromAngle);
            var radius = body.RadiusM + JobDetourAltitudeM;
            var waypoint = centre + new Vector2D(Math.Cos(fromAngle) * radius, Math.Sin(fromAngle) * radius); // straight up: always clear
            foreach (var maxStep in new[] { Math.PI / 4, Math.PI / 6, Math.PI / 9, Math.PI / 18 })
            {
                var step = Math.Sign(turn) * Math.Min(Math.Abs(turn), maxStep);
                var candidate = centre + new Vector2D(Math.Cos(fromAngle + step) * radius, Math.Sin(fromAngle + step) * radius);
                if (!SegmentClears(craft.Position, candidate, centre, body.RadiusM + craft.CollisionRadiusM + 100)) continue;
                waypoint = candidate;
                break;
            }
            // Brake against the body being circled (the waypoint moves with it), not the far-off goal:
            // matching the goal's velocity here could point the ship straight into this body (#41).
            // The job sets its real target again on the next tick.
            SetTarget(craft.Id, body.Id);
            return Autopilot.Approach(craft, waypoint, GetVelocityAt(body.Id, TimeS), maxSpeed, arrivalSpeed: 40);
        }
        return Autopilot.Approach(craft, goal, goalVelocity, maxSpeed, arrivalSpeed);
    }

    /// <summary>Does the segment a→b stay at least <paramref name="minDistance"/> from <paramref name="centre"/>?</summary>
    private static bool SegmentClears(Vector2D a, Vector2D b, Vector2D centre, double minDistance)
    {
        var ab = b - a;
        var lengthSq = ab.X * ab.X + ab.Y * ab.Y;
        var t = lengthSq > 1e-12 ? Math.Clamp(((centre.X - a.X) * ab.X + (centre.Y - a.Y) * ab.Y) / lengthSq, 0, 1) : 0;
        return (a + ab * t).DistanceTo(centre) >= minDistance;
    }

    /// <summary>Moving at <paramref name="relativeVelocity"/>, does the ship come within <paramref name="radius"/> of the centre in the next <paramref name="horizonS"/>?</summary>
    private static bool WillHit(Vector2D position, Vector2D relativeVelocity, Vector2D centre, double radius, double horizonS)
    {
        var toCentre = centre - position;
        var speedSq = relativeVelocity.X * relativeVelocity.X + relativeVelocity.Y * relativeVelocity.Y;
        if (speedSq < 1e-9) return false;
        var tClosest = (toCentre.X * relativeVelocity.X + toCentre.Y * relativeVelocity.Y) / speedSq;
        if (tClosest <= 0) return false; // moving away
        var t = Math.Min(tClosest, horizonS);
        return (position + relativeVelocity * t).DistanceTo(centre) < radius;
    }
}
