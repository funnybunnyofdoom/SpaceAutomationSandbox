using System;

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
                    case DockApproach.NoFreePort: WaitHaul(craft, job, HaulJobState.ToPickup, $"no free docking port at {pickup.Name}"); break;
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
                    case DockApproach.NoFreePort: WaitHaul(craft, job, HaulJobState.ToDropoff, $"no free docking port at {destination.Name}"); break;
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

    /// <summary>
    /// One tick of getting docked at a station: lift off if landed, undock from anything else, fly to
    /// its first free berth and dock when close and matched. Shared by mining and freight jobs.
    /// </summary>
    private DockApproach FlyToDock(CraftState craft, CraftState station)
    {
        if (craft.IsDocked && craft.DockedToCraftId == station.Id) return DockApproach.Docked;
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
        var ports = FreePorts(station);
        if (ports.Count == 0) return DockApproach.NoFreePort;

        var berth = BerthPosition(station, ports[0], craft.CollisionRadiusM, station.Position);
        var stationVelocity = CraftVelocityAt(station, TimeS);
        // A landed station's berth sits just above its body: don't treat that body as an obstacle to detour around.
        craft.Controls = NavigateTo(craft, berth, stationVelocity, station.IsLanded ? station.LandedOnBodyId : BodyId.None, JobCruiseSpeedMps, 0);
        if (berth.DistanceTo(craft.Position) < 30 && (craft.Velocity - stationVelocity).Length < DockMaxRelativeSpeedMps * 0.8
            && TryDock(craft.Id).Done)
        {
            craft.Controls = ShipControls.Idle; // this tick's throttle would undock it again
            return DockApproach.Docked;
        }
        return DockApproach.Underway;
    }
}
