using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Construction;

/// <summary>The shapes and turns of the large-ship tools (#112), as plain lists of cells: no view, no editing state.</summary>
public static class BlueprintTools
{
    /// <summary>A straight line, every cell touching the next by an edge or a corner.</summary>
    public static IReadOnlyList<GridPos> Line(GridPos a, GridPos b)
    {
        var steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        var cells = new List<GridPos>(steps + 1);
        for (var i = 0; i <= steps; i++)
            cells.Add(steps == 0 ? a : new GridPos(a.X + (int)Math.Round((b.X - a.X) * (double)i / steps, MidpointRounding.AwayFromZero), a.Y + (int)Math.Round((b.Y - a.Y) * (double)i / steps, MidpointRounding.AwayFromZero)));
        return cells;
    }

    /// <summary>A filled rectangle between two corners.</summary>
    public static IReadOnlyList<GridPos> Rectangle(GridPos a, GridPos b)
    {
        var cells = new List<GridPos>();
        for (var y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
            for (var x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++) cells.Add(new GridPos(x, y));
        return cells;
    }

    /// <summary>Only the rectangle's edge.</summary>
    public static IReadOnlyList<GridPos> Outline(GridPos a, GridPos b) =>
        Rectangle(a, b).Where(p => p.X == a.X || p.X == b.X || p.Y == a.Y || p.Y == b.Y).ToList();

    /// <summary>A square brush, 1 to 5 across.</summary>
    public static IReadOnlyList<GridPos> Brush(GridPos centre, int size)
    {
        var half = Math.Max(0, size / 2);
        return Rectangle(new GridPos(centre.X - half, centre.Y - half), new GridPos(centre.X + half, centre.Y + half));
    }

    /// <summary>
    /// The region joined edge to edge with a cell and holding the same kind of part (or nothing). Empty space that reaches the edge of
    /// the grid isn't a region to fill: null.
    /// </summary>
    public static IReadOnlyList<GridPos>? Region(IReadOnlyDictionary<GridPos, DesignCell> cells, GridPos start, int extent)
    {
        string? Kind(GridPos p) => cells.TryGetValue(p, out var c) ? c.PartId : null;
        if (Math.Abs(start.X) > extent || Math.Abs(start.Y) > extent) return null;
        var kind = Kind(start);
        var seen = new HashSet<GridPos> { start };
        var found = new List<GridPos> { start };
        for (var i = 0; i < found.Count; i++)
        {
            var p = found[i];
            if (kind is null && (Math.Abs(p.X) == extent || Math.Abs(p.Y) == extent)) return null;
            foreach (var n in new[] { new GridPos(p.X + 1, p.Y), new GridPos(p.X - 1, p.Y), new GridPos(p.X, p.Y + 1), new GridPos(p.X, p.Y - 1) })
                if (Math.Abs(n.X) <= extent && Math.Abs(n.Y) <= extent && Kind(n) == kind && seen.Add(n)) found.Add(n);
        }
        return found;
    }

    /// <summary>A facing seen in a mirror: sideways facings swap left to right, the others front to back.</summary>
    public static int MirrorFacing(int rotation, bool leftRight) => leftRight ? rotation switch { 1 => 3, 3 => 1, _ => rotation } : rotation switch { 0 => 2, 2 => 0, _ => rotation };

    /// <summary>A cell and its mirror images across the grid's middle, without repeats.</summary>
    public static IReadOnlyList<DesignCell> Mirrored(DesignCell cell, bool leftRight, bool frontBack)
    {
        var all = new List<DesignCell> { cell };
        if (leftRight) all.AddRange(all.Select(c => new DesignCell(new GridPos(-c.Pos.X, c.Pos.Y), c.PartId, MirrorFacing(c.Rotation, true))).ToList());
        if (frontBack) all.AddRange(all.Select(c => new DesignCell(new GridPos(c.Pos.X, -c.Pos.Y), c.PartId, MirrorFacing(c.Rotation, false))).ToList());
        // On the middle line a part stays as drawn.
        return all.GroupBy(c => c.Pos).Select(g => g.First()).ToList();
    }

    /// <summary>Cells moved so their top left corner is (0, 0).</summary>
    public static IReadOnlyList<DesignCell> ToCorner(IEnumerable<DesignCell> cells)
    {
        var list = cells.ToList();
        if (list.Count == 0) return list;
        var (x, y) = (list.Min(c => c.Pos.X), list.Min(c => c.Pos.Y));
        return list.Select(c => c with { Pos = new GridPos(c.Pos.X - x, c.Pos.Y - y) }).OrderBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).ToList();
    }

    /// <summary>
    /// A stamp (its corner at (0, 0)) put down at a cell and side by side towards another, along whichever way it's further (#113).
    /// </summary>
    public static IReadOnlyList<DesignCell> Repeated(IReadOnlyList<DesignCell> stamp, GridPos from, GridPos to)
    {
        if (stamp.Count == 0) return stamp;
        var (width, height) = (stamp.Max(c => c.Pos.X) + 1, stamp.Max(c => c.Pos.Y) + 1);
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        var across = Math.Abs(dx) >= Math.Abs(dy);
        var (size, along) = across ? (width, dx) : (height, dy);
        var cells = new List<DesignCell>();
        for (var i = 0; i <= Math.Abs(along) / size; i++)
        {
            var step = i * size * Math.Sign(along);
            foreach (var c in stamp) cells.Add(c with { Pos = new GridPos(from.X + c.Pos.X + (across ? step : 0), from.Y + c.Pos.Y + (across ? 0 : step)) });
        }
        return cells;
    }

    /// <summary>A quarter turn clockwise, parts and all, with the corner at (0, 0).</summary>
    public static IReadOnlyList<DesignCell> Turned(IEnumerable<DesignCell> cells) =>
        ToCorner(cells.Select(c => new DesignCell(new GridPos(-c.Pos.Y, c.Pos.X), c.PartId, (c.Rotation + 1) & 3)));

    /// <summary>The mirror image, with the corner at (0, 0).</summary>
    public static IReadOnlyList<DesignCell> Flipped(IEnumerable<DesignCell> cells, bool leftRight) =>
        ToCorner(cells.Select(c => new DesignCell(leftRight ? new GridPos(-c.Pos.X, c.Pos.Y) : new GridPos(c.Pos.X, -c.Pos.Y), c.PartId, MirrorFacing(c.Rotation, leftRight))));
}
