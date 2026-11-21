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
    /// Queues <paramref name="design"/> (a ship or a station) at the yard and takes its materials from
    /// the yard's inventory now. A fixed structure (foundation) needs <paramref name="landingSite"/>:
    /// the body it will be placed on. Rejected, with reasons and the exact shortfall, if the yard can't
    /// build it or stock is short.
    /// </summary>
    public BuildOrderResult OrderBuild(CraftId yardId, ShipDesign design, PartCatalog catalog, string? shipName = null,
        BodyId landingSite = default)
    {
        var yard = GetCraft(yardId);
        if (!yard.IsShipyard) return BuildOrderResult.Rejected($"{yard.Name} has no fabricator.");

        var role = design.SuggestedRole(catalog);
        var errors = design.Validate(catalog, role);
        if (errors.Count > 0) return BuildOrderResult.Rejected(errors.Select(e => $"Design '{design.Name}': {e}").ToArray());

        var fixedStructure = design.ComputeStats(catalog).IsFixedStructure;
        if (fixedStructure && landingSite.IsNone)
            return BuildOrderResult.Rejected($"'{design.Name}' has a foundation: choose a body to place it on.");
        if (fixedStructure && !_byId.ContainsKey(landingSite)) return BuildOrderResult.Rejected($"No {landingSite} in this sector.");
        if (fixedStructure && !GetBody(landingSite).Surveyed)
            return BuildOrderResult.Rejected($"{GetBody(landingSite).Name} hasn't been surveyed: visit it with a ship you pilot first.");
        if (!fixedStructure) landingSite = BodyId.None;

        var bill = design.ComputeBillOfMaterials(catalog).ToWholeKg();
        var shortfall = yard.Inventory.Shortfall(bill);
        if (shortfall.Count > 0)
            return new BuildOrderResult(false, null,
                new[] { "Not enough materials: " + string.Join(", ", shortfall.Select(kv => $"{kv.Key} short by {kv.Value} kg")) },
                shortfall);

        if (!yard.Inventory.TryRemove(bill)) throw new InvalidOperationException("Inventory changed during an order."); // can't happen: single-threaded
        var id = new OrderId(_nextOrderId++);
        var order = new BuildOrder(id, shipName ?? $"{design.Name} #{id.Value}", design, design.ComputeStats(catalog), bill,
            landingSite: landingSite);
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
            if (!TryUsePower(yard, yard.Stats.FabricatorPowerKW * FixedStepClock.TickSeconds)) continue; // no power: no progress
            order.ProgressKg = Math.Min(order.TotalKg,
                order.ProgressKg + yard.Stats.FabricationRateKgPerS * Tuning.FabricationRateMultiplier * FixedStepClock.TickSeconds);
            if (order.ProgressKg >= order.TotalKg) (finished ??= new()).Add((yard, order));
        }
        if (finished is null) return;

        // Added after the loop: the craft list must not change while it is being iterated.
        foreach (var (yard, order) in finished)
        {
            yard.BuildQueueList.Remove(order);
            var craft = new CraftState(AllocateCraftId(), order.ShipName, order.Design, order.Stats, yard.Position, yard.HeadingRad);
            AddCraft(craft);
            if (!order.LandingSite.IsNone) PlaceOnSurface(GetBody(order.LandingSite), yard, craft, endS);
            else PlaceNextTo(yard, craft, endS);
        }
    }

    /// <summary>
    /// PLACEHOLDER for construction ships/sites: puts a finished fixed structure straight onto the
    /// chosen body's surface, on the side facing the yard (first free spot), nose away from the
    /// body so its foundation is on the ground. Landing anchors it (foundation).
    /// </summary>
    private void PlaceOnSurface(CelestialBodyState body, CraftState yard, CraftState craft, double timeS)
    {
        var bodyPos = GetPositionAt(body.Id, timeS);
        var toward = yard.Position - bodyPos;
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
