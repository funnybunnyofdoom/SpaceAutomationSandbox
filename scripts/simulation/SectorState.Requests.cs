using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Station requests and the freighters that serve them (#1).
/// <para>A station posts "keep at least X kg of M here" (<see cref="SetRequest"/>). A station's spare
/// stock of M is what it holds above its own request. A "serve requests" freighter with no trip
/// claims one: the first open request (stations in id order, materials A–Z) that some other station
/// can supply, from the nearest such station. A claim reserves:</para>
/// <list type="bullet">
/// <item>cargo: the amount counts as on its way to the destination, and until loaded as gone from the pickup;</item>
/// <item>berths: a station takes at most as many freighter visits at once as it has docking ports.</item>
/// </list>
/// Reservations are worked out from the freighters' own contracts every time, so they can't drift
/// out of step and are saved with the jobs. They bind only serve-requests freighters; players,
/// miners and fixed routes still move stock directly (the claimant then carries what it finds).
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Crewed stations keep this many hours of food and water requested automatically (#14).</summary>
    public const double AutoSupplyHours = 24;

    /// <summary>
    /// The automatic supply request (#14) of a crewed station: food and water for its crew for
    /// <see cref="AutoSupplyHours"/>, plus water for its farms. 0 for ships, uncrewed craft, craft
    /// without docking ports and craft from saves without crew rules.
    /// </summary>
    public static long AutoSupplyKg(CraftState station, string materialId)
    {
        if (station.Stats.DockingPorts.Count == 0 || Movable(station)) return 0;
        var fuel = AutoFuelKg(station, materialId); // generators' fuel (#78), crewed or not
        if (station.LegacyNoCrew || station.Crew <= 0) return fuel;
        var seconds = AutoSupplyHours * 3600;
        return fuel + materialId switch
        {
            FoodMaterial => (long)Math.Ceiling(station.Crew * FoodKgPerCrewPerS * seconds),
            WaterMaterial => (long)Math.Ceiling(station.Crew * WaterKgPerCrewPerS * seconds + station.Stats.FoodRateKgPerS * seconds),
            _ => 0,
        };
    }

    /// <summary>Stations with generators keep this many hours of full-output fuel requested (#78, the creator's choice).</summary>
    public const double AutoFuelHours = 12;

    /// <summary>Fuel a station's generators burn in <see cref="AutoFuelHours"/> at full output, for one material (whole kg).</summary>
    public static long AutoFuelKg(CraftState station, string materialId)
    {
        double kg = 0;
        foreach (var gen in station.Stats.GeneratorList)
            if (gen.BurnsKg.TryGetValue(materialId, out var perBatch))
                kg += gen.KW * AutoFuelHours * 3600 / gen.KJPerBatch * perBatch;
        return (long)Math.Ceiling(kg);
    }

    /// <summary>What a station wants kept in stock: its own request or its automatic supply request, whichever is more.</summary>
    public static long WantedKg(CraftState station, string materialId) =>
        Math.Max(station.Requests.GetValueOrDefault(materialId), AutoSupplyKg(station, materialId));

    /// <summary>Materials a station wants (its requests and automatic supplies), A–Z.</summary>
    private static IEnumerable<string> WantedMaterials(CraftState station)
    {
        var wanted = station.Requests.Keys.ToList();
        if (AutoSupplyKg(station, FoodMaterial) > 0) wanted.AddRange(new[] { FoodMaterial, WaterMaterial });
        foreach (var gen in station.Stats.GeneratorList) // their fuel (#78)
            foreach (var material in gen.BurnsKg.Keys)
                if (AutoSupplyKg(station, material) > 0) wanted.Add(material);
        return wanted.Distinct().OrderBy(m => m, StringComparer.Ordinal);
    }

    /// <summary>A serve-requests freighter with nothing to do looks again after this long.</summary>
    public const double RequestRetryS = 10;

    /// <summary>Sets (kg &gt; 0) or clears (0) a station's request for a material.</summary>
    public ActionResult SetRequest(CraftId stationId, string materialId, long kg)
    {
        var station = GetCraft(stationId);
        if (kg < 0) return new(false, "A request can't be negative.");
        if (Parts is { } parts && !parts.Materials.Contains(materialId)) return new(false, $"Unknown material '{materialId}'.");
        if (kg == 0)
            return station.RequestsList.Remove(materialId)
                ? new(true, $"{station.Name}: request for {materialId} cleared.")
                : new(false, $"{station.Name} has no request for {materialId}.");
        if (station.Stats.DockingPorts.Count == 0) return new(false, $"{station.Name} has no docking port, so freighters can't deliver there.");
        if (station.HasCargoLimit && kg > station.CargoCapacityKg)
            return new(false, $"{station.Name} only holds {station.CargoCapacityKg / 1000.0:0} t.");
        station.RequestsList[materialId] = kg;
        return new(true, $"{station.Name}: keep at least {kg / 1000.0:0.0} t of {materialId}.");
    }

    /// <summary>
    /// A crewed automated ship docked at a station tops up food and water to <see cref="AutoSupplyHours"/>
    /// for its crew from the station's spare stock (what it holds beyond what it wants for itself), #14.
    /// </summary>
    private void TopUpSupplies(CraftState ship)
    {
        if (!ship.IsAutomated || !ship.IsDocked || ship.Crew <= 0 || ship.LegacyNoCrew) return;
        if (!_craftsById.TryGetValue(ship.DockedToCraftId, out var station)) return;
        var seconds = AutoSupplyHours * 3600;
        foreach (var (material, rate) in new[] { (FoodMaterial, FoodKgPerCrewPerS), (WaterMaterial, WaterKgPerCrewPerS) })
        {
            var need = (long)Math.Ceiling(ship.Crew * rate * seconds) - ship.Inventory[material];
            if (need <= 0) continue;
            var spare = station.Inventory[material] - WantedKg(station, material);
            var amount = Math.Min(Math.Min(need, spare), MaxTransferKg(station.Id, ship.Id, material));
            if (amount > 0) TryTransfer(station.Id, ship.Id, material, amount);
        }
    }

    /// <summary>Restores a saved request (no checks beyond the basics).</summary>
    public void RestoreRequest(CraftId stationId, string materialId, long kg)
    {
        if (kg <= 0) throw new ArgumentOutOfRangeException(nameof(kg));
        GetCraft(stationId).RequestsList[materialId] = kg;
    }

    /// <summary>Kg of a material reserved by serve-requests freighters as on its way to <paramref name="stationId"/>.</summary>
    public long IncomingKg(CraftId stationId, string materialId) =>
        Contracts().Where(c => c.Contract.DestinationId == stationId && c.Contract.MaterialId == materialId).Sum(c => c.Contract.AmountKg);

    /// <summary>What <paramref name="stationId"/> still needs of a request after stock and incoming trips (0 if none).</summary>
    public long OpenRequestKg(CraftId stationId, string materialId)
    {
        var station = GetCraft(stationId);
        var want = WantedKg(station, materialId);
        return want > 0 ? Math.Max(0, want - station.Inventory[materialId] - IncomingKg(stationId, materialId)) : 0;
    }

    /// <summary>Active contracts of every serve-requests freighter except <paramref name="except"/>, in craft id order.</summary>
    private IEnumerable<(CraftState Ship, ServeRequestsJob Job, HaulContract Contract)> Contracts(CraftState? except = null)
    {
        foreach (var ship in _crafts)
            if (!ReferenceEquals(ship, except) && ship.Job is ServeRequestsJob { Active: true, Contract: { } contract } job)
                yield return (ship, job, contract);
    }

    /// <summary>Gives a craft a serve-requests freight job, refusing (with the reason) if it can't work.</summary>
    public ActionResult StartJob(CraftId craftId, ServeRequestsJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        if (craft.Stats.CargoCapacityKg <= 0) return new(false, $"{craft.Name} has no cargo bay.");
        job.Active = true;
        job.WaitReason = null;
        job.Contract = null;
        job.State = HaulJobState.ToPickup;
        craft.Job = job;
        return new(true, $"{craft.Name}: serving station requests.");
    }

    /// <summary>
    /// The trip this freighter would claim now, or null with the reason. Deterministic: requests by
    /// station id then material, sources by distance then id.
    /// </summary>
    public HaulContract? FindContract(CraftState ship, long minLoadKg, out string reason)
    {
        if (ship.FreeCargoKg <= 0) { reason = "the hold is full of other cargo"; return null; }

        var outgoing = new Dictionary<(CraftId, string), long>();
        var visits = new Dictionary<CraftId, int>();
        foreach (var (_, job, c) in Contracts(ship))
        {
            if (job.BeforePickup) outgoing[(c.PickupId, c.MaterialId)] = outgoing.GetValueOrDefault((c.PickupId, c.MaterialId)) + c.AmountKg;
            var visiting = job.BeforePickup ? c.PickupId : c.DestinationId;
            visits[visiting] = visits.GetValueOrDefault(visiting) + 1;
        }

        string? unmet = null;
        var berthsTaken = false;
        foreach (var station in _crafts)
        {
            if (ReferenceEquals(station, ship)) continue;
            foreach (var material in WantedMaterials(station))
            {
                var need = OpenRequestKg(station.Id, material);
                if (need <= 0) continue;
                unmet ??= $"no station has spare {material} for {station.Name}";

                HaulContract? best = null;
                var bestDistance = double.PositiveInfinity;
                foreach (var source in _crafts)
                {
                    if (ReferenceEquals(source, ship) || ReferenceEquals(source, station) || source.Stats.DockingPorts.Count == 0) continue;
                    var spare = source.Inventory[material] - WantedKg(source, material)
                                - outgoing.GetValueOrDefault((source.Id, material));
                    var amount = Math.Min(Math.Min(need, spare), ship.FreeCargoKg);
                    if (amount <= 0 || amount < Math.Min(minLoadKg, need)) continue;
                    if (visits.GetValueOrDefault(source.Id) >= source.Stats.DockingPorts.Count) { berthsTaken = true; continue; }
                    var distance = source.Position.DistanceTo(ship.Position);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = new HaulContract(source.Id, station.Id, material, amount);
                }
                if (best is not null) { reason = ""; return best; }
            }
        }

        reason = unmet is null ? "no open requests"
            : berthsTaken ? "every supplier's docking ports are reserved by other freighters"
            : unmet;
        return null;
    }

    private void StepServe(CraftState craft, ServeRequestsJob job, bool allowResume)
    {
        if (job.State == HaulJobState.Waiting)
        {
            if (Tick < job.RetryAtTick) { craft.Controls = new ShipControls(0, 0, !craft.IsDocked); return; }
            job.State = job.ResumeState;
            job.WaitReason = null;
            if (!allowResume) { craft.Controls = new ShipControls(0, 0, true); return; }
        }

        if (job.Contract is null)
        {
            var found = FindContract(craft, job.MinLoadKg, out var reason);
            if (found is null)
            {
                if (craft.IsDocked) TryUndock(craft.Id); // don't hold a port while idle
                WaitServe(craft, job, HaulJobState.ToPickup, reason, RequestRetryS);
                return;
            }
            job.Contract = found;
            job.State = craft.DockedToCraftId == found.PickupId ? HaulJobState.Loading : HaulJobState.ToPickup;
        }

        var contract = job.Contract;
        if (!_craftsById.TryGetValue(contract.PickupId, out var pickup) || !_craftsById.TryGetValue(contract.DestinationId, out var destination))
        {
            job.Contract = null; // a station is gone: drop the trip; what's in the hold is delivered by the next one or by hand
            WaitServe(craft, job, HaulJobState.ToPickup, "a station in the trip is missing");
            return;
        }
        var material = contract.MaterialId;

        switch (job.State)
        {
            case HaulJobState.ToPickup:
                switch (FlyToDock(craft, pickup))
                {
                    case DockApproach.Docked: job.State = HaulJobState.Loading; break;
                    case DockApproach.NoFreePort: WaitServe(craft, job, HaulJobState.ToPickup, $"queued for a berth at {pickup.Name}"); break;
                }
                return;

            case HaulJobState.Loading:
                craft.Controls = ShipControls.Idle;
                if (craft.DockedToCraftId != pickup.Id) { job.State = HaulJobState.ToPickup; return; }
                var spare = Math.Max(0, pickup.Inventory[material] - pickup.Requests.GetValueOrDefault(material));
                var take = Math.Min(Math.Min(contract.AmountKg - craft.Inventory[material], spare), craft.FreeCargoKg);
                if (take > 0) TryTransfer(pickup.Id, craft.Id, material, take);
                var held = craft.Inventory[material];
                if (held <= 0)
                {
                    job.Contract = null; // someone else took the stock: find another trip
                    WaitServe(craft, job, HaulJobState.ToPickup, $"{pickup.Name} no longer has spare {material}");
                    return;
                }
                job.Contract = contract with { AmountKg = held }; // carry what was there (maybe less than claimed)
                job.State = HaulJobState.ToDropoff;
                return;

            case HaulJobState.ToDropoff:
                switch (FlyToDock(craft, destination))
                {
                    case DockApproach.Docked: job.State = HaulJobState.Unloading; break;
                    case DockApproach.NoFreePort: WaitServe(craft, job, HaulJobState.ToDropoff, $"queued for a berth at {destination.Name}"); break;
                }
                return;

            case HaulJobState.Unloading:
                craft.Controls = ShipControls.Idle;
                if (craft.DockedToCraftId != destination.Id) { job.State = HaulJobState.ToDropoff; return; }
                var carrying = craft.Inventory[material];
                var room = Math.Min(carrying, MaxTransferKg(craft.Id, destination.Id, material));
                if (room > 0 && TryTransfer(craft.Id, destination.Id, material, room).Done) job.DeliveredKg += room;
                if (craft.Inventory[material] == 0)
                {
                    job.CompletedCycles++;
                    job.Contract = null;
                    job.State = HaulJobState.ToPickup; // next tick: look for the next trip
                }
                else if (room == 0)
                {
                    TryUndock(craft.Id);
                    WaitServe(craft, job, HaulJobState.ToDropoff, $"{destination.Name} is full", JobStationRetryS);
                }
                else job.Contract = contract with { AmountKg = craft.Inventory[material] };
                return;

            default:
                job.State = HaulJobState.ToPickup;
                return;
        }
    }

    private void WaitServe(CraftState craft, ServeRequestsJob job, HaulJobState resume, string reason, double retryAfterS = 0)
    {
        job.RetryAtTick = Tick + (long)(retryAfterS * FixedStepClock.TicksPerSecond);
        job.ResumeState = resume;
        job.WaitReason = reason;
        job.State = HaulJobState.Waiting;
        craft.Controls = new ShipControls(0, 0, !craft.IsDocked);
    }
}
