using System;
using System.Collections.Generic;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Shape-accurate contact with bodies (#43): a craft touches a body when any of its grid cells
/// (each a disc of <see cref="CellContactRadiusM"/>) reaches the surface, not when its whole bounding
/// circle does. Crafts therefore rest on their lowest parts instead of hovering. The bounding circle
/// is still used as a cheap first test and for craft-to-craft spacing (parking, docking).
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Contact radius of one cell: the disc inside its square.</summary>
    public const double CellContactRadiusM = ShipDesign.CellSizeM / 2;

    /// <summary>World positions of the craft's cell centres for a given position and heading.</summary>
    public static IEnumerable<(DesignCell Cell, Vector2D Position)> CellPositions(CraftState craft, Vector2D position, double headingRad)
    {
        var (sin, cos) = Math.SinCos(headingRad);
        foreach (var cell in craft.Design.Cells)
        {
            var x = (cell.Pos.X - craft.Stats.CenterOfMassX) * ShipDesign.CellSizeM;
            var y = (cell.Pos.Y - craft.Stats.CenterOfMassY) * ShipDesign.CellSizeM;
            yield return (cell, position + new Vector2D(x * cos - y * sin, x * sin + y * cos));
        }
    }

    /// <summary>Gap between the craft's closest cell and the body's surface (negative = overlapping).</summary>
    public static double SurfaceGap(CraftState craft, Vector2D position, double headingRad, Vector2D bodyCentre, double bodyRadiusM)
    {
        var gap = double.PositiveInfinity;
        foreach (var (_, p) in CellPositions(craft, position, headingRad))
            gap = Math.Min(gap, p.DistanceTo(bodyCentre) - bodyRadiusM - CellContactRadiusM);
        return gap;
    }

    /// <summary>Gap between the craft's closest cell and the body's surface right now.</summary>
    public double SurfaceGap(CraftState craft, CelestialBodyState body) =>
        SurfaceGap(craft, craft.Position, craft.HeadingRad, GetPosition(body.Id), body.RadiusM);

    /// <summary>
    /// Moves the craft along the body's normal until its closest cell just touches the surface
    /// (out if overlapping, in if hovering). A few iterations, because cells off the normal move
    /// less than the centre does.
    /// </summary>
    private static Vector2D SettleOnSurface(CraftState craft, Vector2D position, Vector2D bodyCentre, double bodyRadiusM)
    {
        for (var i = 0; i < 4; i++)
        {
            var gap = SurfaceGap(craft, position, craft.HeadingRad, bodyCentre, bodyRadiusM);
            if (Math.Abs(gap) < 1e-6) break;
            var outward = position - bodyCentre;
            var length = outward.Length;
            var normal = length > 1e-9 ? outward * (1 / length) : new Vector2D(0, -1);
            position -= normal * gap;
        }
        return position;
    }

    private void ResolveContact(CraftState craft, double endS)
    {
        foreach (var body in _bodies) // id order: deterministic if two bodies overlap
        {
            var bodyPos = GetPositionAt(body.Id, endS);
            if (craft.Position.DistanceTo(bodyPos) >= body.RadiusM + craft.CollisionRadiusM) continue; // can't touch
            if (SurfaceGap(craft, craft.Position, craft.HeadingRad, bodyPos, body.RadiusM) >= 0) continue;

            var settled = SettleOnSurface(craft, craft.Position, bodyPos, body.RadiusM);
            craft.LandedOnBodyId = body.Id;
            craft.LandedOffset = settled - bodyPos;
            craft.Position = settled;
            craft.Velocity = GetVelocityAt(body.Id, endS);
            OnLanded(craft);
            return;
        }
    }
}
