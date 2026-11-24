using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Salvage and field repairs (#86).
/// <list type="bullet">
/// <item><b>Salvage:</b> a craft with a salvage arm, close to a wreck (see #84) and matched to it, first loads what the
/// wreck holds, then cuts parts off it, heaviest first. A cut part becomes exactly its materials, in the wreck's stock, to be
/// loaded next; the last part goes straight into the hold and the wreck is gone. By hand it works on the
/// targeted wreck; a <see cref="SalvageJob"/> does it on its own.</item>
/// <item><b>Field repairs:</b> repair drones, and working robots (#13) more slowly, mend damaged parts on their own craft and
/// on craft docked to it, at the same cost as a shipyard (#3), from their own craft's stock. Missing parts still need a shipyard.</item>
/// </list>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>How far a salvage arm reaches, hull to hull, and the most speed it can work at.</summary>
    public const double SalvageRangeM = 150, SalvageMaxSpeedMps = 10;

    /// <summary>Repair work one working robot does, kg per second.</summary>
    public const double RobotRepairKgPerS = 0.2;

    // --- Salvage ---

    /// <summary>The wreck a craft is salvaging by its job or its target, if it has one in mind.</summary>
    private CraftState? SalvageTarget(CraftState craft)
    {
        var id = craft.Job is SalvageJob { Active: true } job ? job.State == SalvageJobState.Cutting ? job.WreckId : CraftId.None : craft.TargetCraftId;
        return !id.IsNone && _craftsById.TryGetValue(id, out var wreck) && wreck.IsWreck ? wreck : null;
    }

    private bool InSalvageRange(CraftState craft, CraftState wreck) =>
        craft.Position.DistanceTo(wreck.Position) - craft.CollisionRadiusM - wreck.CollisionRadiusM <= SalvageRangeM
        && (craft.Velocity - wreck.Velocity).Length <= SalvageMaxSpeedMps;

    /// <summary>The order a wreck is cut up in: heaviest parts first, so the lightest is left for last.</summary>
    private DesignCell NextCut(CraftState wreck) =>
        wreck.Design.Cells.OrderByDescending(c => Parts!.Get(c.PartId).MassKg).ThenBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).First();

    /// <summary>Mass of the lightest part: the last one, which must fit a hold whole.</summary>
    private double LastPartKg(CraftState wreck) => wreck.Design.Cells.Min(c => Parts!.Get(c.PartId).MassKg);

    private void StepSalvage()
    {
        if (Parts is null) return;
        List<CraftState>? gone = null;
        foreach (var craft in _crafts)
        {
            craft.SalvageStatus = null;
            if (craft.Stats.SalvageRateKgPerS <= 0 || craft.IsWreck || !IsPlayers(craft) || craft.IsDocked || craft.Transit is not null) continue;
            if (SalvageTarget(craft) is not { } wreck || gone?.Contains(wreck) == true) { craft.SalvageProgressKg = 0; continue; }
            if (!InSalvageRange(craft, wreck)) { craft.SalvageProgressKg = 0; craft.SalvageStatus = $"salvage: come within {SalvageRangeM:0} m and match speed"; continue; }
            if (!Operational(craft)) { craft.SalvageStatus = $"salvage stopped: {craft.CrewShortReason}"; continue; }
            Strip(wreck);
            var lastOnly = wreck.Design.Cells.Count == 1 && wreck.Inventory.TotalKg == 0;
            if (craft.FreeCargoKg <= 0 || (lastOnly && craft.FreeCargoKg < LastPartKg(wreck))) { craft.SalvageStatus = "salvage: the hold is full"; continue; }
            if (!TryUsePower(craft, craft.Stats.SalvagePowerKW * FixedStepClock.TickSeconds)) { craft.SalvageStatus = "salvage: no power"; continue; }
            craft.SalvageProgressKg += craft.Stats.SalvageRateKgPerS * FixedStepClock.TickSeconds;

            if (wreck.Inventory.TotalKg > 0)
            {
                var budget = Math.Min((long)Math.Floor(craft.SalvageProgressKg), craft.FreeCargoKg);
                foreach (var (material, kg) in wreck.Inventory.Kg.ToList())
                {
                    var take = Math.Min(kg, budget);
                    if (take <= 0) break;
                    wreck.Inventory.TryRemove(new Dictionary<string, long> { [material] = take });
                    craft.Inventory.Add(material, take);
                    budget -= take;
                    craft.SalvageProgressKg -= take;
                }
                craft.SalvageStatus = $"loading from {wreck.Name} ({wreck.Inventory.TotalKg / 1000.0:0.0} t left loose)";
                continue;
            }
            var cell = NextCut(wreck);
            var part = Parts.Get(cell.PartId);
            craft.SalvageStatus = $"cutting {part.Name} off {wreck.Name} ({Math.Min(1, craft.SalvageProgressKg / part.MassKg) * 100:0}%), {wreck.Design.Cells.Count} parts left";
            if (craft.SalvageProgressKg < part.MassKg) continue;
            craft.SalvageProgressKg = 0;
            if (wreck.Design.Cells.Count == 1)
            {
                craft.Inventory.Add(part.Bill.ToWholeKg());
                AddEvent($"{craft.Name} finished salvaging {wreck.Name}.");
                (gone ??= new()).Add(wreck);
            }
            else BreakOff(wreck, new List<DesignCell> { cell }, wreck.Inventory);
        }
        if (gone is not null) foreach (var wreck in gone) RemoveCraft(wreck); // after the loop: the list mustn't change while iterated
    }

    /// <summary>Turns what a wreck holds in other forms (kits, unfinished orders, a refining batch) into loose materials.</summary>
    private static void Strip(CraftState wreck)
    {
        foreach (var kit in wreck.KitsList) wreck.Inventory.Add(kit.MaterialsKg);
        wreck.KitsList.Clear();
        foreach (var order in wreck.BuildQueueList) wreck.Inventory.Add(order.MaterialsKg);
        wreck.BuildQueueList.Clear();
        if (wreck.RefiningBatch is not { } batch) return;
        wreck.Inventory.Add(batch.InputsKg);
        wreck.RefiningBatch = null;
    }

    // --- The job ---

    public ActionResult StartJob(CraftId craftId, SalvageJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        if (craft.Stats.SalvageRateKgPerS <= 0) return new(false, $"{craft.Name} has no salvage arm.");
        if (craft.Stats.CargoCapacityKg <= 0) return new(false, $"{craft.Name} has no cargo bay.");
        if (!_craftsById.TryGetValue(job.DestinationId, out var destination) || destination.Id == craft.Id) return new(false, "The destination must be another craft.");
        if (destination.Stats.DockingPorts.Count == 0 || destination.IsWreck || !IsPlayers(destination)) return new(false, $"{destination.Name} has no working docking port.");
        job.Active = true;
        job.WaitReason = null;
        job.WreckId = CraftId.None;
        job.State = Salvaged(craft).Any() ? SalvageJobState.ToDestination : SalvageJobState.ToWreck;
        craft.Job = job;
        return new(true, $"{craft.Name}: salvaging wrecks for {destination.Name}.");
    }

    /// <summary>What a salvager delivers: everything but its own fuel, food and water (and shells, if it's armed).</summary>
    private static IEnumerable<string> Salvaged(CraftState craft) => craft.Inventory.Kg.Keys.Where(m =>
        m is not (FuelMaterial or FoodMaterial or WaterMaterial) && !(m is ShellMaterial or MissileMaterial && craft.Stats.WeaponList.Count > 0));

    /// <summary>The nearest wreck it can finish, in this system first, then in others it can reach; not those on the ground.</summary>
    private CraftState? PickWreck(CraftState craft)
    {
        var here = StarAt(craft.Position)?.Id;
        CraftState? best = null;
        var bestKey = (Elsewhere: true, Distance: double.MaxValue);
        foreach (var wreck in _crafts)
        {
            if (!wreck.IsWreck || wreck.IsLanded || wreck.Id == craft.Id || LastPartKg(wreck) > craft.CargoCapacityKg) continue;
            var elsewhere = StarAt(wreck.Position)?.Id != here;
            if (elsewhere && GateHop(craft, wreck.Position, out _) is null) continue;
            var key = (elsewhere, wreck.Position.DistanceTo(craft.Position));
            if (best is null || key.CompareTo(bestKey) < 0) { best = wreck; bestKey = key; }
        }
        return best;
    }

    private void WaitSalvage(CraftState craft, SalvageJob job, string reason)
    {
        job.State = SalvageJobState.Waiting;
        job.WaitReason = reason;
        job.RetryAtTick = Tick + (long)(JobStationRetryS * FixedStepClock.TicksPerSecond);
        craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded);
    }

    private void StepSalvageJob(CraftState craft, SalvageJob job)
    {
        if (job.State == SalvageJobState.Waiting)
        {
            if (Tick < job.RetryAtTick) { craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded); return; }
            job.State = Salvaged(craft).Any() ? SalvageJobState.ToDestination : SalvageJobState.ToWreck;
            job.WaitReason = null;
        }
        if (!_craftsById.TryGetValue(job.DestinationId, out var destination) || destination.IsWreck) { WaitSalvage(craft, job, StationGone("destination")); return; }
        switch (job.State)
        {
            case SalvageJobState.ToWreck:
            case SalvageJobState.Cutting:
                var wreck = job.WreckId.IsNone ? null : _craftsById.GetValueOrDefault(job.WreckId);
                if (wreck is null || !wreck.IsWreck || wreck.IsLanded)
                {
                    job.State = SalvageJobState.ToWreck;
                    wreck = PickWreck(craft);
                    job.WreckId = wreck?.Id ?? CraftId.None;
                    if (wreck is null)
                    {
                        if (Salvaged(craft).Any()) job.State = SalvageJobState.ToDestination;
                        else WaitSalvage(craft, job, "no wrecks it can reach and carry");
                        return;
                    }
                }
                var lastOnly = wreck.Design.Cells.Count == 1 && wreck.CargoKg == 0;
                if (craft.FreeCargoKg <= 0 || (lastOnly && craft.FreeCargoKg < LastPartKg(wreck)))
                {
                    if (Salvaged(craft).Any()) job.State = SalvageJobState.ToDestination;
                    else WaitSalvage(craft, job, "the hold is full of its own supplies");
                    return;
                }
                if (craft.IsDocked) { TryUndock(craft.Id); craft.Controls = ShipControls.Idle; return; }
                if (craft.IsLanded)
                {
                    if (craft.IsAnchored) TryRelease(craft.Id);
                    var outward = craft.Position - GetPosition(craft.LandedOnBodyId);
                    craft.Controls = Autopilot.ThrustAlong(craft, outward * (1 / Math.Max(outward.Length, 1e-9)));
                    return;
                }
                SetTargetCraft(craft.Id, wreck.Id);
                var away = craft.Position - wreck.Position;
                var ring = wreck.CollisionRadiusM + craft.CollisionRadiusM + SalvageRangeM / 2;
                var spot = wreck.Position + (away.Length > 1 ? away * (ring / away.Length) : new Vector2D(ring, 0));
                if (InSalvageRange(craft, wreck))
                {
                    job.State = SalvageJobState.Cutting;
                    if (spot.DistanceTo(craft.Position) < 40) { craft.Controls = new ShipControls(0, 0, true); return; } // hold beside it
                }
                else job.State = SalvageJobState.ToWreck;
                craft.Controls = NavigateTo(craft, spot, CraftVelocityAt(wreck, TimeS), BodyId.None, DockingSpeedLimit(craft, wreck), 0, wreck.Id);
                if (!craft.Cruising) SetTargetCraft(craft.Id, wreck.Id);
                return;

            case SalvageJobState.ToDestination:
                switch (FlyToDock(craft, destination))
                {
                    case DockApproach.Docked: job.State = SalvageJobState.Unloading; break;
                    case DockApproach.NoFreePort: WaitSalvage(craft, job, $"queued for a berth at {destination.Name}"); job.RetryAtTick = Tick; break;
                }
                return;

            case SalvageJobState.Unloading:
                craft.Controls = ShipControls.Idle;
                if (!craft.IsDocked) { job.State = SalvageJobState.ToDestination; return; }
                long moved = 0;
                foreach (var material in Salvaged(craft).ToList())
                {
                    var kg = MaxTransferKg(craft.Id, destination.Id, material);
                    if (kg > 0 && TryTransfer(craft.Id, destination.Id, material, kg).Done) moved += kg;
                }
                job.DeliveredKg += moved;
                if (!Salvaged(craft).Any()) { job.CompletedCycles++; job.State = SalvageJobState.ToWreck; }
                else if (moved == 0)
                {
                    TryUndock(craft.Id); // don't hold the port while blocked
                    WaitSalvage(craft, job, $"{destination.Name} is full");
                }
                return;
        }
    }

    // --- Field repairs ---

    private void StepFieldRepairs()
    {
        if (Parts is null) return;
        foreach (var craft in _crafts)
        {
            if (craft.IsWreck || (craft.Stats.RepairRateKgPerS <= 0 && craft.RobotsWorking <= 0) || !Operational(craft)) continue;
            CraftState? patient = Mendable(craft) ? craft : null;
            if (patient is null)
                foreach (var c in _crafts) if (c.DockedToCraftId == craft.Id && Mendable(c)) { patient = c; break; }
            // A shipyard mends with its fabricator (itself and what's docked at it).
            if (patient is null || craft.IsShipyard || (patient.IsDocked && _craftsById.TryGetValue(patient.DockedToCraftId, out var at) && at.IsShipyard)) continue;
            if (NextRepair(patient) is not { Missing: false } next) continue;
            var part = Parts.Get(next.Cell.PartId);
            var cost = RepairCost(patient, next.Cell, false);
            var work = Math.Max(1, cost.Values.Sum());
            if (patient.RepairProgressKg < work)
            {
                var rate = craft.RobotsWorking * RobotRepairKgPerS;
                if (craft.Stats.RepairRateKgPerS > 0 && TryUsePower(craft, craft.Stats.RepairPowerKW * FixedStepClock.TickSeconds)) rate += craft.Stats.RepairRateKgPerS;
                if (rate <= 0) { patient.RepairStatus = "field repairs stopped: no power"; continue; }
                patient.RepairProgressKg = Math.Min(work, patient.RepairProgressKg + rate * FixedStepClock.TickSeconds);
                patient.RepairStatus = $"field repair of {part.Name} ({patient.RepairProgressKg / work * 100:0}%)";
                if (patient.RepairProgressKg < work) continue;
            }
            var shortfall = craft.Inventory.Shortfall(cost);
            if (shortfall.Count > 0) { patient.RepairStatus = $"waiting: field repair of {part.Name} needs {Describe(shortfall)} more aboard {craft.Name}"; continue; }
            craft.Inventory.TryRemove(cost);
            foreach (var (material, kg) in cost) _ejectedKg[material] = _ejectedKg.GetValueOrDefault(material) + kg; // the worn material is discarded, as at a shipyard
            patient.RepairProgressKg = 0;
            patient.HealthList.Remove(next.Cell.Pos);
            patient.Stats = patient.Design.ComputeStats(Parts, patient.HealthList);
            patient.RepairStatus = $"field repair of {part.Name}: done";
        }
    }

    private static bool Mendable(CraftState craft) => craft.HealthList.Count > 0 && !craft.IsWreck && IsPlayers(craft);
}
