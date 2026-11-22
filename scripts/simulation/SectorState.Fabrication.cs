using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Shipyards: accept build orders against their inventory, assemble the first order in the queue
/// at the yard's fabrication rate, and place the finished ship next to the yard, or store the
/// finished station as a construction kit (#8).
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
    /// Queues <paramref name="design"/> at the yard and takes its materials from the yard's inventory
    /// now. A ship design finishes as a ship beside the yard; a station design finishes as a
    /// construction kit in the yard's storage (#8), for a ship to carry and deploy. Rejected, with
    /// reasons and the exact shortfall, if the yard can't build it or stock is short.
    /// </summary>
    public BuildOrderResult OrderBuild(CraftId yardId, ShipDesign design, PartCatalog catalog, string? shipName = null)
    {
        var yard = GetCraft(yardId);
        if (!yard.IsShipyard) return BuildOrderResult.Rejected($"{yard.Name} has no fabricator.");

        var role = design.SuggestedRole(catalog);
        var errors = design.Validate(catalog, role);
        if (errors.Count > 0) return BuildOrderResult.Rejected(errors.Select(e => $"Design '{design.Name}': {e}").ToArray());

        var bill = design.ComputeBillOfMaterials(catalog).ToWholeKg();
        var shortfall = yard.Inventory.Shortfall(bill);
        if (shortfall.Count > 0)
            return new BuildOrderResult(false, null,
                new[] { "Not enough materials: " + string.Join(", ", shortfall.Select(kv => $"{kv.Key} short by {kv.Value} kg")) },
                shortfall);

        if (!yard.Inventory.TryRemove(bill)) throw new InvalidOperationException("Inventory changed during an order."); // can't happen: single-threaded
        var id = new OrderId(_nextOrderId++);
        var order = new BuildOrder(id, shipName ?? $"{design.Name} #{id.Value}", design, design.ComputeStats(catalog), bill,
            isKit: role == DesignRole.Station);
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
        if (OrderIdInUse(order.Id))
            throw new InvalidOperationException($"Duplicate {order.Id}.");
        yard.BuildQueueList.Add(order);
        if (order.Id.Value >= _nextOrderId) _nextOrderId = order.Id.Value + 1;
    }

    private bool OrderIdInUse(OrderId id) =>
        _crafts.Any(c => c.BuildQueueList.Any(o => o.Id == id) || c.KitsList.Any(k => k.Id == id));

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
            if (order.ProgressKg >= order.TotalKg) { (finished ??= new()).Add((yard, order)); continue; } // a kit waiting for room
            if (!Operational(yard)) continue; // no crew (or no supplies): no progress (#11)
            if (!TryUsePower(yard, yard.Stats.FabricatorPowerKW * FixedStepClock.TickSeconds)) continue; // no power: no progress
            order.ProgressKg = Math.Min(order.TotalKg,
                order.ProgressKg + yard.Stats.FabricationRateKgPerS * Tuning.FabricationRateMultiplier * FixedStepClock.TickSeconds);
            if (order.ProgressKg >= order.TotalKg) (finished ??= new()).Add((yard, order));
        }
        if (finished is null) return;

        // Added after the loop: the craft list must not change while it is being iterated.
        foreach (var (yard, order) in finished)
        {
            if (order.IsKit)
            {
                var kit = new ConstructionKit(order.Id, order.ShipName, order.Design, order.Stats, order.MaterialsKg);
                order.WaitingForRoom = yard.FreeCargoKg < kit.MassKg;
                if (order.WaitingForRoom) continue; // finished, but the kit doesn't fit in storage yet
                yard.BuildQueueList.Remove(order);
                yard.KitsList.Add(kit);
                continue;
            }
            yard.BuildQueueList.Remove(order);
            var craft = new CraftState(AllocateCraftId(), order.ShipName, order.Design, order.Stats, yard.Position, yard.HeadingRad);
            AddCraft(craft);
            PlaceNextTo(yard, craft, endS);
        }
    }

    /// <summary>
    /// Puts a structure onto a body's surface, on the side facing <paramref name="near"/> (first free
    /// spot), nose away from the body so a foundation is on the ground. Landing anchors a foundation.
    /// Used when a construction kit is deployed (#8).
    /// </summary>
    private void PlaceOnSurface(CelestialBodyState body, CraftState near, CraftState craft, double timeS)
    {
        var bodyPos = GetPositionAt(body.Id, timeS);
        var toward = near.Position - bodyPos;
        var baseAngle = toward.Length > 0 ? Math.Atan2(toward.Y, toward.X) : -Math.PI / 2;
        var radius = body.RadiusM + craft.CollisionRadiusM;
        for (var slot = 0; slot <= 2000; slot++)
        {
            // 0, +1, −1, +2, −2 … steps of one craft width along the surface.
            var step = (slot + 1) / 2 * (slot % 2 == 1 ? 1 : -1) * (2 * craft.CollisionRadiusM + ParkingGapM);
            var angle = baseAngle + step / radius;
            var offset = new Vector2D(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            if (!IsFree(bodyPos + offset, craft)) continue;
            craft.HeadingRad = craft.PreviousHeadingRad = FlightModel.WrapAngle(Math.Atan2(offset.X, -offset.Y)); // forward = outward
            var settled = SettleOnSurface(craft, bodyPos + offset, bodyPos, body.RadiusM); // rest on the foundation row
            craft.LandedOnBodyId = body.Id;
            craft.LandedOffset = settled - bodyPos;
            craft.Position = craft.PreviousPosition = settled;
            craft.Velocity = GetVelocityAt(body.Id, timeS);
            OnLanded(craft);
            return;
        }
        throw new InvalidOperationException($"No free spot on {body.Name}.");
    }

    /// <summary>
    /// Parks a new craft beside its yard, matched to the yard's motion: on the same orbit (a little
    /// behind it), on the same body's surface, or beside it in free space. Takes the first free slot.
    /// </summary>
    private void PlaceNextTo(CraftState yard, CraftState craft, double timeS)
    {
        // A free docking port first: the new ship starts docked at its yard.
        if (craft.Stats.TotalThrustN > 0 && FreePorts(yard).FirstOrDefault(-1) is var port and >= 0)
        {
            DockAt(craft, yard, port);
            return;
        }

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
                var bodyCentre = GetPositionAt(body.Id, timeS);
                var settled = SettleOnSurface(craft, position, bodyCentre, body.RadiusM);
                craft.LandedOnBodyId = body.Id;
                craft.LandedOffset = settled - bodyCentre;
                craft.Position = craft.PreviousPosition = settled;
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
