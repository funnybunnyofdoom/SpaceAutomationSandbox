using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

public enum YardWorkKind
{
    /// <summary>Change the craft's parts to a new design (#56).</summary>
    Refit,
    /// <summary>Take the craft apart for its materials (#57).</summary>
    Dismantle,
    /// <summary>Pack a station back into a construction kit, to deploy elsewhere (#57).</summary>
    PackKit,
}

/// <summary>Work a shipyard is doing on a craft: refit, dismantle, or pack as a kit. Held by the craft worked on.</summary>
public sealed record YardOrder(YardWorkKind Kind, CraftId YardId, ShipDesign? Target = null);

/// <summary>
/// Refits (#56), dismantling and packing stations into kits (#57), all done by a shipyard's fabricator
/// (after repairs, before building), one part at a time:
/// <list type="bullet">
/// <item><b>Where:</b> ships docked at the yard; stations (which can't move) within <see cref="YardReachM"/>
/// of it; the yard itself for a refit. Work pauses, with the reason, while the craft is out of reach.</item>
/// <item><b>Removing a part</b> returns <see cref="RecoveryFraction"/> of its materials, times its health,
/// to the yard's stock (whole kg, rounded down); the rest becomes slag in the stock, so every kg is
/// accounted for. <b>Adding a part</b> takes its full bill from the stock.</item>
/// <item><b>Refit:</b> parts that differ are removed first, then the new ones added. The craft keeps its
/// id, name, cargo, crew and job; the job pauses ("refitting at X") until the refit is done.</item>
/// <item><b>Dismantle:</b> parts come off one by one; when the last is gone the craft is removed. Its id is
/// never reused. Crew move to the yard and its job stops when the order is given.</item>
/// <item><b>Pack as kit:</b> an undamaged station becomes a construction kit in the yard's storage, with
/// nothing lost, after the yard has worked through its mass.</item>
/// </list>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Share of a removed part's materials recovered (times its health). The rest is slag.</summary>
    public const double RecoveryFraction = 0.9;

    /// <summary>Stations within this distance of a shipyard's hull can be worked on by it.</summary>
    public const double YardReachM = 500;

    /// <summary>The material losses from taking parts off become.</summary>
    public const string ScrapMaterial = "slag";

    /// <summary>Can this yard work on this craft now?</summary>
    public bool InYardReach(CraftState yard, CraftState craft) =>
        craft.Id == yard.Id || craft.DockedToCraftId == yard.Id
        || (!Movable(craft) && craft.Position.DistanceTo(yard.Position) - craft.CollisionRadiusM - yard.CollisionRadiusM <= YardReachM);

    /// <summary>Craft a yard can work on now: itself, ships docked at it, stations nearby (id order).</summary>
    public IReadOnlyList<CraftState> CraftInYardReach(CraftId yardId)
    {
        var yard = GetCraft(yardId);
        return _crafts.Where(c => InYardReach(yard, c)).ToList();
    }

    /// <summary>What taking one part off returns (whole kg) and how much becomes slag.</summary>
    private (SortedDictionary<string, long> Returned, long ScrapKg) RemovalYield(CraftState craft, DesignCell cell)
    {
        var bill = Parts!.Get(cell.PartId).Bill.ToWholeKg();
        var health = craft.HealthList.TryGetValue(cell.Pos, out var h) ? h : 1;
        var returned = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long scrap = 0;
        foreach (var (material, kg) in bill)
        {
            var back = (long)Math.Floor(kg * RecoveryFraction * health + 1e-9);
            if (back > 0) returned[material] = back;
            scrap += kg - back;
        }
        return (returned, scrap);
    }

    /// <summary>Preview of an order: materials it needs, materials it returns, slag, and part counts.</summary>
    public sealed record YardWorkPreview(IReadOnlyDictionary<string, long> CostKg, IReadOnlyDictionary<string, long> ReturnedKg,
        long ScrapKg, int PartsAdded, int PartsRemoved, double WorkKg);

    /// <summary>The parts a refit to <paramref name="target"/> removes and adds, in the order the yard does them.</summary>
    private static (List<DesignCell> Remove, List<DesignCell> Add) RefitDiff(ShipDesign current, ShipDesign target)
    {
        var wanted = target.Cells.ToDictionary(c => c.Pos);
        var remove = current.Cells.Where(c => !wanted.TryGetValue(c.Pos, out var w) || w.PartId != c.PartId || w.Rotation != c.Rotation)
            .OrderBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).ToList();
        var have = current.Cells.ToDictionary(c => c.Pos);
        var add = target.Cells.Where(c => !have.TryGetValue(c.Pos, out var h) || h.PartId != c.PartId || h.Rotation != c.Rotation).ToList();
        return (remove, add);
    }

    public YardWorkPreview PreviewRefit(CraftId craftId, ShipDesign target)
    {
        var craft = GetCraft(craftId);
        var (remove, add) = RefitDiff(craft.Design, target);
        var cost = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var returned = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long scrap = 0;
        double work = 0;
        foreach (var cell in remove)
        {
            var (back, lost) = RemovalYield(craft, cell);
            foreach (var (m, kg) in back) returned[m] = returned.GetValueOrDefault(m) + kg;
            scrap += lost;
            work += Parts!.Get(cell.PartId).Bill.ToWholeKg().Values.Sum();
        }
        foreach (var cell in add)
        {
            var bill = Parts!.Get(cell.PartId).Bill.ToWholeKg();
            foreach (var (m, kg) in bill) cost[m] = cost.GetValueOrDefault(m) + kg;
            work += bill.Values.Sum();
        }
        return new YardWorkPreview(cost, returned, scrap, add.Count, remove.Count, work);
    }

    public YardWorkPreview PreviewDismantle(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        var returned = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long scrap = 0;
        double work = 0;
        foreach (var cell in craft.Design.Cells)
        {
            var (back, lost) = RemovalYield(craft, cell);
            foreach (var (m, kg) in back) returned[m] = returned.GetValueOrDefault(m) + kg;
            scrap += lost;
            work += back.Values.Sum() + lost;
        }
        return new YardWorkPreview(new Dictionary<string, long>(), returned, scrap, 0, craft.Design.Cells.Count, work);
    }

    /// <summary>Common checks for an order on a craft at a yard; null when fine.</summary>
    private string? YardOrderBlocker(CraftState yard, CraftState craft)
    {
        if (Parts is null) return "This game has no part catalog.";
        if (!yard.IsShipyard) return $"{yard.Name} has no fabricator.";
        if (craft.YardOrder is { } existing) return $"{craft.Name} already has a {existing.Kind.ToString().ToLowerInvariant()} order (cancel it first).";
        if (!InYardReach(yard, craft))
            return Movable(craft) ? $"{craft.Name} must be docked at {yard.Name}." : $"{craft.Name} must be within {YardReachM:0} m of {yard.Name}.";
        return null;
    }

    /// <summary>Everything on board must be gone before a craft is taken apart or packed.</summary>
    private string? EmptyBlocker(CraftState craft)
    {
        if (craft.Inventory.TotalKg > 0) return $"{craft.Name} still holds {craft.Inventory.TotalKg / 1000.0:0.0} t of cargo: unload it first.";
        if (craft.Kits.Count > 0) return $"{craft.Name} still holds construction kits: unload them first.";
        if (craft.PenKg > 0) return $"{craft.Name} still holds animals: release them first.";
        if (craft.BuildQueue.Count > 0) return $"{craft.Name} has build orders: cancel them first.";
        if (craft.RefiningBatch is not null) return $"{craft.Name} is refining: wait for the batch to finish.";
        if (_crafts.Any(c => c.DockedToCraftId == craft.Id)) return $"Ships are docked at {craft.Name}: undock them first.";
        if (craft.Id == PilotCraftId) return $"You're flying {craft.Name}: switch to another craft first.";
        return null;
    }

    public ActionResult OrderRefit(CraftId yardId, CraftId craftId, ShipDesign target)
    {
        var yard = GetCraft(yardId);
        var craft = GetCraft(craftId);
        if (YardOrderBlocker(yard, craft) is { } blocked) return new(false, blocked);
        var parts = Parts!;
        if (craft.IsLanded) return new(false, $"{craft.Name} is landed: refits are done in space.");
        var role = (craft.IntactDesign ?? craft.Design).SuggestedRole(parts);
        if (target.SuggestedRole(parts) != role) return new(false, $"{target.Name} is a {target.SuggestedRole(parts).ToString().ToLowerInvariant()}, {craft.Name} a {role.ToString().ToLowerInvariant()}.");
        var errors = target.Validate(parts, role);
        if (errors.Count > 0) return new(false, $"Design '{target.Name}': {string.Join("; ", errors)}");
        var (remove, add) = RefitDiff(craft.Design, target);
        if (remove.Count == 0 && add.Count == 0) return new(false, $"{craft.Name} already matches {target.Name}.");
        if (LockedPartsReason(add.Select(c => c.PartId)) is { } locked) return new(false, locked); // research (#37)
        if (remove.Count == craft.Design.Cells.Count) return new(false, "A refit must keep at least one part as it is (otherwise build a new craft).");
        var stats = target.ComputeStats(parts);
        if (craft.HasCargoLimit && craft.CargoKg > stats.CargoCapacityKg)
            return new(false, $"{craft.CargoKg / 1000.0:0.0} t of cargo won't fit in {target.Name} ({stats.CargoCapacityKg / 1000.0:0.0} t): unload some first.");
        if (!craft.LegacyNoCrew && craft.Crew > stats.CrewCapacity)
            return new(false, $"{craft.Crew} crew would have nowhere to live ({target.Name} houses {stats.CrewCapacity}): move them first.");
        if (craft.Id == yard.Id && !yard.Design.Cells.Any(c => parts.Get(c.PartId).FabricationRateKgPerS > 0
                                                             && target.Cells.Any(t => t.Pos == c.Pos && t.PartId == c.PartId && t.Rotation == c.Rotation)))
            return new(false, $"{yard.Name} can't refit itself without keeping one of its fabricators where it is.");

        craft.IntactDesign = null; // the refit replaces whatever broke off
        craft.RepairProgressKg = 0;
        craft.YardOrder = new YardOrder(YardWorkKind.Refit, yard.Id, target);
        craft.YardProgressKg = 0;
        var preview = PreviewRefit(craft.Id, target);
        return new(true, $"{craft.Name}: refit to {target.Name} at {yard.Name} ({preview.PartsRemoved} parts off, {preview.PartsAdded} on).");
    }

    public ActionResult OrderDismantle(CraftId yardId, CraftId craftId)
    {
        var yard = GetCraft(yardId);
        var craft = GetCraft(craftId);
        if (craft.Id == yard.Id) return new(false, $"{yard.Name} can't take itself apart: use another shipyard.");
        if (YardOrderBlocker(yard, craft) is { } blocked) return new(false, blocked);
        if (EmptyBlocker(craft) is { } notEmpty) return new(false, notEmpty);
        if (craft.Crew > 0 && !craft.LegacyNoCrew && yard.Stats.CrewCapacity - yard.Crew < craft.Crew)
            return new(false, $"{yard.Name} has habitat room for {Math.Max(0, yard.Stats.CrewCapacity - yard.Crew)} of {craft.Name}'s {craft.Crew} crew.");
        StopForYardWork(craft, yard);
        craft.YardOrder = new YardOrder(YardWorkKind.Dismantle, yard.Id);
        craft.YardProgressKg = 0;
        var preview = PreviewDismantle(craft.Id);
        return new(true, $"{craft.Name}: dismantling at {yard.Name}: {preview.ReturnedKg.Values.Sum() / 1000.0:0.0} t back, {preview.ScrapKg / 1000.0:0.0} t slag.");
    }

    public ActionResult OrderPackKit(CraftId yardId, CraftId craftId)
    {
        var yard = GetCraft(yardId);
        var craft = GetCraft(craftId);
        if (craft.Id == yard.Id) return new(false, $"{yard.Name} can't pack itself.");
        if (YardOrderBlocker(yard, craft) is { } blocked) return new(false, blocked);
        if (craft.Design.SuggestedRole(Parts!) != DesignRole.Station) return new(false, $"{craft.Name} is a ship: only stations pack into kits.");
        if (NeedsRepair(craft)) return new(false, $"{craft.Name} is damaged: repair it first (or dismantle it).");
        if (EmptyBlocker(craft) is { } notEmpty) return new(false, notEmpty);
        if (craft.Crew > 0 && !craft.LegacyNoCrew && yard.Stats.CrewCapacity - yard.Crew < craft.Crew)
            return new(false, $"{yard.Name} has habitat room for {Math.Max(0, yard.Stats.CrewCapacity - yard.Crew)} of {craft.Name}'s {craft.Crew} crew.");
        if (yard.HasCargoLimit && yard.FreeCargoKg < craft.Stats.TotalMassKg)
            return new(false, $"{yard.Name} needs {craft.Stats.TotalMassKg / 1000:0.0} t of free storage for the kit.");
        StopForYardWork(craft, yard);
        craft.YardOrder = new YardOrder(YardWorkKind.PackKit, yard.Id);
        craft.YardProgressKg = 0;
        return new(true, $"{craft.Name}: packing into a kit at {yard.Name}.");
    }

    /// <summary>Before dismantling or packing: the job stops and the crew move to the yard.</summary>
    private static void StopForYardWork(CraftState craft, CraftState yard)
    {
        if (craft.Job is { Active: true } job)
        {
            job.Active = false;
            job.WaitReason = $"stopped: {craft.Name} is being taken apart at {yard.Name}";
        }
        craft.Controls = ShipControls.Idle;
        if (craft.Crew > 0 && !craft.LegacyNoCrew)
        {
            yard.Crew += craft.Crew;
            craft.Crew = 0;
        }
    }

    /// <summary>
    /// For a craft whose job is running and which has a yard order (a refit): the job pauses. Docked (or
    /// in reach), it stays put; otherwise it flies to the yard and docks. True when it handled the craft.
    /// </summary>
    private bool StepYardOrder(CraftState craft, ShipJob job)
    {
        if (craft.YardOrder is not { } order || !_craftsById.TryGetValue(order.YardId, out var yard)) return false;
        job.ServiceStatus = null;
        var what = order.Kind switch { YardWorkKind.Refit => $"a refit to {order.Target!.Name}", YardWorkKind.Dismantle => "dismantling", _ => "packing" };
        if (InYardReach(yard, craft))
        {
            craft.Controls = ShipControls.Idle;
            job.ServiceStatus = craft.YardStatus is { } now && now.StartsWith("waiting", StringComparison.Ordinal) ? now : $"in for {what} at {yard.Name}";
            return true;
        }
        switch (FlyToDock(craft, yard))
        {
            case DockApproach.Docked:
                craft.Controls = ShipControls.Idle;
                job.ServiceStatus = $"in for {what} at {yard.Name}";
                break;
            case DockApproach.NoFreePort:
                craft.Controls = new ShipControls(0, 0, true);
                job.ServiceStatus = $"queued for {what} at {yard.Name}";
                break;
            default:
                job.ServiceStatus = $"flying to {yard.Name} for {what}";
                break;
        }
        return true;
    }

    /// <summary>Cancels a yard order. Parts already changed stay as they are.</summary>
    public ActionResult CancelYardWork(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (craft.YardOrder is not { } order) return new(false, $"{craft.Name} has no yard order.");
        craft.YardOrder = null;
        craft.YardProgressKg = 0;
        return new(true, $"{craft.Name}: {order.Kind.ToString().ToLowerInvariant()} cancelled; parts already changed stay as they are.");
    }

    /// <summary>Restores a saved order.</summary>
    public void RestoreYardOrder(CraftId craftId, YardOrder? order, double progressKg)
    {
        var craft = GetCraft(craftId);
        if (order is not null)
        {
            GetCraft(order.YardId);
            if (order.Kind == YardWorkKind.Refit && order.Target is null) throw new ArgumentException("A refit order needs a target design.");
        }
        if (!(progressKg >= 0)) throw new ArgumentOutOfRangeException(nameof(progressKg));
        craft.YardOrder = order;
        craft.YardProgressKg = progressKg;
    }

    /// <summary>Restores the craft id counter (never lowers it), so ids of removed craft aren't reused.</summary>
    public void RestoreNextCraftId(uint next)
    {
        if (next < 1) throw new ArgumentOutOfRangeException(nameof(next));
        _nextCraftId = Math.Max(_nextCraftId, next);
    }

    /// <summary>Next craft id to hand out (saved, so removed craft's ids are never reused).</summary>
    public uint NextCraftId => _nextCraftId;

    /// <summary>
    /// One tick of yard work at a yard with no repair to do: the first craft (by id) whose order is for
    /// this yard. Returns true if the fabricator was used.
    /// </summary>
    private bool StepYardWork(CraftState yard, List<CraftState>? removed)
    {
        CraftState? craft = null;
        foreach (var c in _crafts)
        {
            if (c.YardOrder?.YardId != yard.Id) continue;
            if (!InYardReach(yard, c))
            {
                c.YardStatus = Movable(c) ? $"waiting: dock at {yard.Name} to continue" : $"waiting: too far from {yard.Name}";
                continue;
            }
            craft ??= c;
            if (c != craft) c.YardStatus = $"waiting: {yard.Name} is working on {craft.Name}";
        }
        if (craft is null) return false;
        var order = craft.YardOrder!;
        var verb = order.Kind switch { YardWorkKind.Refit => "refitting", YardWorkKind.Dismantle => "dismantling", _ => "packing" };
        if (!Operational(yard)) { craft.YardStatus = $"waiting: {verb} at {yard.Name} stopped: {yard.CrewShortReason}"; return false; }

        // This step's work and what it needs.
        DesignCell? removeCell = null, addCell = null;
        double work;
        switch (order.Kind)
        {
            case YardWorkKind.Refit:
                var (remove, add) = RefitDiff(craft.Design, order.Target!);
                if (remove.Count > 0) removeCell = remove[0];
                else if (add.Count > 0) addCell = add[0];
                else { FinishRefit(craft, order.Target!); return false; }
                work = Parts!.Get((removeCell ?? addCell)!.Value.PartId).Bill.ToWholeKg().Values.Sum();
                break;
            case YardWorkKind.Dismantle:
                removeCell = craft.Design.Cells.OrderBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).First();
                work = Parts!.Get(removeCell.Value.PartId).Bill.ToWholeKg().Values.Sum();
                break;
            default:
                work = Math.Round(craft.Stats.TotalMassKg);
                break;
        }
        work = Math.Max(1, work);

        if (craft.YardProgressKg < work)
        {
            if (!TryUsePower(yard, yard.Stats.FabricatorPowerKW * FixedStepClock.TickSeconds))
            {
                craft.YardStatus = $"waiting: {verb} at {yard.Name} stopped: no power";
                return true;
            }
            craft.YardProgressKg = Math.Min(work, craft.YardProgressKg + yard.Stats.FabricationRateKgPerS * Tuning.FabricationRateMultiplier * FixedStepClock.TickSeconds);
            craft.YardStatus = $"{verb} at {yard.Name}" + StepLabel(craft, removeCell, addCell, work);
            if (craft.YardProgressKg < work) return true;
        }

        if (removeCell is { } off)
        {
            var (back, scrap) = RemovalYield(craft, off);
            var total = back.Values.Sum() + scrap;
            if (yard.HasCargoLimit && yard.FreeCargoKg < total)
            {
                craft.YardStatus = $"waiting: {verb} at {yard.Name} needs {total / 1000.0:0.0} t of free storage there";
                return true;
            }
            yard.Inventory.Add(back);
            if (scrap > 0) yard.Inventory.Add(ScrapMaterial, scrap);
            craft.YardProgressKg = 0;
            if (craft.Design.Cells.Count == 1) { (removed ??= new()).Add(craft); return true; } // the last part: the craft is gone
            craft.HealthList.Remove(off.Pos);
            ReplaceDesign(craft, new ShipDesign(craft.Design.Id, craft.Design.Name, craft.Design.Cells.Where(c => c.Pos != off.Pos)));
            return true;
        }
        if (addCell is { } on)
        {
            var bill = Parts!.Get(on.PartId).Bill.ToWholeKg();
            var shortfall = yard.Inventory.Shortfall(bill);
            if (shortfall.Count > 0)
            {
                craft.YardStatus = $"waiting: {verb} at {yard.Name} needs {Describe(shortfall)} more";
                return true;
            }
            yard.Inventory.TryRemove(bill);
            craft.YardProgressKg = 0;
            var target = order.Target!;
            var present = craft.Design.Cells.Where(c => c.Pos != on.Pos).Append(on).ToList();
            ReplaceDesign(craft, new ShipDesign(craft.Design.Id, craft.Design.Name, present));
            if (RefitDiff(craft.Design, target) is { Remove.Count: 0, Add.Count: 0 }) FinishRefit(craft, target);
            return true;
        }

        // Packing: done when the work is.
        var kitMass = Math.Round(craft.Stats.TotalMassKg);
        if (yard.HasCargoLimit && yard.FreeCargoKg < kitMass)
        {
            craft.YardStatus = $"waiting: packing at {yard.Name} needs {kitMass / 1000:0.0} t of free storage there";
            return true;
        }
        var materials = craft.Design.ComputeBillOfMaterials(Parts!).ToWholeKg();
        // The kit remembers the station, so deploying it brings back the same station (#24).
        yard.KitsList.Add(new ConstructionKit(new OrderId(_nextOrderId++), craft.Name, craft.Design, craft.Design.ComputeStats(Parts!), materials)
        {
            OriginalCraftId = craft.Id,
            Requests = new Dictionary<string, long>(craft.Requests),
        });
        (removed ??= new()).Add(craft);
        return true;
    }

    private string StepLabel(CraftState craft, DesignCell? off, DesignCell? on, double work)
    {
        var percent = $"{craft.YardProgressKg / work * 100:0}%";
        if (off is { } r) return $": taking off {Parts!.Get(r.PartId).Name} ({percent}, {craft.Design.Cells.Count} parts on board)";
        if (on is { } a) return $": fitting {Parts!.Get(a.PartId).Name} ({percent})";
        return $" ({percent})";
    }

    private void FinishRefit(CraftState craft, ShipDesign target)
    {
        // Same cells as the target now: take on its identity (id and name of the blueprint).
        if (!ReferenceEquals(craft.Design, target)) ReplaceDesign(craft, target);
        craft.YardOrder = null;
        craft.YardProgressKg = 0;
        craft.YardStatus = $"refit to {target.Name}: done";
    }

    /// <summary>
    /// Swaps a craft's design (parts added or removed), keeping the unchanged parts where they were in
    /// the world. Ships docked at it let go if its ports changed.
    /// </summary>
    private void ReplaceDesign(CraftState craft, ShipDesign design)
    {
        var oldStats = craft.Stats;
        craft.Design = design;
        craft.Stats = design.ComputeStats(Parts!, craft.HealthList);
        var shift = new Vector2D((craft.Stats.CenterOfMassX - oldStats.CenterOfMassX) * ShipDesign.CellSizeM,
            (craft.Stats.CenterOfMassY - oldStats.CenterOfMassY) * ShipDesign.CellSizeM);
        var (sin, cos) = Math.SinCos(craft.HeadingRad);
        craft.Position += new Vector2D(shift.X * cos - shift.Y * sin, shift.X * sin + shift.Y * cos);
        craft.StoredEnergyKJ = Math.Min(craft.StoredEnergyKJ, craft.Stats.EnergyCapacityKJ);
        if (!craft.Stats.DockingPorts.SequenceEqual(oldStats.DockingPorts))
            foreach (var ship in _crafts.Where(s => s.DockedToCraftId == craft.Id).ToList())
                ship.DockedToCraftId = CraftId.None;
    }

    /// <summary>
    /// Removes a craft from the sector (dismantled or packed). Its id is never reused. References to it
    /// are cleared; jobs elsewhere that used it see it missing and wait with that reason.
    /// </summary>
    private void RemoveCraft(CraftState craft)
    {
        _crafts.Remove(craft);
        _craftsById.Remove(craft.Id);
        if (PilotCraftId == craft.Id) PilotCraftId = CraftId.None;
        foreach (var c in _crafts)
        {
            if (c.TargetCraftId == craft.Id) c.TargetCraftId = CraftId.None;
            if (c.DockedToCraftId == craft.Id) c.DockedToCraftId = CraftId.None;
            if (c.DockClaimStation == craft.Id) { c.DockClaimStation = CraftId.None; c.DockClaimPort = 0; }
            if (c.Transit?.TargetCraft == craft.Id) c.Transit = null;
            if (c.ServiceAt == craft.Id) c.ServiceAt = CraftId.None;
            if (c.YardOrder?.YardId == craft.Id) { c.YardOrder = null; c.YardProgressKg = 0; }
            c.BrakeAgainst = CraftId.None;
        }
    }
}
