using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Craft targets and docking. A docking port on a station is one berth, just outside the port in
/// the direction it opens. A ship close to a free berth and matched to the station can dock (K).
/// It is then held at the berth, nose towards the port, and moves with the station until it
/// undocks (K, or its own throttle). Docking moves nothing between inventories by itself.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Max distance from a ship's centre to a free berth for docking. Placeholder.</summary>
    public const double DockRangeM = 100;

    /// <summary>Max speed relative to the station for docking. Placeholder.</summary>
    public const double DockMaxRelativeSpeedMps = 1.0;

    /// <summary>Clearance between a docked ship's collision circle and its port.</summary>
    public const double DockGapM = 2;

    /// <summary>Targets another craft: the brake matches its velocity and the HUD shows relative motion.</summary>
    public void SetTargetCraft(CraftId craftId, CraftId target)
    {
        var craft = GetCraft(craftId);
        if (!target.IsNone)
        {
            GetCraft(target);
            if (target == craftId) throw new ArgumentException("A craft can't target itself.", nameof(target));
            craft.TargetBodyId = BodyId.None;
        }
        craft.TargetCraftId = target;
    }

    /// <summary>Velocity of a craft at a time, exact for craft on rails or docked to one; otherwise its current velocity.</summary>
    public Vector2D CraftVelocityAt(CraftState craft, double timeS)
    {
        if (craft.RailOrbit is { } rail) return RailVelocityAt(rail, timeS);
        if (!craft.DockedToCraftId.IsNone) return CraftVelocityAt(GetCraft(craft.DockedToCraftId), timeS);
        if (craft.IsLanded) return GetVelocityAt(craft.LandedOnBodyId, timeS);
        return craft.Velocity;
    }

    /// <summary>Craft-to-craft relation: gap between collision circles, relative speed, closing speed.</summary>
    public RelativeMotion GetRelativeMotionToCraft(CraftId craftId, CraftId otherId)
    {
        var craft = GetCraft(craftId);
        var other = GetCraft(otherId);
        var offset = other.Position - craft.Position;
        var distance = offset.Length;
        var relative = craft.Velocity - other.Velocity;
        var closing = distance > 0 ? (relative.X * offset.X + relative.Y * offset.Y) / distance : 0;
        return new RelativeMotion(distance - craft.CollisionRadiusM - other.CollisionRadiusM, relative.Length, closing);
    }

    /// <summary>Indices of the station's docking ports not occupied by any craft.</summary>
    public IReadOnlyList<int> FreePorts(CraftState station) =>
        Enumerable.Range(0, station.Stats.DockingPorts.Count)
            .Where(i => !_crafts.Any(c => c.DockedToCraftId == station.Id && c.DockPortIndex == i))
            .ToList();

    /// <summary>World position where a ship of <paramref name="shipRadiusM"/> sits when docked at port <paramref name="port"/>.</summary>
    public Vector2D BerthPosition(CraftState station, int port, double shipRadiusM, Vector2D stationPosition)
    {
        var info = station.Stats.DockingPorts[port];
        var (fx, fy) = DesignCell.Facing(info.Rotation);
        var localX = (info.Pos.X - station.Stats.CenterOfMassX + fx * 0.5) * ShipDesign.CellSizeM + fx * (shipRadiusM + DockGapM);
        var localY = (info.Pos.Y - station.Stats.CenterOfMassY + fy * 0.5) * ShipDesign.CellSizeM + fy * (shipRadiusM + DockGapM);
        return stationPosition + Rotate(new Vector2D(localX, localY), station.HeadingRad);
    }

    /// <summary>Heading that points a docked ship's nose into the port (opposite to where the port opens).</summary>
    public static double DockedHeading(CraftState station, int port) =>
        FlightModel.WrapAngle(station.HeadingRad + (station.Stats.DockingPorts[port].Rotation + 2) * Math.PI / 2);

    private static Vector2D Rotate(Vector2D v, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new Vector2D(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    /// <summary>
    /// Docks the ship at the nearest free berth: of its target craft if that has ports, otherwise of
    /// any station in range. Refused, with the reason, if too far, too fast, landed or no berth is free.
    /// </summary>
    public ActionResult TryDock(CraftId shipId)
    {
        var ship = GetCraft(shipId);
        if (!ship.DockedToCraftId.IsNone) return new(false, $"{ship.Name} is already docked.");
        if (ship.IsLanded) return new(false, $"{ship.Name} is landed; lift off before docking.");
        if (ship.Stats.TotalThrustN <= 0) return new(false, $"{ship.Name} can't fly, so it can't dock.");

        var stations = ship.TargetCraftId.IsNone
            ? _crafts.Where(c => c.Id != ship.Id && c.Stats.DockingPorts.Count > 0 && c.DockedToCraftId.IsNone)
            : new[] { GetCraft(ship.TargetCraftId) }.Where(c => c.Stats.DockingPorts.Count > 0);

        (CraftState Station, int Port, double Distance)? best = null;
        foreach (var station in stations)
            foreach (var port in FreePorts(station))
            {
                var distance = BerthPosition(station, port, ship.CollisionRadiusM, station.Position).DistanceTo(ship.Position);
                if (best is null || distance < best.Value.Distance) best = (station, port, distance);
            }

        if (best is null)
            return new(false, ship.TargetCraftId.IsNone ? "No station with a free docking port nearby." : "Target has no free docking port.");
        var (target, berth, gap) = best.Value;
        if (gap > DockRangeM) return new(false, $"Too far from {target.Name}'s nearest free port: {gap:0} m (max {DockRangeM:0} m).");
        var relative = (ship.Velocity - target.Velocity).Length;
        if (relative > DockMaxRelativeSpeedMps)
            return new(false, $"Too fast relative to {target.Name}: {relative:0.0} m/s (max {DockMaxRelativeSpeedMps:0.0}). Target it and brake.");

        DockAt(ship, target, berth);
        return new(true, $"{ship.Name} docked at {target.Name}, port {berth + 1}.");
    }

    public ActionResult TryUndock(CraftId shipId)
    {
        var ship = GetCraft(shipId);
        if (ship.DockedToCraftId.IsNone) return new(false, $"{ship.Name} is not docked.");
        var station = GetCraft(ship.DockedToCraftId);
        ship.DockedToCraftId = CraftId.None;
        ship.Velocity = station.Velocity;
        return new(true, $"{ship.Name} undocked from {station.Name}.");
    }

    private void DockAt(CraftState ship, CraftState station, int port)
    {
        ship.RailOrbit = null;
        ship.DockedToCraftId = station.Id;
        ship.DockPortIndex = port;
        ship.Position = ship.PreviousPosition = BerthPosition(station, port, ship.CollisionRadiusM, station.Position);
        ship.Velocity = station.Velocity;
        ship.HeadingRad = ship.PreviousHeadingRad = DockedHeading(station, port);
    }

    /// <summary>Restores a saved dock without moving the ship (its saved position is already at the berth).</summary>
    public void RestoreDock(CraftId shipId, CraftId stationId, int port)
    {
        var ship = GetCraft(shipId);
        var station = GetCraft(stationId);
        if (stationId == shipId) throw new InvalidOperationException($"{shipId} can't dock to itself.");
        if (port < 0 || port >= station.Stats.DockingPorts.Count) throw new InvalidOperationException($"{stationId} has no port {port}.");
        if (_crafts.Any(c => c.DockedToCraftId == stationId && c.DockPortIndex == port))
            throw new InvalidOperationException($"{stationId} port {port} is occupied twice.");
        if (ship.IsLanded || ship.IsOnRails) throw new InvalidOperationException($"{shipId} can't be docked and also landed or on rails.");
        ship.DockedToCraftId = stationId;
        ship.DockPortIndex = port;
    }

    /// <summary>
    /// After everything has moved this tick: docked ships follow their berth, unless their own
    /// throttle pushed them off (then they leave with the station's velocity plus that push).
    /// </summary>
    private void UpdateDocked(List<(CraftState Ship, Vector2D OwnDeltaV)> docked)
    {
        foreach (var (ship, ownDeltaV) in docked)
        {
            var station = GetCraft(ship.DockedToCraftId);
            var berth = BerthPosition(station, ship.DockPortIndex, ship.CollisionRadiusM, station.Position);
            var controls = ship.Controls.Clamped();
            if (!controls.Brake && controls.Throttle > 0 && ownDeltaV.Length > 0)
            {
                ship.DockedToCraftId = CraftId.None;
                ship.Velocity = station.Velocity + ownDeltaV;
                ship.Position = berth + ownDeltaV * FixedStepClock.TickSeconds;
                continue;
            }
            ship.Position = berth;
            ship.Velocity = station.Velocity;
            ship.HeadingRad = DockedHeading(station, ship.DockPortIndex);
        }
    }
}
