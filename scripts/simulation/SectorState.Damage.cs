using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Impact damage and craft-to-craft collisions (#34).
/// <list type="bullet">
/// <item>Each part has health 0..1. Landing faster than <see cref="SafeImpactSpeedMps"/> damages the
/// parts that touched down; so does a craft-to-craft bump faster than that. Damage falls off with
/// distance from the contact point.</item>
/// <item>A damaged part works worse (its output scales with health, see <see cref="ShipDesign.ComputeStats"/>).
/// At 0 it breaks off: it leaves the design and its materials are recorded as debris in
/// <see cref="EjectedKg"/>, so mass is conserved. The last part never breaks off.</item>
/// <item>Craft collide by their cells (discs of <see cref="CellContactRadiusM"/>), not their rough
/// bounding circles, so docking and parking side by side still work. Overlapping craft are pushed
/// apart and their closing speed along the contact is removed (an inelastic bump). Landed and
/// parked craft (stations) don't move; docked and cruising craft don't collide.</item>
/// <item>Automated craft follow traffic rules to avoid each other (#55, SectorState.Traffic).</item>
/// </list>
/// Shipyards repair damage (#3, SectorState.Service). Sectors without a part catalog (bare test sectors) take no damage.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Landings up to this speed are harmless.</summary>
    public const double SafeImpactSpeedMps = 6;

    /// <summary>
    /// Craft-to-craft bumps up to this closing speed are harmless (#55): hulls nudging each other in a
    /// crowd are not ships slamming into rock. Faster ones damage both.
    /// </summary>
    public const double SafeBumpSpeedMps = 12;

    /// <summary>Health lost per m/s of closing speed above the safe speed, at the contact point.</summary>
    public const double DamagePerMps = 0.08;

    /// <summary>Parts within this many metres of the contact point share the damage (less further away).</summary>
    public const double DamageRadiusM = 1.5 * ShipDesign.CellSizeM;

    /// <summary>One damaging impact, for diagnostics.</summary>
    public readonly record struct ImpactEvent(long Tick, CraftId Craft, string With, double SpeedMps);

    private readonly Queue<ImpactEvent> _impacts = new();

    /// <summary>The last damaging impacts (oldest first). In memory only.</summary>
    public IReadOnlyCollection<ImpactEvent> RecentImpacts => _impacts;

    /// <summary>Damages the parts of <paramref name="craft"/> near a contact point after an impact.</summary>
    private void ApplyImpact(CraftState craft, Vector2D contactPoint, double closingSpeedMps, string with = "", double safeSpeedMps = SafeImpactSpeedMps)
    {
        if (closingSpeedMps <= safeSpeedMps || Parts is null) return;
        if (_impacts.Count == 20) _impacts.Dequeue();
        _impacts.Enqueue(new ImpactEvent(Tick, craft.Id, with, closingSpeedMps));
        var damage = (closingSpeedMps - safeSpeedMps) * DamagePerMps;
        var changed = false;
        List<DesignCell>? broken = null;
        foreach (var (cell, position) in CellPositions(craft, craft.Position, craft.HeadingRad).ToList())
        {
            var distance = position.DistanceTo(contactPoint);
            if (distance > DamageRadiusM + CellContactRadiusM) continue;
            var share = 1 - Math.Max(0, distance - CellContactRadiusM) / (DamageRadiusM + CellContactRadiusM);
            var health = (craft.HealthList.TryGetValue(cell.Pos, out var h) ? h : 1) - damage * share;
            changed = true;
            if (health <= 0) (broken ??= new()).Add(cell);
            else craft.HealthList[cell.Pos] = health;
        }
        if (!changed) return;
        if (broken is not null) BreakOff(craft, broken);
        else craft.Stats = craft.Design.ComputeStats(Parts, craft.HealthList);
    }

    /// <summary>
    /// Removes broken parts from a craft; their materials become debris (ejected mass), or go into
    /// <paramref name="salvageInto"/> when they're cut off on purpose (#86).
    /// </summary>
    private void BreakOff(CraftState craft, List<DesignCell> broken, Inventory? salvageInto = null)
    {
        var parts = Parts!;
        // Never remove the last part: keep the one that was hit least (first in row order on ties).
        if (broken.Count >= craft.Design.Cells.Count)
        {
            var keep = broken.OrderBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).First();
            broken.Remove(keep);
            craft.HealthList[keep.Pos] = 0.01;
        }
        if (broken.Count == 0) { craft.Stats = craft.Design.ComputeStats(parts, craft.HealthList); return; }

        var lostPort = false;
        foreach (var cell in broken)
        {
            craft.HealthList.Remove(cell.Pos);
            var part = parts.Get(cell.PartId);
            lostPort |= part.IsDockingPort;
            foreach (var (material, kg) in part.Bill.ToWholeKg())
                if (salvageInto is not null) salvageInto.Add(material, kg);
                else _ejectedKg[material] = (_ejectedKg.TryGetValue(material, out var had) ? had : 0) + kg;
        }
        craft.PartsLost += broken.Count;
        craft.IntactDesign ??= craft.Design; // remembered so a shipyard can rebuild what broke off (#3)

        // The design keeps its id and name; the craft is now a damaged copy of it.
        var brokenSet = broken.Select(c => c.Pos).ToHashSet();
        var heading = craft.HeadingRad;
        var oldStats = craft.Stats;
        var design = new ShipDesign(craft.Design.Id, craft.Design.Name, craft.Design.Cells.Where(c => !brokenSet.Contains(c.Pos)));
        craft.Design = design;
        craft.Stats = design.ComputeStats(parts, craft.HealthList);
        // The centre of mass moved inside the craft: keep the remaining parts where they were in the world.
        var shift = new Vector2D((craft.Stats.CenterOfMassX - oldStats.CenterOfMassX) * ShipDesign.CellSizeM,
            (craft.Stats.CenterOfMassY - oldStats.CenterOfMassY) * ShipDesign.CellSizeM);
        var (sin, cos) = Math.SinCos(heading);
        craft.Position += new Vector2D(shift.X * cos - shift.Y * sin, shift.X * sin + shift.Y * cos);
        craft.StoredEnergyKJ = Math.Min(craft.StoredEnergyKJ, craft.Stats.EnergyCapacityKJ);

        // Ports are numbered by position in the design: if one broke off, release every docked ship.
        if (lostPort || craft.Stats.DockingPorts.Count != oldStats.DockingPorts.Count)
            foreach (var ship in _crafts.Where(s => s.DockedToCraftId == craft.Id).ToList())
                ship.DockedToCraftId = CraftId.None;
    }

    /// <summary>Restores saved part health (and broken-part count) without side effects.</summary>
    public void RestoreHealth(CraftId craftId, IReadOnlyDictionary<GridPos, double> health, int partsLost)
    {
        var craft = GetCraft(craftId);
        craft.HealthList.Clear();
        foreach (var (pos, h) in health)
        {
            if (!(h > 0 && h <= 1)) throw new ArgumentOutOfRangeException(nameof(health), $"Part health {h} at ({pos.X},{pos.Y}).");
            if (craft.Design.Cells.All(c => c.Pos != pos)) throw new ArgumentException($"No part at ({pos.X},{pos.Y}) to have health.");
            if (h < 1) craft.HealthList[pos] = h;
        }
        craft.PartsLost = Math.Max(0, partsLost);
        if (Parts is not null && craft.HealthList.Count > 0) craft.Stats = craft.Design.ComputeStats(Parts, craft.HealthList);
    }

    // --- Craft-to-craft collisions -------------------------------------------------------------

    private readonly Dictionary<long, int> _gridHead = new();
    private int[] _gridNext = Array.Empty<int>();

    private static bool Collides(CraftState c) => !c.IsDocked && !c.Cruising && c.Transit is null && !c.Dormant;
    private static bool Movable(CraftState c) => !c.IsLanded && !c.IsOnRails;

    /// <summary>
    /// Finds overlapping craft (a spatial grid, then the cells of close pairs), pushes them apart and
    /// removes their closing speed; fast bumps damage both. Deterministic: pairs in craft id order.
    /// Craft can't overlap, so even a crowd at a station has few craft per grid cell.
    /// </summary>
    private void ResolveCraftCollisions()
    {
        if (_crafts.Count < 2) return;
        double maxRadius = 0;
        foreach (var c in _crafts) if (Collides(c)) maxRadius = Math.Max(maxRadius, c.CollisionRadiusM);
        var cellSize = Math.Max(50, 2 * maxRadius + 1);

        if (_gridNext.Length < _crafts.Count) _gridNext = new int[Math.Max(_crafts.Count, 2 * _gridNext.Length)];
        _gridHead.Clear();
        for (var i = 0; i < _crafts.Count; i++)
        {
            var c = _crafts[i];
            if (!Collides(c)) continue;
            var key = GridKey((long)Math.Floor(c.Position.X / cellSize), (long)Math.Floor(c.Position.Y / cellSize));
            _gridNext[i] = _gridHead.TryGetValue(key, out var head) ? head : -1;
            _gridHead[key] = i;
        }

        for (var i = 0; i < _crafts.Count; i++)
        {
            var a = _crafts[i];
            if (!Collides(a)) continue;
            var cx = (long)Math.Floor(a.Position.X / cellSize);
            var cy = (long)Math.Floor(a.Position.Y / cellSize);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!_gridHead.TryGetValue(GridKey(cx + dx, cy + dy), out var j)) continue;
                for (; j >= 0; j = _gridNext[j])
                {
                    if (j <= i) continue; // each pair once, in id order
                    var b = _crafts[j];
                    if (!Movable(a) && !Movable(b)) continue;
                    if (a.Position.DistanceTo(b.Position) >= a.CollisionRadiusM + b.CollisionRadiusM) continue;
                    ResolvePair(a, b);
                }
            }
        }
    }

    private Vector2D[] _cellsA = new Vector2D[32], _cellsB = new Vector2D[32];

    /// <summary>World positions of a craft's cell centres into a reused buffer; returns how many.</summary>
    private static int FillCellPositions(CraftState craft, ref Vector2D[] buffer)
    {
        var cells = craft.Design.Cells;
        if (buffer.Length < cells.Count) buffer = new Vector2D[Math.Max(cells.Count, 2 * buffer.Length)];
        var (sin, cos) = Math.SinCos(craft.HeadingRad);
        var comX = craft.Stats.CenterOfMassX;
        var comY = craft.Stats.CenterOfMassY;
        for (var i = 0; i < cells.Count; i++)
        {
            var x = (cells[i].Pos.X - comX) * ShipDesign.CellSizeM;
            var y = (cells[i].Pos.Y - comY) * ShipDesign.CellSizeM;
            buffer[i] = craft.Position + new Vector2D(x * cos - y * sin, x * sin + y * cos);
        }
        return cells.Count;
    }

    private void ResolvePair(CraftState a, CraftState b)
    {
        // Deepest overlapping cell pair (cell positions into reused buffers: this runs for every close pair).
        var minDistance = 2 * CellContactRadiusM * 0.9; // cells touching face to face don't count (docked neighbours)
        var countA = FillCellPositions(a, ref _cellsA);
        var countB = FillCellPositions(b, ref _cellsB);
        var deepestSq = minDistance * minDistance;
        int bestA = -1, bestB = -1;
        for (var i = 0; i < countA; i++)
        {
            var ca = _cellsA[i];
            for (var j = 0; j < countB; j++)
            {
                var dx = ca.X - _cellsB[j].X;
                var dy = ca.Y - _cellsB[j].Y;
                var dSq = dx * dx + dy * dy;
                if (dSq < deepestSq) { deepestSq = dSq; bestA = i; bestB = j; }
            }
        }
        if (bestA < 0) return;
        var pa = _cellsA[bestA];
        var pb = _cellsB[bestB];
        var deepest = minDistance - Math.Sqrt(deepestSq);

        var between = pb - pa;
        var normal = between.Length > 1e-9 ? between * (1 / between.Length) : (b.Position - a.Position) * (1 / Math.Max((b.Position - a.Position).Length, 1e-9));
        var closing = -((b.Velocity.X - a.Velocity.X) * normal.X + (b.Velocity.Y - a.Velocity.Y) * normal.Y); // > 0: approaching

        // Share of the push and velocity change: by mass, all on the one that can move.
        double shareA, shareB;
        if (!Movable(a)) { shareA = 0; shareB = 1; }
        else if (!Movable(b)) { shareA = 1; shareB = 0; }
        else { shareA = b.MassKg / (a.MassKg + b.MassKg); shareB = 1 - shareA; }

        a.Position -= normal * (deepest * shareA);
        b.Position += normal * (deepest * shareB);
        if (closing > 0)
        {
            a.Velocity -= normal * (closing * shareA);
            b.Velocity += normal * (closing * shareB);
            var contact = (pa + pb) * 0.5;
            ApplyImpact(a, contact, closing, b.Name, SafeBumpSpeedMps);
            ApplyImpact(b, contact, closing, a.Name, SafeBumpSpeedMps);
        }
    }
}
