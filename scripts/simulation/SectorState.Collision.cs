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
            var x = (cell.Pos.X - craft.Shape.CenterOfMassX) * ShipDesign.CellSizeM;
            var y = (cell.Pos.Y - craft.Shape.CenterOfMassY) * ShipDesign.CellSizeM;
            yield return (cell, position + new Vector2D(x * cos - y * sin, x * sin + y * cos));
        }
    }

    /// <summary>Craft up to this many cells are simply checked cell by cell.</summary>
    private const int SmallCraftCells = 64;

    /// <summary>For tests (#114): try every cell, as before the shortcuts. The results must be exactly the same.</summary>
    internal bool CheckEveryCell { get; set; }

    /// <summary>Where a craft's cells are, one at a time: the same sums as for all of them.</summary>
    private readonly struct CellPlace
    {
        private readonly DesignCell[] _cells;
        private readonly Vector2D _position;
        private readonly double _sin, _cos, _comX, _comY;

        public CellPlace(CraftState craft)
        {
            _cells = craft.Design.CellArray;
            _position = craft.Position;
            (_sin, _cos) = Math.SinCos(craft.HeadingRad);
            _comX = craft.Shape.CenterOfMassX;
            _comY = craft.Shape.CenterOfMassY;
        }

        public Vector2D Of(int i)
        {
            var x = (_cells[i].Pos.X - _comX) * ShipDesign.CellSizeM;
            var y = (_cells[i].Pos.Y - _comY) * ShipDesign.CellSizeM;
            return _position + new Vector2D(x * _cos - y * _sin, x * _sin + y * _cos);
        }

        /// <summary>A world position in the craft's own grid, in cells.</summary>
        public (double X, double Y) Local(Vector2D world)
        {
            var dx = world.X - _position.X;
            var dy = world.Y - _position.Y;
            return ((dx * _cos + dy * _sin) / ShipDesign.CellSizeM + _comX, (dy * _cos - dx * _sin) / ShipDesign.CellSizeM + _comY);
        }
    }

    private readonly SortedSet<int> _along = new();

    /// <summary>The cells a segment could touch, in order (some it doesn't): found through the grid, not by trying them all (#114).</summary>
    private SortedSet<int> CellsAlong(CraftState craft, Vector2D a, Vector2D b)
    {
        _along.Clear();
        var place = new CellPlace(craft);
        var (ax, ay) = place.Local(a);
        var (bx, by) = place.Local(b);
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) * 2)); // half a cell at a time
        for (var s = 0; s <= steps; s++)
        {
            var x = (int)Math.Round(ax + (bx - ax) * s / steps);
            var y = (int)Math.Round(ay + (by - ay) * s / steps);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (craft.Design.IndexOf(new GridPos(x + dx, y + dy)) is var at and >= 0) _along.Add(at);
        }
        return _along;
    }

    /// <summary>Gap between the craft's closest cell and the body's surface (negative = overlapping).</summary>
    public static double SurfaceGap(CraftState craft, Vector2D position, double headingRad, Vector2D bodyCentre, double bodyRadiusM)
    {
        var gap = double.PositiveInfinity;
        if (craft.Design.CellArray.Length > SmallCraftCells && position.DistanceTo(bodyCentre) > craft.CollisionRadiusM)
        {
            // From outside the craft the closest cell is one on its edge (#114).
            var (sin, cos) = Math.SinCos(headingRad);
            var cells = craft.Design.CellArray;
            foreach (var i in craft.Design.EdgeCells)
            {
                var x = (cells[i].Pos.X - craft.Shape.CenterOfMassX) * ShipDesign.CellSizeM;
                var y = (cells[i].Pos.Y - craft.Shape.CenterOfMassY) * ShipDesign.CellSizeM;
                var p = position + new Vector2D(x * cos - y * sin, x * sin + y * cos);
                gap = Math.Min(gap, p.DistanceTo(bodyCentre) - bodyRadiusM - CellContactRadiusM);
            }
            return gap;
        }
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
        var positions = AllBodyPositions(endS);
        for (var i = 0; i < _bodies.Count; i++) // id order: deterministic if two bodies overlap
        {
            var body = _bodies[i];
            var bodyPos = positions[i];
            if (craft.Position.DistanceTo(bodyPos) >= body.RadiusM + craft.CollisionRadiusM) continue; // can't touch
            if (SurfaceGap(craft, craft.Position, craft.HeadingRad, bodyPos, body.RadiusM) >= 0) continue;

            var settled = SettleOnSurface(craft, craft.Position, bodyPos, body.RadiusM);
            var bodyVelocity = GetVelocityAt(body.Id, endS);
            var normal = (settled - bodyPos) * (1 / Math.Max((settled - bodyPos).Length, 1e-9));
            var relative = craft.Velocity - bodyVelocity;
            var impact = -(relative.X * normal.X + relative.Y * normal.Y); // speed into the surface
            craft.LandedOnBodyId = body.Id;
            craft.LandedOffset = settled - bodyPos;
            craft.Position = settled;
            craft.Velocity = bodyVelocity;
            ApplyImpact(craft, bodyPos + normal * body.RadiusM, impact, body.Name); // hard landings break parts (#34)
            if (craft.IsLanded) craft.LandedOffset = craft.Position - bodyPos; // a part may have broken off
            OnLanded(craft);
            return;
        }
    }
}
