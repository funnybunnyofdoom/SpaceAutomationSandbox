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
    public void RestoreJob(CraftId craftId, MiningJob job) => GetCraft(craftId).Job = job;

    private void StepJobs()
    {
        foreach (var craft in _crafts)
            if (craft.Job is { Active: true } job) StepJob(craft, job, allowResume: true);
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
                    else Wait(craft, job, MiningJobState.Landing, anchor.Message);
                    return;
                }
                SetTarget(craft.Id, source.Id);
                craft.Controls = Autopilot.Approach(craft, SurfacePoint(source, craft, -3), GetVelocityAt(source.Id, TimeS),
                    JobLandingSpeedMps, arrivalSpeed: 1.5);
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
                if (craft.IsDocked) TryUndock(craft.Id);
                SetTargetCraft(craft.Id, destination.Id);
                var ports = FreePorts(destination);
                if (ports.Count == 0) { Wait(craft, job, MiningJobState.ToDestination, $"no free docking port at {destination.Name}"); return; }
                var berth = BerthPosition(destination, ports[0], craft.CollisionRadiusM, destination.Position);
                var stationVelocity = CraftVelocityAt(destination, TimeS);
                craft.Controls = NavigateTo(craft, berth, stationVelocity, BodyId.None, JobCruiseSpeedMps, 0);
                if (berth.DistanceTo(craft.Position) < 30 && (craft.Velocity - stationVelocity).Length < DockMaxRelativeSpeedMps * 0.8)
                {
                    var dock = TryDock(craft.Id);
                    if (dock.Done)
                    {
                        job.State = MiningJobState.Unloading;
                        craft.Controls = ShipControls.Idle; // this tick's throttle would undock it again
                    }
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
                else if (amount == 0) Wait(craft, job, MiningJobState.Unloading, $"{destination.Name} is full");
                return;
        }
    }

    private static void Wait(CraftState craft, MiningJob job, MiningJobState resume, string reason)
    {
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

    /// <summary>
    /// Approach a goal, detouring around the first body (other than <paramref name="ignore"/>) whose
    /// surface the straight path would pass too close to: circle the body high above its surface in
    /// steps of at most 45° towards the goal's side, until the straight line is clear (re-checked
    /// every tick). Goals hugging a body (a low station) are reached from directly above.
    /// </summary>
    private ShipControls NavigateTo(CraftState craft, Vector2D goal, Vector2D goalVelocity, BodyId ignore, double maxSpeed, double arrivalSpeed)
    {
        var path = goal - craft.Position;
        var length = path.Length;
        if (length > 1e-6)
        {
            var dir = path * (1 / length);
            foreach (var body in _bodies.Where(b => b.Id != ignore))
            {
                var centre = GetPosition(body.Id);
                var along = Math.Clamp((centre.X - craft.Position.X) * dir.X + (centre.Y - craft.Position.Y) * dir.Y, 0, length);
                var closest = craft.Position + dir * along;
                if (closest.DistanceTo(centre) >= body.RadiusM + craft.CollisionRadiusM + JobAvoidClearanceM) continue;

                var fromAngle = Math.Atan2(craft.Position.Y - centre.Y, craft.Position.X - centre.X);
                var toAngle = Math.Atan2(goal.Y - centre.Y, goal.X - centre.X);
                var turn = FlightModel.WrapAngle(toAngle - fromAngle);
                var step = Math.Sign(turn) * Math.Min(Math.Abs(turn), Math.PI / 4);
                var radius = body.RadiusM + JobDetourAltitudeM;
                var waypoint = centre + new Vector2D(Math.Cos(fromAngle + step) * radius, Math.Sin(fromAngle + step) * radius);
                return Autopilot.Approach(craft, waypoint, GetVelocityAt(body.Id, TimeS), maxSpeed, arrivalSpeed: 40);
            }
        }
        return Autopilot.Approach(craft, goal, goalVelocity, maxSpeed, arrivalSpeed);
    }
}
