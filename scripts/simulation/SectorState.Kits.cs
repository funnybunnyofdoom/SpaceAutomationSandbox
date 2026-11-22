using System;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Construction kits (#8): moving them across a dock, and deploying one where the station should
/// stand. A kit is deployed by whoever holds it:
/// <list type="bullet">
/// <item>landed on a body: the station goes onto that surface beside it (a foundation anchors);</item>
/// <item>parked in orbit (on rails): the station joins the same orbit, a little behind;</item>
/// <item>flying close to a body and nearly matched to it: the station is put on a circular orbit
/// there (PLACEHOLDER: no gravity yet, so "in orbit" means held on rails, docs/06).</item>
/// </list>
/// Structures with foundations can only be deployed while landed. The kit's materials become the
/// station, so mass is conserved.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Deploying in flight needs a body surface within this distance.</summary>
    public const double DeployMaxAltitudeM = 20_000;

    /// <summary>…and the carrier nearly matched to that body.</summary>
    public const double DeployMaxRelativeSpeedMps = 20;

    /// <summary>Orbit period given to stations deployed in flight (same as the starter yard's).</summary>
    public const double DeployOrbitPeriodS = NewGame.ShipyardOrbitPeriodS;

    /// <summary>Moves a kit between two craft docked together, if the receiver has room for it.</summary>
    public ActionResult TryTransferKit(CraftId fromId, CraftId toId, OrderId kitId)
    {
        var from = GetCraft(fromId);
        var to = GetCraft(toId);
        if (from.DockedToCraftId != to.Id && to.DockedToCraftId != from.Id)
            return new(false, $"{from.Name} and {to.Name} must be docked together to move a kit.");
        var kit = from.KitsList.FirstOrDefault(k => k.Id == kitId);
        if (kit is null) return new(false, $"{from.Name} has no such kit.");
        if (to.FreeCargoKg < kit.MassKg)
            return new(false, $"{to.Name} has room for {to.FreeCargoKg / 1000.0:0.0} t; the kit needs {kit.MassKg / 1000.0:0.0} t.");
        from.KitsList.Remove(kit);
        to.KitsList.Add(kit);
        return new(true, $"Moved kit '{kit.Name}' from {from.Name} to {to.Name}.");
    }

    /// <summary>Why <paramref name="craftId"/> can't deploy its first kit here, or null if it can.</summary>
    public string? DeployBlocker(CraftId craftId) =>
        GetCraft(craftId).Kits.FirstOrDefault() is { } kit ? DeployBlocker(GetCraft(craftId), kit) : "No kit on board.";

    private string? DeployBlocker(CraftState carrier, ConstructionKit kit)
    {
        if (carrier.IsDocked) return "Undock first.";
        if (carrier.IsLanded) return null;
        if (kit.NeedsSurface) return $"'{kit.Name}' has foundations: land on a body to deploy it.";
        if (carrier.IsOnRails) return null;
        var (body, altitude) = NearestBody(carrier.Position);
        if (altitude > DeployMaxAltitudeM)
            return $"Too far from any body: come within {DeployMaxAltitudeM / 1000:0} km of {body.Name}'s surface (now {altitude / 1000:0.0} km).";
        var relative = (carrier.Velocity - GetVelocityAt(body.Id, TimeS)).Length;
        if (relative > DeployMaxRelativeSpeedMps)
            return $"Match speed with {body.Name} first ({relative:0} m/s; at most {DeployMaxRelativeSpeedMps:0}).";
        return null;
    }

    /// <summary>
    /// Unpacks a kit the craft holds into a new station beside it. Refused, with the reason, if it is
    /// docked, too far from a body, moving too fast, or (for foundations) not landed.
    /// </summary>
    public ActionResult TryDeployKit(CraftId craftId, OrderId kitId)
    {
        var carrier = GetCraft(craftId);
        var kit = carrier.KitsList.FirstOrDefault(k => k.Id == kitId);
        if (kit is null) return new(false, $"{carrier.Name} has no such kit.");
        if (DeployBlocker(carrier, kit) is { } reason) return new(false, reason);

        var station = new CraftState(AllocateCraftId(), kit.Name, kit.Design, kit.Stats, carrier.Position, carrier.HeadingRad);
        AddCraft(station);
        carrier.KitsList.Remove(kit);
        if (carrier.IsLanded) PlaceOnSurface(GetBody(carrier.LandedOnBodyId), carrier, station, TimeS);
        else if (carrier.IsOnRails) PlaceNextTo(carrier, station, TimeS);
        else PlaceInOrbitNear(carrier, station);
        return new(true, $"Deployed {station.Name} ({station.Id}).");
    }

    /// <summary>Puts a station on a circular rail orbit around the nearest body, at the carrier's distance, first free spot.</summary>
    private void PlaceInOrbitNear(CraftState carrier, CraftState station)
    {
        var (body, _) = NearestBody(carrier.Position);
        var offset = carrier.Position - GetPosition(body.Id);
        var radius = Math.Max(offset.Length, body.RadiusM + station.CollisionRadiusM + ParkingGapM);
        var phaseNow = Math.Atan2(offset.Y, offset.X);
        for (var slot = 1; slot <= 1000; slot++)
        {
            var distance = carrier.CollisionRadiusM + station.CollisionRadiusM + ParkingGapM
                           + (slot - 1) * (2 * station.CollisionRadiusM + ParkingGapM);
            var orbit = new CircularOrbit(body.Id, radius, DeployOrbitPeriodS,
                phaseNow - distance / radius - 2.0 * Math.PI * (TimeS / DeployOrbitPeriodS));
            if (!IsFree(RailPositionAt(orbit, TimeS), station)) continue;
            PutOnRails(station.Id, orbit);
            return;
        }
        throw new InvalidOperationException($"No free spot near {carrier.Id}."); // 1000 slots: not reachable in practice
    }

    /// <summary>Restores a saved kit exactly.</summary>
    public void RestoreKit(CraftId craftId, ConstructionKit kit)
    {
        if (OrderIdInUse(kit.Id)) throw new InvalidOperationException($"Duplicate {kit.Id}.");
        GetCraft(craftId).KitsList.Add(kit);
        if (kit.Id.Value >= _nextOrderId) _nextOrderId = kit.Id.Value + 1;
    }
}
