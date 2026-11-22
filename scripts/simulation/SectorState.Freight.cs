using System;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Freight jobs: shuttle one material from a pickup station to a destination station, docking at
/// each, with the same autopilot and actions as mining. Waits (with a reason) rather than hauling
/// scraps, overfilling a "keep stocked" target, or crowding a station with no free port.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Gives a craft a freight job, refusing (with the reason) if it or the job can't work.</summary>
    public ActionResult StartJob(CraftId craftId, HaulJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        if (craft.Stats.CargoCapacityKg <= 0) return new(false, $"{craft.Name} has no cargo bay.");
        if (!_craftsById.TryGetValue(job.PickupId, out var pickup) || pickup.Id == craft.Id)
            return new(false, "The pickup must be another craft.");
        if (!_craftsById.TryGetValue(job.DestinationId, out var destination) || destination.Id == craft.Id)
            return new(false, "The destination must be another craft.");
        if (pickup.Id == destination.Id) return new(false, "Pickup and destination must be different.");
        if (pickup.Stats.DockingPorts.Count == 0) return new(false, $"{pickup.Name} has no docking port.");
        if (destination.Stats.DockingPorts.Count == 0) return new(false, $"{destination.Name} has no docking port.");

        job.Active = true;
        job.WaitReason = null;
        job.State = craft.Inventory[job.MaterialId] > 0 ? HaulJobState.ToDropoff : HaulJobState.ToPickup;
        craft.Job = job;
        return new(true, $"{craft.Name}: hauling {job.MaterialId} from {pickup.Name} to {destination.Name}.");
    }

    private void StepHaul(CraftState craft, HaulJob job, bool allowResume)
    {
        if (!_craftsById.TryGetValue(job.PickupId, out var pickup)) { WaitHaul(craft, job, job.State, "the pickup is missing"); return; }
        if (!_craftsById.TryGetValue(job.DestinationId, out var destination)) { WaitHaul(craft, job, job.State, "the destination is missing"); return; }
        var material = job.MaterialId;

        switch (job.State)
        {
            case HaulJobState.Waiting:
                if (Tick < job.RetryAtTick) { craft.Controls = new ShipControls(0, 0, !craft.IsDocked); return; }
                job.State = job.ResumeState;
                job.WaitReason = null;
                if (allowResume) StepHaul(craft, job, allowResume: false);
                else craft.Controls = new ShipControls(0, 0, true);
                return;

            case HaulJobState.ToPickup:
                switch (FlyToDock(craft, pickup))
                {
                    case DockApproach.Docked: job.State = HaulJobState.Loading; break;
                    case DockApproach.NoFreePort: WaitHaul(craft, job, HaulJobState.ToPickup, $"queued for a berth at {pickup.Name}"); break;
                }
                return;

            case HaulJobState.Loading:
                craft.Controls = ShipControls.Idle;
                if (craft.DockedToCraftId != pickup.Id) { job.State = HaulJobState.ToPickup; return; }
                var available = pickup.Inventory[material];
                if (available < job.MinPickupKg && craft.Inventory[material] == 0)
                {
                    TryUndock(craft.Id); // don't hold the port (a miner may need it to deliver)
                    WaitHaul(craft, job, HaulJobState.ToPickup,
                        $"{pickup.Name} has {available / 1000.0:0.0} t of {material} (waiting for {job.MinPickupKg / 1000.0:0.0} t)", JobStationRetryS);
                    return;
                }
                var take = MaxTransferKg(pickup.Id, craft.Id, material);
                if (take > 0) TryTransfer(pickup.Id, craft.Id, material, take);
                if (craft.Inventory[material] > 0) job.State = HaulJobState.ToDropoff;
                else WaitHaul(craft, job, HaulJobState.Loading, "the hold is full of other cargo");
                return;

            case HaulJobState.ToDropoff:
                switch (FlyToDock(craft, destination))
                {
                    case DockApproach.Docked: job.State = HaulJobState.Unloading; break;
                    case DockApproach.NoFreePort: WaitHaul(craft, job, HaulJobState.ToDropoff, $"queued for a berth at {destination.Name}"); break;
                }
                return;

            case HaulJobState.Unloading:
                craft.Controls = ShipControls.Idle;
                if (craft.DockedToCraftId != destination.Id) { job.State = HaulJobState.ToDropoff; return; }
                var room = MaxTransferKg(craft.Id, destination.Id, material);
                if (job.KeepStockedKg > 0) room = Math.Min(room, Math.Max(0, job.KeepStockedKg - destination.Inventory[material]));
                if (room > 0 && TryTransfer(craft.Id, destination.Id, material, room).Done) job.DeliveredKg += room;
                if (craft.Inventory[material] == 0)
                {
                    job.CompletedCycles++;
                    job.State = HaulJobState.ToPickup;
                }
                else if (room == 0)
                {
                    TryUndock(craft.Id); // don't hold the port while blocked
                    WaitHaul(craft, job, HaulJobState.ToDropoff, job.KeepStockedKg > 0 && destination.Inventory[material] >= job.KeepStockedKg
                        ? $"{destination.Name} already has {job.KeepStockedKg / 1000.0:0.0} t of {material}"
                        : $"{destination.Name} is full", JobStationRetryS);
                }
                return;
        }
    }

    private void WaitHaul(CraftState craft, HaulJob job, HaulJobState resume, string reason, double retryAfterS = 0)
    {
        job.RetryAtTick = Tick + (long)(retryAfterS * FixedStepClock.TicksPerSecond);
        job.ResumeState = resume;
        job.WaitReason = reason;
        job.State = HaulJobState.Waiting;
        craft.Controls = new ShipControls(0, 0, true);
    }

    private enum DockApproach { Underway, Docked, NoFreePort }

    /// <summary>Within this distance of a station's hull, approaching ships slow to <see cref="StationApproachSpeedMps"/> (#34).</summary>
    public const double StationSlowZoneM = 150;

    /// <summary>Closing speed allowed close to a station: below the damage threshold, so a bump is harmless.</summary>
    public const double StationApproachSpeedMps = 5;

    /// <summary>
    /// Where to head now on the way into a berth (#34): ships come in along the port's own direction,
    /// never through the station. From behind or the side, first to a point off to the side of the
    /// station, then to a point straight out from the port, then into the berth.
    /// </summary>
    private Vector2D DockingWaypoint(CraftState craft, CraftState station, int port, Vector2D berth)
    {
        var info = station.Stats.DockingPorts[port];
        var (fx, fy) = DesignCell.Facing(info.Rotation);
        var outward = Rotate(new Vector2D(fx, fy), station.HeadingRad);
        var clearance = station.CollisionRadiusM + craft.CollisionRadiusM + 20;
        var lineUp = berth + outward * clearance; // straight out from the port, clear of the hull

        var fromBerth = craft.Position - berth;
        var ahead = fromBerth.X * outward.X + fromBerth.Y * outward.Y;
        var side = (fromBerth - outward * ahead).Length;
        if (ahead > 0 && side < Math.Max(4, 0.3 * ahead)) return berth; // in the approach lane: straight in

        // Not lined up: head for the line-up point, stepping round the station (at a safe radius) while
        // it's more than 60° away round the hull, so the way there never crosses the station.
        var away = craft.Position - station.Position;
        var current = Math.Atan2(away.Y, away.X);
        var wanted = Math.Atan2(outward.Y, outward.X);
        var difference = FlightModel.WrapAngle(wanted - current);
        if (Math.Abs(difference) <= Math.PI / 3) return lineUp;
        var radius = Math.Max(clearance, away.Length);
        var step = current + Math.Sign(difference) * Math.PI / 4;
        return station.Position + new Vector2D(Math.Cos(step), Math.Sin(step)) * radius;
    }

    /// <summary>Speed limit relative to the station: full cruise far out, ramping down to a harmless crawl near the hull.</summary>
    private static double DockingSpeedLimit(CraftState craft, CraftState station)
    {
        var gap = craft.Position.DistanceTo(station.Position) - station.CollisionRadiusM - craft.CollisionRadiusM;
        return Math.Clamp(StationApproachSpeedMps + Math.Max(0, gap - StationSlowZoneM) * 0.5, StationApproachSpeedMps, JobCruiseSpeedMps);
    }

    /// <summary>
    /// One tick of getting docked at a station: lift off if landed, undock from anything else, fly to
    /// its first free berth and dock when close and matched. Shared by mining and freight jobs.
    /// </summary>
    private DockApproach FlyToDock(CraftState craft, CraftState station)
    {
        if (craft.IsDocked && craft.DockedToCraftId == station.Id) { ReleaseClaim(craft); return DockApproach.Docked; }
        if (craft.IsLanded)
        {
            if (craft.IsAnchored) TryRelease(craft.Id);
            var (body, altitude) = NearestBody(craft.Position);
            var outward = craft.Position - GetPosition(body.Id);
            craft.Controls = Autopilot.ThrustAlong(craft, outward * (1 / Math.Max(outward.Length, 1e-9)));
            return DockApproach.Underway;
        }
        if (craft.IsDocked) TryUndock(craft.Id);
        SetTargetCraft(craft.Id, station.Id);
        var stationVelocity = CraftVelocityAt(station, TimeS);
        // A landed station's berth sits just above its body: don't treat that body as an obstacle to detour around.
        var ignore = station.IsLanded ? station.LandedOnBodyId : BodyId.None;
        // Far out, just head for the station; berths are only claimed close in, so far-off ships don't block them (#55).
        var gap = craft.Position.DistanceTo(station.Position) - station.CollisionRadiusM;
        if (gap > ClaimRangeM && craft.DockClaimStation != station.Id)
        {
            craft.Controls = NavigateTo(craft, HoldPoint(craft, station), stationVelocity, ignore, DockingSpeedLimit(craft, station), 0, station.Id);
            return DockApproach.Underway;
        }
        var port = ClaimPort(craft, station, FreePorts(station));
        if (port < 0)
        {
            // Every berth is taken or claimed: wait on the holding ring, out of the approach lanes (#55).
            var hold = HoldPoint(craft, station);
            if (Math.Abs(craft.Position.DistanceTo(station.Position) - hold.DistanceTo(station.Position)) > 50)
            {
                // Not on the ring yet (too close, or still on the way): fly to it first.
                craft.Controls = NavigateTo(craft, hold, stationVelocity, ignore, DockingSpeedLimit(craft, station), 0, station.Id);
                return DockApproach.Underway;
            }
            return DockApproach.NoFreePort;
        }

        var berth = BerthPosition(station, port, craft.CollisionRadiusM, station.Position);
        craft.Controls = NavigateTo(craft, DockingWaypoint(craft, station, port, berth), stationVelocity, ignore,
            DockingSpeedLimit(craft, station), 0, station.Id);
        if (berth.DistanceTo(craft.Position) < 30 && (craft.Velocity - stationVelocity).Length < DockMaxRelativeSpeedMps * 0.8
            && TryDockAt(craft, station, port))
        {
            ReleaseClaim(craft);
            craft.Controls = ShipControls.Idle; // this tick's throttle would undock it again
            return DockApproach.Docked;
        }
        return DockApproach.Underway;
    }
}
