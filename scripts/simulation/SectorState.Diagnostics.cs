using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Where every kg in the sector is, by kind of holder (#19). The total never changes except through
/// debug cheats. <see cref="DiscoveredKg"/> is the mineable mass of star systems generated since the
/// start (#54): new world, not mass made from nothing, so it's taken out of the total.
/// </summary>
public sealed record MassLedger(
    long DepositsKg,
    long StockKg,
    long KitsKg,
    long OrdersKg,
    long RefiningKg,
    long CraftKg,
    long EjectedKg,
    long DiscoveredKg = 0)
{
    public long TotalKg => DepositsKg + StockKg + KitsKg + OrdersKg + RefiningKg + CraftKg + EjectedKg - DiscoveredKg;
}

/// <summary>Jobs that wait for the same reason, with the craft waiting.</summary>
public sealed record WaitGroup(string Reason, IReadOnlyList<CraftId> Crafts);

/// <summary>Read-only views over the sector for the diagnostics panel (#19). Nothing here changes state.</summary>
public sealed partial class SectorState
{
    /// <summary>
    /// The mass ledger: minable deposits, stock (inventories), construction kits, materials held by
    /// build orders and refinery batches, the craft themselves (their parts), and mass ejected.
    /// </summary>
    public MassLedger ComputeMassLedger()
    {
        long deposits = 0, stock = 0, kits = 0, orders = 0, refining = 0, craftKg = 0;
        foreach (var body in _bodies)
        {
            if (body.IsDismantlable) deposits += body.WholeMassKg; // all of it can be mined (#27)
            else foreach (var kg in body.DepositsKg.Values) deposits += kg;
        }
        foreach (var craft in _crafts)
        {
            stock += craft.Inventory.TotalKg;
            kits += craft.KitsKg;
            stock += craft.PenKg; // live animals (#93)
            foreach (var order in craft.BuildQueueList) foreach (var kg in order.MaterialsKg.Values) orders += kg;
            if (craft.RefiningBatch is { } batch) foreach (var kg in batch.InputsKg.Values) refining += kg;
            craftKg += (long)Math.Round(craft.Stats.TotalMassKg);
        }
        foreach (var swarm in _swarms) // their holds and the ships themselves (#30)
        {
            stock += swarm.Inventory.TotalKg;
            craftKg += swarm.Count * (long)Math.Round(swarm.Stats.TotalMassKg);
        }
        craftKg += MegaBuiltKg(); // megastructures (#126)
        long ejected = 0;
        foreach (var kg in _ejectedKg.Values) ejected += kg;
        return new MassLedger(deposits, stock, kits, orders, refining, craftKg, ejected, DiscoveredKg);
    }

    /// <summary>Active jobs that are waiting, grouped by reason, most crowded first (then A–Z).</summary>
    public IReadOnlyList<WaitGroup> WaitingJobs() =>
        _crafts.Where(c => c.Job is { Active: true, WaitReason: not null })
            .GroupBy(c => c.Job!.WaitReason!, StringComparer.Ordinal)
            .Select(g => new WaitGroup(g.Key, g.Select(c => c.Id).ToList()))
            .OrderByDescending(g => g.Crafts.Count).ThenBy(g => g.Reason, StringComparer.Ordinal)
            .ToList();
}
