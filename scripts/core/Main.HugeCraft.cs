using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Drawing huge craft (#114): a craft of many parts is kept as one mesh and drawn in one call, instead of sending every cell every
/// frame. The mesh is made again when the craft's parts or their damage change, at most a few times a second.
/// </summary>
public partial class Main
{
    private const int MeshCells = 200;
    private const ulong MeshRebuildMs = 250, MeshForgetMs = 10_000;

    private sealed class CraftMesh
    {
        public ArrayMesh? Sprites, Colours;
        public int Version;
        public ShipDesign? Design;
        public ulong BuiltMs, UsedMs;

        /// <summary>Cells with an engine that pushes forward, for their flames.</summary>
        public int[] Engines = Array.Empty<int>();
    }

    private readonly Dictionary<CraftId, CraftMesh> _craftMeshes = new();
    private SectorState? _craftMeshesOf;
    private ulong _meshSweepMs;

    private static bool IsFlameEngine(DesignCell cell) =>
        cell.Rotation == 0 && (cell.PartId.StartsWith("thruster", StringComparison.Ordinal) || cell.PartId is "rocket" or "ion_drive" or "methalox_rocket" or "nuclear_rocket" or "antimatter_drive");

    /// <summary>A big craft keeps its shape at any zoom where it's more than a speck; below that it's a marker like the rest.</summary>
    private bool DrawBig(CraftState craft, Vector2 centre, float heading, float cellPx) =>
        craft.Stats.CellCount > MeshCells && craft.CollisionRadiusM * _pixelsPerMetre > 8 && !NoMeshes && DrawCraftMesh(craft, centre, heading, cellPx);

    /// <summary>Dev/test hook <c>--no-meshes</c>: cell by cell, to compare.</summary>
    private static readonly bool NoMeshes = OS.GetCmdlineUserArgs().Contains("--no-meshes");

    /// <summary>Draws a big craft from its mesh; false if it should be drawn cell by cell.</summary>
    private bool DrawCraftMesh(CraftState craft, Vector2 centre, float heading, float cellPx)
    {
        if (craft.Stats.CellCount <= MeshCells) return false;
        if (!ReferenceEquals(_craftMeshesOf, _sector)) { _craftMeshes.Clear(); _craftMeshesOf = _sector; }
        var now = Time.GetTicksMsec();
        if (!_craftMeshes.TryGetValue(craft.Id, out var mesh)) _craftMeshes[craft.Id] = mesh = new CraftMesh();
        mesh.UsedMs = now;
        if (mesh.Sprites is null && mesh.Colours is null || ((mesh.Version != craft.LookVersion || !ReferenceEquals(mesh.Design, craft.Design)) && now - mesh.BuiltMs > MeshRebuildMs))
            BuildCraftMesh(craft, mesh, now);
        // The mesh is in cells round its design's (0, 0): the centre of mass is only known to the craft.
        var transform = new Transform2D(heading, centre).ScaledLocal(new Vector2(cellPx, cellPx)).TranslatedLocal(new Vector2((float)-craft.Stats.CenterOfMassX, (float)-craft.Stats.CenterOfMassY));
        if (cellPx >= 3f && mesh.Sprites is not null) DrawMesh(mesh.Sprites, PartSprites.Atlas, transform);
        else if (mesh.Colours is not null) DrawMesh(mesh.Colours, null, transform);

        if (craft.Controls.Throttle > 0 && !craft.Controls.Brake && cellPx >= 1.5f)
            foreach (var i in mesh.Engines)
            {
                if (i >= craft.Design.Cells.Count) break;
                var cell = craft.Design.Cells[i];
                if (craft.DryDrives.Contains(cell.PartId)) continue;
                var length = cell.PartId is "rocket" or "methalox_rocket" ? 1.6f : cell.PartId == "antimatter_drive" ? 2.4f : cell.PartId is "ion_drive" or "nuclear_rocket" ? 1.2f : 0.9f;
                var at = new Vector2(cell.Pos.X, cell.Pos.Y + 0.5f);
                BatchTriangle(transform * (at + new Vector2(-0.3f, 0)), transform * (at + new Vector2(0.3f, 0)), transform * (at + new Vector2(0, length)), cell.PartId == "ion_drive" ? IonGlowColor : FlameColor);
            }
        if (now > _meshSweepMs)
        {
            _meshSweepMs = now + MeshForgetMs;
            foreach (var id in _craftMeshes.Where(m => now - m.Value.UsedMs > MeshForgetMs).Select(m => m.Key).ToList()) _craftMeshes.Remove(id);
        }
        return true;
    }

    private void BuildCraftMesh(CraftState craft, CraftMesh mesh, ulong now)
    {
        var cells = craft.Design.Cells;
        var (points, uvs, tints, colours) = (new Vector2[cells.Count * 4], new Vector2[cells.Count * 4], new Color[cells.Count * 4], new Color[cells.Count * 4]);
        var indices = new int[cells.Count * 6];
        var engines = new List<int>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var at = new Vector2(cell.Pos.X - 0.5f, cell.Pos.Y - 0.5f);
            var v = i * 4;
            points[v] = at;
            points[v + 1] = at + new Vector2(1, 0);
            points[v + 2] = at + new Vector2(1, 1);
            points[v + 3] = at + new Vector2(0, 1);
            // As cell by cell: damaged parts darken towards black-red (#34).
            var damaged = craft.PartHealth.TryGetValue(cell.Pos, out var health) ? (float)(1 - health) * 0.8f : 0f;
            var colour = _partColors.GetValueOrDefault(cell.PartId, Colors.Magenta).Lerp(new Color(0.25f, 0.05f, 0.05f), damaged);
            var tint = Colors.White.Lerp(new Color(0.35f, 0.12f, 0.12f), damaged);
            if (PartSprites.TryCorners(cell.PartId, cell.Rotation, out var a, out var b, out var c, out var d)) (uvs[v], uvs[v + 1], uvs[v + 2], uvs[v + 3]) = (a, b, c, d);
            for (var k = 0; k < 4; k++) (tints[v + k], colours[v + k]) = (tint, colour);
            (indices[i * 6], indices[i * 6 + 1], indices[i * 6 + 2], indices[i * 6 + 3], indices[i * 6 + 4], indices[i * 6 + 5]) = (v, v + 1, v + 2, v, v + 2, v + 3);
            if (IsFlameEngine(cell)) engines.Add(i);
        }
        ArrayMesh Make(Color[] shades)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = points;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Color] = shades;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            var made = new ArrayMesh();
            made.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: Mesh.ArrayFormat.FlagUse2DVertices);
            return made;
        }
        mesh.Sprites = PartSprites.Atlas is null ? null : Make(tints);
        mesh.Colours = Make(colours);
        mesh.Engines = engines.ToArray();
        mesh.Design = craft.Design;
        mesh.Version = craft.LookVersion;
        mesh.BuiltMs = now;
    }

    /// <summary>Dev/test hook: <c>--huge=2000x3</c> adds three ships of about 2,000 parts beside the player (cheats; nothing is saved).</summary>
    private void DemoHuge(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith("--huge=")) is not { } arg || arg["--huge=".Length..].Split('x') is not [var partsText, var countText]
            || !int.TryParse(partsText, out var parts) || !int.TryParse(countText, out var count) || _catalog is null || _player is not { } p) return;
        var width = Math.Max(3, (int)Math.Round(Math.Sqrt(parts / 1.6)));
        var design = new ShipDesign($"huge_{parts}", $"Huge {parts}", Enumerable.Range(0, parts).Select(i =>
        {
            var (x, y) = (i % width, i / width);
            var last = i >= parts - width;
            var id = i == width / 2 ? "cockpit" : last && x % 2 == 0 ? "thruster_mk3" : last ? "hull"
                : (x + 3 * y) % 11 == 0 ? "battery_mk2" : (x + 3 * y) % 7 == 0 ? "solar_panel_mk3" : (x + 2 * y) % 5 == 0 ? "cargo" : i % 97 == 0 ? "docking_port" : "hull";
            return new DesignCell(new GridPos(x - width / 2, y), id);
        }));
        var stats = design.ComputeStats(_catalog);
        // Out from the planet, clear of it and of each other.
        var (home, _) = _sector.NearestBody(p.Position);
        var out_ = p.Position - _sector.GetPosition(home.Id);
        out_ *= 1 / Math.Max(out_.Length, 1);
        for (var i = 0; i < count; i++)
            _sector.AddCraft(new CraftState(_sector.AllocateCraftId(), $"Huge {i + 1}", design, stats,
                p.Position + out_ * (400 + stats.BoundingRadiusM * (1.2 + 2.2 * i)), 0.4 + i) { Velocity = p.Velocity, LegacyNoCrew = true });
    }
}
