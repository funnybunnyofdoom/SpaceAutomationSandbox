using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Shipyards: accept build orders against their inventory, assemble the first order in the queue
/// at the yard's fabrication rate, and place the finished craft next to the yard.
/// Mass is conserved: materials move from the inventory into the order, then become the craft.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Clearance between a new craft and its neighbours when it is placed.</summary>
    public const double ParkingGapM = 10;

    private long _nextOrderId = 1;

    /// <summary>Next order id to hand out (saved, so ids are never reused).</summary>
    public long NextOrderId => _nextOrderId;

    /// <summary>
    /// Queues <paramref name="design"/> at the yard and takes its materials from the yard's inventory now.
    /// Rejected, with reasons and the exact shortfall, if the yard can't build or stock is short.
    /// </summary>
    public BuildOrderResult OrderBuild(CraftId yardId, ShipDesign design, PartCatalog catalog, string? shipName = null)
    {
        var yard = GetCraft(yardId);
        if (!yard.IsShipyard) return BuildOrderResult.Rejected($"{yard.Name} has no fabricator.");

        var errors = design.Validate(catalog, DesignRole.Ship);
        if (errors.Count > 0) return BuildOrderResult.Rejected(errors.Select(e => $"Design '{design.Name}': {e}").ToArray());

        var bill = design.ComputeBillOfMaterials(catalog).ToWholeKg();
        var shortfall = yard.Inventory.Shortfall(bill);
        if (shortfall.Count > 0)
            return new BuildOrderResult(false, null,
                new[] { "Not enough materials: " + string.Join(", ", shortfall.Select(kv => $"{kv.Key} short by {kv.Value} kg")) },
                shortfall);

        if (!yard.Inventory.TryRemove(bill)) throw new InvalidOperationException("Inventory changed during an order."); // can't happen: single-threaded
        var id = new OrderId(_nextOrderId++);
        var order = new BuildOrder(id, shipName ?? $"{design.Name} #{id.Value}", design, design.ComputeStats(catalog), bill);
        yard.BuildQueueList.Add(order);
        return new BuildOrderResult(true, id, Array.Empty<string>(), new Dictionary<string, long>());
    }

    /// <summary>Removes an order and returns all of its materials to the yard. False if there is no such order.</summary>
    public bool CancelBuild(CraftId yardId, OrderId orderId)
    {
        var yard = GetCraft(yardId);
        var order = yard.BuildQueueList.FirstOrDefault(o => o.Id == orderId);
        if (order is null) return false;
        yard.BuildQueueList.Remove(order);
        yard.Inventory.Add(order.MaterialsKg);
        return true;
    }

    /// <summary>Restores a saved order exactly (its materials are already out of the inventory).</summary>
    public void RestoreOrder(CraftId yardId, BuildOrder order)
    {
        var yard = GetCraft(yardId);
        if (yard.BuildQueueList.Any(o => o.Id == order.Id) || _crafts.Any(c => c.BuildQueueList.Any(o => o.Id == order.Id)))
            throw new InvalidOperationException($"Duplicate {order.Id}.");
        yard.BuildQueueList.Add(order);
        if (order.Id.Value >= _nextOrderId) _nextOrderId = order.Id.Value + 1;
    }

    /// <summary>Restores the order counter from a save (never lowers it).</summary>
    public void RestoreNextOrderId(long next)
    {
        if (next < 1) throw new ArgumentOutOfRangeException(nameof(next));
        _nextOrderId = Math.Max(_nextOrderId, next);
    }

    private void StepFabrication(double endS)
    {
        List<(CraftState Yard, BuildOrder Order)>? finished = null;
        foreach (var yard in _crafts)
        {
            if (yard.BuildQueueList.Count == 0 || !yard.IsShipyard) continue;
            var order = yard.BuildQueueList[0];
            order.ProgressKg = Math.Min(order.TotalKg, order.ProgressKg + yard.Stats.FabricationRateKgPerS * FixedStepClock.TickSeconds);
            if (order.ProgressKg >= order.TotalKg) (finished ??= new()).Add((yard, order));
        }
        if (finished is null) return;

        // Added after the loop: the craft list must not change while it is being iterated.
        foreach (var (yard, order) in finished)
        {
            yard.BuildQueueList.Remove(order);
            var craft = new CraftState(AllocateCraftId(), order.ShipName, order.Design, order.Stats, yard.Position, yard.HeadingRad);
            AddCraft(craft);
            PlaceNextTo(yard, craft, endS);
        }
    }

    /// <summary>
    /// Parks a new craft beside its yard, matched to the yard's motion: on the same orbit (a little
    /// behind it), on the same body's surface, or beside it in free space. Takes the first free slot.
    /// </summary>
    private void PlaceNextTo(CraftState yard, CraftState craft, double timeS)
    {
        for (var slot = 1; slot <= 1000; slot++)
        {
            var distance = yard.CollisionRadiusM + craft.CollisionRadiusM + ParkingGapM
                           + (slot - 1) * (2 * craft.CollisionRadiusM + ParkingGapM);

            if (yard.RailOrbit is { } orbit)
            {
                var parking = orbit with { PhaseAtZeroRad = orbit.PhaseAtZeroRad - distance / orbit.RadiusM };
                var position = RailPositionAt(parking, timeS);
                if (!IsFree(position, craft)) continue;
                craft.RailOrbit = parking;
                craft.Position = craft.PreviousPosition = position;
                craft.Velocity = RailVelocityAt(parking, timeS);
                return;
            }

            if (yard.IsLanded)
            {
                var body = GetBody(yard.LandedOnBodyId);
                var radius = body.RadiusM + craft.CollisionRadiusM;
                var angle = Math.Atan2(yard.LandedOffset.Y, yard.LandedOffset.X) - distance / radius;
                var offset = new Vector2D(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
                var position = GetPositionAt(body.Id, timeS) + offset;
                if (!IsFree(position, craft)) continue;
                craft.LandedOnBodyId = body.Id;
                craft.LandedOffset = offset;
                craft.Position = craft.PreviousPosition = position;
                craft.Velocity = GetVelocityAt(body.Id, timeS);
                OnLanded(craft);
                return;
            }

            var free = yard.Position + new Vector2D(distance, 0);
            if (!IsFree(free, craft)) continue;
            craft.Position = craft.PreviousPosition = free;
            craft.Velocity = yard.Velocity;
            return;
        }
        throw new InvalidOperationException($"No free parking slot near {yard.Id}."); // 1000 slots: not reachable in practice
    }

    private bool IsFree(Vector2D position, CraftState placing) =>
        _crafts.All(other => ReferenceEquals(other, placing)
                             || other.Position.DistanceTo(position) >= other.CollisionRadiusM + placing.CollisionRadiusM + ParkingGapM / 2);
}
