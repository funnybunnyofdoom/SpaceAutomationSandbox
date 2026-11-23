using System;
using System.Collections.Generic;
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
        if (!source.AnySiteSurveyed)
            return new(false, $"{source.Name} hasn't been surveyed: fly a ship you pilot within {SurveyRangeM / 1000:0} km of it first.");
        if (LawForbidsMining(source) is { } forbidden) return new(false, $"Not allowed: {forbidden}.");
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

    /// <summary>Craft that can't move (parked or landed), gathered once per tick for the autopilot's station avoidance.</summary>
    private readonly List<CraftState> _stationsThisTick = new();

    private void StepJobs()
    {
        _stationsThisTick.Clear();
        foreach (var craft in _crafts)
        {
            if (!Movable(craft)) _stationsThisTick.Add(craft);
            if (!craft.IsAutomated && !craft.DockClaimStation.IsNone) { craft.DockClaimStation = CraftId.None; craft.DockClaimPort = 0; }
            craft.GivingWayTo = CraftId.None;
        }
        // Far from the player: abstract legs move on, waiting craft go dormant (#17).
        BuildGateLinks(); // warp routes (#23)
        foreach (var craft in _crafts) if (craft.Transit is not null) StepTransit(craft);
        MarkDormant();
        BuildTrafficGrid(); // traffic rules (#55)
        foreach (var craft in _crafts)
        {
            if (craft.Transit is not null || TryStartTransit(craft)) { craft.Job?.NoteTick(Tick); continue; }
            if (craft.Job is { Active: true } held && StepYardOrder(craft, held)) { held.NoteTick(Tick); continue; } // refits pause the job (#56)
            if (craft.Job is { Active: true } active && StepService(craft, active)) { active.NoteTick(Tick); continue; } // repairs first (#3)
            if (craft.Job is { Active: true } routed && StepGateRoute(craft, routed)) { routed.NoteTick(Tick); continue; } // via warp gates (#23)
            switch (craft.Job)
            {
                case SurveyJob { Active: true } survey: StepSurveyJob(craft, survey); break;
                case MiningJob { Active: true } mining: StepJob(craft, mining, allowResume: true); break;
                case HaulJob { Active: true } haul: StepHaul(craft, haul, allowResume: true); break;
                case ServeRequestsJob { Active: true } serve: StepServe(craft, serve, allowResume: true); break;
            }
            TopUpSupplies(craft); // crewed ships take food and water when docked (#14)
            craft.Job?.NoteTick(Tick);
        }
    }

    private void StepJob(CraftState craft, MiningJob job, bool allowResume)
    {
        // Keep the step to go back to (#24): waiting again mustn't overwrite it with "waiting".
        var resume = job.State == MiningJobState.Waiting ? job.ResumeState : job.State;
        if (!_byId.TryGetValue(job.SourceBodyId, out var source)) { Wait(craft, job, resume, "the source body is missing"); return; }
        if (!_craftsById.TryGetValue(job.DestinationId, out var destination)) { Wait(craft, job, resume, StationGone("destination")); return; }
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
                if (MineableSurveyed(source, material) == 0) // only surveyed sites count (#70), and only what the laws allow (#36)
                {
                    if (held > 0) { if (craft.IsDocked) TryUndock(craft.Id); job.State = MiningJobState.ToDestination; return; }
                    if (craft.IsLanded && craft.LandedOnBodyId == source.Id) // anchored on the source: wait there
                    {
                        Wait(craft, job, MiningJobState.ToSource, NothingToMine(source, material));
                        return;
                    }
                    // Docked or in space: land on the source and wait there, anchored. Not docked (it would hold a
                    // berth others need) and not in space (it would hang in a planet's gravity).
                }
                if (craft.IsDocked) TryUndock(craft.Id);
                if (craft.IsLanded)
                {
                    if (craft.LandedOnBodyId == source.Id) { job.State = MiningJobState.Landing; return; }
                    Lift(craft, job, MiningJobState.ToSource);
                    return;
                }
                SetTarget(craft.Id, source.Id);
                var hover = SurfacePoint(source, craft, JobHoverAltitudeM, job);
                // The spot can be round the far side now that ore sits at sites (#70): until the craft is nearly
                // above it, aim high over the spot and let the source count as an obstacle, so the path goes round.
                var sourceCentre = GetPosition(source.Id);
                var craftBearing = Math.Atan2(craft.Position.Y - sourceCentre.Y, craft.Position.X - sourceCentre.X);
                if (Math.Abs(FlightModel.WrapAngle(job.SpotBearingRad!.Value - craftBearing)) > SpotDetourAngleRad)
                {
                    var spotDirection = new Vector2D(Math.Cos(job.SpotBearingRad.Value), Math.Sin(job.SpotBearingRad.Value));
                    var high = sourceCentre + spotDirection * (source.RadiusM + craft.CollisionRadiusM + JobLiftAltitudeM);
                    craft.Controls = NavigateTo(craft, high, GetVelocityAt(source.Id, TimeS), BodyId.None, JobCruiseSpeedMps, 20);
                    return;
                }
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
                craft.Controls = GiveWay(craft, CraftId.None, source.Id) ?? LandOnAnchor(craft, source); // let lifting ships out first (#55)
                return;

            case MiningJobState.Mining:
                craft.Controls = ShipControls.Idle;
                if (!craft.IsAnchored) { job.State = MiningJobState.Landing; return; }
                var full = craft.FreeCargoKg == 0 || craft.Inventory.TotalKg >= job.ReturnFraction * craft.CargoCapacityKg;
                if (full || MineableAt(source, material, LandedBearing(craft)) == 0)
                {
                    if (held > 0) { Lift(craft, job, MiningJobState.ToDestination); return; }
                    if (!full && MineableSurveyed(source, material) > 0) // this site is dug out: on to another (#70)
                    {
                        job.SpotBearingRad = null;
                        Lift(craft, job, MiningJobState.ToSource);
                        return;
                    }
                    Wait(craft, job, MiningJobState.Mining,
                        full ? "the hold is full of other cargo" : NothingToMine(source, material));
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
                    case DockApproach.NoFreePort: Wait(craft, job, MiningJobState.ToDestination, $"queued for a berth at {destination.Name}"); break;
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
                    job.SpotBearingRad = null; // a new spot next trip
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
    /// <summary>Spacing between miners' spots on the same rock, along its surface (#55).</summary>
    public const double MiningSpotSpacingM = 40;

    /// <summary>
    /// The point above this trip's landing spot. The spot is picked once per trip (saved with the job):
    /// on the side the craft is coming from, then nudged by up to three spacings either way by craft
    /// id, so miners working the same rock land apart instead of on top of each other (#55); then
    /// moved into the nearest surveyed site that holds the material (#70).
    /// </summary>
    private Vector2D SurfacePoint(CelestialBodyState body, CraftState craft, double altitudeM, MiningJob job)
    {
        var centre = GetPosition(body.Id);
        if (job.SpotBearingRad is null)
        {
            var outward = craft.Position - centre;
            var bearing = Math.Atan2(outward.Y, outward.X);
            var nudge = (int)(craft.Id.Value % 7) - 3;
            var spacing = MiningSpotSpacingM / Math.Max(body.RadiusM, 1);
            // Into the site first (keeping room for the nudge), then apart by craft id: miners arriving
            // from outside the site would otherwise all get the same edge spot.
            if (MiningSite(body, job.MaterialId, bearing) is { } site) bearing = site.Clamp(bearing, 4 * spacing);
            job.SpotBearingRad = FlightModel.WrapAngle(bearing + nudge * spacing);
        }
        var angle = job.SpotBearingRad.Value;
        return centre + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (body.RadiusM + craft.CollisionRadiusM + altitudeM);
    }

    /// <summary>Further round the body than this from its landing spot, a miner first flies high over the spot (#70).</summary>
    public const double SpotDetourAngleRad = 0.4;

    /// <summary>Why a mining job has nothing to mine: none left anywhere, or none in the surveyed sites (#70).</summary>
    private string NothingToMine(CelestialBodyState source, string material) =>
        LawForbidsMining(source) ?? (material == source.BulkMaterialId && !BulkMiningAllowed && source.SurveyedDeposit(material, false) == 0
            ? $"law: no bulk {material} mining (No planet dismantling)"
            : source.Deposit(material) > 0 ? $"no surveyed site on {source.Name} has {material}: fly over more of it" : $"{source.Name} has no {material} left");

    /// <summary>
    /// The surveyed site nearest a bearing where the material can be mined (#70): one holding its ore,
    /// or for the body's bulk (rock or ice) any surveyed site. Null if there's none.
    /// </summary>
    public static SurfaceSite? MiningSite(CelestialBodyState body, string material, double nearBearingRad)
    {
        SurfaceSite? best = null;
        var bestGap = double.MaxValue;
        foreach (var site in body.Sites)
        {
            if (!site.Surveyed) continue;
            var ok = site.DepositsKg.GetValueOrDefault(material) > 0 || (material == body.BulkMaterialId && body.BulkKg > 0);
            if (!ok) continue;
            var gap = Math.Abs(Math.IEEERemainder(nearBearingRad - site.CentreRad, 2 * Math.PI)) - site.WidthRad / 2;
            if (gap < bestGap) { best = site; bestGap = gap; }
        }
        return best;
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
    private ShipControls NavigateTo(CraftState craft, Vector2D goal, Vector2D goalVelocity, BodyId ignore, double maxSpeed, double arrivalSpeed,
        CraftId ignoreCraft = default)
    {
        if (CruiseLeg(craft, goal, goalVelocity) is { } cruise) return cruise; // long legs between planets (#53)
        // Keep right on long legs (#55), except on the way down to a body: there the shifted goal could
        // sit low over a surface the autopilot deliberately doesn't treat as an obstacle.
        if (ignore.IsNone) goal = LaneGoal(craft, goal);
        // Gravity curves the path towards the body pulling it (#32): predict with it for that body.
        var pullingIndex = GravitySourceIndex(craft.Position, TimeS);
        var pull = GravityAt(craft.Position, TimeS, pullingIndex);
        var positions = AllBodyPositions(TimeS, out var velocities);
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Id == ignore) continue;
            var centre = positions[i];
            var relative = craft.Velocity - velocities[i];
            // Look further ahead the faster we close: time to turn round (π at the turn rate) plus time to stop.
            var accel = craft.Stats.TotalThrustN / craft.MassKg;
            var horizon = JobImpactHorizonS + (accel > 0 ? relative.Length / accel : 0) + Math.PI / FlightModel.TurnRateRadPerS;
            if (WillHit(craft.Position, relative, centre, body.RadiusM + craft.CollisionRadiusM + 20, horizon,
                    i == pullingIndex ? pull : Vector2D.Zero))
            {
                var away = craft.Position - centre;
                return Autopilot.ThrustAlong(craft, away * (1 / Math.Max(away.Length, 1e-9)));
            }
        }
        // Then other craft, then routing round stations: never let a detour override a ship about to be hit (#55).
        if (GiveWay(craft, ignoreCraft, ignore) is { } giveWay) return giveWay;
        if (AvoidStations(craft, goal, ignoreCraft, maxSpeed) is { } avoid) return avoid; // stations are solid (#34)

        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Id == ignore) continue;
            var centre = positions[i];
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
    /// <summary>
    /// Stations (craft that can't move: parked or landed) are obstacles since #34. Steer round any
    /// whose disc the straight way to the goal crosses, and turn away from one about to be hit.
    /// <paramref name="ignoreCraft"/> is the station being docked at (its own approach handles it).
    /// </summary>
    private ShipControls? AvoidStations(CraftState craft, Vector2D goal, CraftId ignoreCraft, double maxSpeed)
    {
        foreach (var station in _stationsThisTick)
        {
            if (station.Id == ignoreCraft || ReferenceEquals(station, craft) || craft.DockedToCraftId == station.Id) continue;
            var clearance = station.CollisionRadiusM + craft.CollisionRadiusM + 25;
            var offset = craft.Position - station.Position;
            if (offset.Length > 3_000 + clearance) continue; // far away: nothing to do yet
            var relative = craft.Velocity - station.Velocity;
            var accel = craft.Stats.TotalThrustN / craft.MassKg;
            var horizon = 3 + (accel > 0 ? relative.Length / accel : 0);
            if (WillHit(craft.Position, relative, station.Position, clearance - 10, horizon))
                return Autopilot.ThrustAlong(craft, offset * (1 / Math.Max(offset.Length, 1e-9)));
            if (SegmentClears(craft.Position, goal, station.Position, clearance)) continue;
            if (goal.DistanceTo(station.Position) < clearance) continue; // the goal itself is beside it: the docking approach handles that
            // Pass on the side we're already on, a little wider than the station.
            var toGoal = goal - craft.Position;
            var side = new Vector2D(-toGoal.Y, toGoal.X) * (1 / Math.Max(toGoal.Length, 1e-9));
            if (offset.X * side.X + offset.Y * side.Y < 0) side = side * -1;
            var waypoint = station.Position + side * (clearance + 15);
            return Autopilot.Approach(craft, waypoint, station.Velocity, Math.Min(maxSpeed, JobCruiseSpeedMps), arrivalSpeed: 20);
        }
        return null;
    }

    private static bool WillHit(Vector2D position, Vector2D relativeVelocity, Vector2D centre, double radius, double horizonS,
        Vector2D acceleration = default)
    {
        if (acceleration.X != 0 || acceleration.Y != 0)
        {
            // Curved path under constant gravity: sample it (cheap: only for the one body pulling).
            for (var step = 1; step <= 12; step++)
            {
                var ts = horizonS * step / 12;
                if ((position + relativeVelocity * ts + acceleration * (0.5 * ts * ts)).DistanceTo(centre) < radius) return true;
            }
            return false;
        }
        var toCentre = centre - position;
        var speedSq = relativeVelocity.X * relativeVelocity.X + relativeVelocity.Y * relativeVelocity.Y;
        if (speedSq < 1e-9) return false;
        var tClosest = (toCentre.X * relativeVelocity.X + toCentre.Y * relativeVelocity.Y) / speedSq;
        if (tClosest <= 0) return false; // moving away
        var t = Math.Min(tClosest, horizonS);
        return (position + relativeVelocity * t).DistanceTo(centre) < radius;
    }
}
