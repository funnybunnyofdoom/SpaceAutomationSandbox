using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceAutomationSandbox.Construction;

/// <summary>Integer cell on a design grid. Forward is −Y.</summary>
public readonly record struct GridPos(int X, int Y);

/// <summary>
/// One occupied cell of a design. <paramref name="Rotation"/> is quarter turns clockwise (0–3).
/// A part's facing at rotation 0 is the design's forward (−Y): a thruster at rotation 0 pushes the
/// craft forward, and a docking port at rotation 0 opens forward.
/// </summary>
public readonly record struct DesignCell(GridPos Pos, string PartId, int Rotation = 0)
{
    /// <summary>Unit direction (design grid axes) a part with <paramref name="rotation"/> faces.</summary>
    public static (int X, int Y) Facing(int rotation) => (rotation & 3) switch
    {
        0 => (0, -1),
        1 => (1, 0),
        2 => (0, 1),
        _ => (-1, 0),
    };
}

/// <summary>A docking port: one berth. Position is the port cell; facing is where it opens.</summary>
public readonly record struct DockingPortInfo(GridPos Pos, int Rotation);

/// <summary>
/// What a design is meant to be. All roles use the same grid and data; only validation differs.
/// Ship: flyable (one cockpit, forward thrust, no foundation). Station: hosts facilities
/// (a fabricator, docking port or cargo bay); may have a foundation (fixed structure).
/// </summary>
public enum DesignRole
{
    Ship,
    Station,
}

/// <summary>Derived totals for a design. Recomputed from the parts, never stored separately.</summary>
public sealed record ShipStats(
    int CellCount,
    double TotalMassKg,
    double TotalThrustN,
    double CargoCapacityKg,
    double EnergyCapacityKJ,
    double CenterOfMassX,
    double CenterOfMassY,
    double BoundingRadiusM,
    double FabricationRateKgPerS,
    int AnchorClampCount,
    int FoundationCount,
    double NonForwardThrustN,
    IReadOnlyList<DockingPortInfo> DockingPorts,
    double MiningRateKgPerS,
    double RefiningRateKgPerS)
{
    /// <summary>Can lock onto a body once landed (clamps release again; foundations never do).</summary>
    public bool CanAnchor => AnchorClampCount > 0 || FoundationCount > 0;

    /// <summary>A foundation makes the structure fixed: anchored for good as soon as it lands.</summary>
    public bool IsFixedStructure => FoundationCount > 0;

    /// <summary>
    /// Full-thrust acceleration of the empty craft in m/s². <see cref="TotalThrustN"/> counts only
    /// rear-facing thrusters (rotation 0); others are in <see cref="NonForwardThrustN"/> and unused for now.
    /// </summary>
    public double MaxAccelerationMps2 => TotalMassKg > 0 ? TotalThrustN / TotalMassKg : 0;
}

/// <summary>
/// A square-grid layout of parts: the same data type for ships, stations and fixed structures.
/// Immutable. Structural problems (bad JSON, duplicate cells, bad rotation) throw on load;
/// gameplay problems are reported by <see cref="Validate"/> so the UI can show why.
/// </summary>
public sealed class ShipDesign
{
    /// <summary>v2 added the optional per-cell <c>rot</c>; v1 files (no rotation) still load.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Edge length of one grid cell in metres. Placeholder scale.</summary>
    public const double CellSizeM = 4.0;

    public ShipDesign(string id, string name, IEnumerable<DesignCell> cells)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new FormatException("Design needs an id.");
        Id = id;
        Name = name;

        var seen = new HashSet<GridPos>();
        var list = new List<DesignCell>();
        foreach (var cell in cells)
        {
            if (!seen.Add(cell.Pos)) throw new FormatException($"Design '{id}' has two parts at ({cell.Pos.X},{cell.Pos.Y}).");
            if (cell.Rotation is < 0 or > 3) throw new FormatException($"Design '{id}': rotation {cell.Rotation} at ({cell.Pos.X},{cell.Pos.Y}) is not 0–3.");
            list.Add(cell);
        }
        // Deterministic order (row, then column) for stats sums and drawing.
        Cells = list.OrderBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).ToList();
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<DesignCell> Cells { get; }

    /// <summary>Ship if it has a cockpit or any thruster (or nothing that makes a station); otherwise Station.</summary>
    public DesignRole SuggestedRole(PartCatalog catalog)
    {
        var parts = Cells.Select(c => catalog.TryGet(c.PartId, out var p) ? p : null).Where(p => p != null).ToList();
        if (parts.Any(p => p!.IsControlSource || p.ThrustN > 0)) return DesignRole.Ship;
        return parts.Any(p => IsStationFacility(p!)) ? DesignRole.Station : DesignRole.Ship;
    }

    private static bool IsStationFacility(PartDefinition p) =>
        p.FabricationRateKgPerS > 0 || p.IsDockingPort || p.CargoCapacityKg > 0 || p.RefiningRateKgPerS > 0;

    /// <summary>Human-readable problems for the given role; empty means the design is usable in that role.</summary>
    public IReadOnlyList<string> Validate(PartCatalog catalog, DesignRole role = DesignRole.Ship)
    {
        var errors = new List<string>();
        if (Cells.Count == 0) { errors.Add("Design has no parts."); return errors; }

        int controlSources = 0, foundations = 0, facilities = 0;
        double forwardThrust = 0;
        foreach (var cell in Cells)
        {
            if (!catalog.TryGet(cell.PartId, out var part))
            {
                errors.Add($"Unknown part '{cell.PartId}' at ({cell.Pos.X},{cell.Pos.Y}).");
                continue;
            }
            if (part.IsControlSource) controlSources++;
            if (cell.Rotation == 0) forwardThrust += part.ThrustN;
            if (part.IsFoundation) foundations++;
            if (IsStationFacility(part)) facilities++;
        }

        switch (role)
        {
            case DesignRole.Ship:
                if (controlSources != 1) errors.Add($"Needs exactly one control source (cockpit); found {controlSources}.");
                if (forwardThrust <= 0) errors.Add("Needs at least one rear-facing thruster (rotation 0) to push it forward.");
                // A foundation would lock the ship down for good the first time it lands.
                if (foundations > 0) errors.Add("Foundations make a fixed structure; a ship can't have one (use an anchor clamp).");
                break;
            case DesignRole.Station:
                if (facilities == 0) errors.Add("A station needs a fabricator, docking port or cargo bay (or a refinery).");
                break;
        }
        return errors;
    }

    /// <summary>Advice that doesn't block use, e.g. thrusters that give no forward thrust yet.</summary>
    public IReadOnlyList<string> Warnings(PartCatalog catalog)
    {
        var sideways = Cells.Count(c => c.Rotation != 0 && catalog.TryGet(c.PartId, out var p) && p.ThrustN > 0);
        return sideways > 0
            ? new[] { $"{sideways} thruster(s) don't face backward; they give no forward thrust (only rear-facing thrusters fly the ship for now)." }
            : Array.Empty<string>();
    }

    /// <summary>Totals and centre of mass (in cell units). Call only on a design whose parts are all known.</summary>
    public ShipStats ComputeStats(PartCatalog catalog)
    {
        double mass = 0, thrust = 0, otherThrust = 0, cargo = 0, energy = 0, fabrication = 0, mining = 0, refining = 0, momentX = 0, momentY = 0;
        int clamps = 0, foundations = 0;
        var ports = new List<DockingPortInfo>();
        foreach (var cell in Cells)
        {
            var part = catalog.Get(cell.PartId);
            mass += part.MassKg;
            if (cell.Rotation == 0) thrust += part.ThrustN;
            else otherThrust += part.ThrustN;
            cargo += part.CargoCapacityKg;
            energy += part.EnergyCapacityKJ;
            fabrication += part.FabricationRateKgPerS;
            mining += part.MiningRateKgPerS;
            refining += part.RefiningRateKgPerS;
            if (part.IsAnchorClamp) clamps++;
            if (part.IsFoundation) foundations++;
            if (part.IsDockingPort) ports.Add(new DockingPortInfo(cell.Pos, cell.Rotation));
            momentX += part.MassKg * cell.Pos.X;
            momentY += part.MassKg * cell.Pos.Y;
        }
        var comX = mass > 0 ? momentX / mass : 0;
        var comY = mass > 0 ? momentY / mass : 0;

        // Smallest circle around the centre of mass that contains every cell square (its corners).
        // Used as the collision shape for now.
        double radiusCellsSq = 0;
        foreach (var cell in Cells)
        {
            var dx = Math.Abs(cell.Pos.X - comX) + 0.5;
            var dy = Math.Abs(cell.Pos.Y - comY) + 0.5;
            radiusCellsSq = Math.Max(radiusCellsSq, dx * dx + dy * dy);
        }

        return new ShipStats(Cells.Count, mass, thrust, cargo, energy, comX, comY,
            Math.Sqrt(radiusCellsSq) * CellSizeM, fabrication, clamps, foundations, otherThrust, ports, mining, refining);
    }

    /// <summary>Total materials to build every part of the design (unknown parts are skipped).</summary>
    public MaterialBill ComputeBillOfMaterials(PartCatalog catalog)
    {
        var total = MaterialBill.Empty;
        foreach (var cell in Cells)
            if (catalog.TryGet(cell.PartId, out var part)) total = total.Plus(part.Bill);
        return total;
    }

    public static ShipDesign FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<DesignFile>(json, PartCatalog.JsonOptions)
                   ?? throw new FormatException("Design file is empty.");
        if (file.SchemaVersion is < 1 or > SchemaVersion)
            throw new FormatException($"Design schemaVersion {file.SchemaVersion}; this build reads 1 to {SchemaVersion}.");
        var cells = (file.Cells ?? new List<CellDto>()).Select(c =>
            new DesignCell(new GridPos(c.X, c.Y), c.Part ?? throw new FormatException("Cell without a part."), c.Rot));
        return new ShipDesign(file.Id ?? "", file.Name ?? file.Id ?? "", cells);
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Same format as data/blueprints. Deterministic: cells in row, then column order; rot omitted when 0.</summary>
    public string ToJson() => JsonSerializer.Serialize(new DesignFile
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        Name = Name,
        Cells = Cells.Select(c => new CellDto { X = c.Pos.X, Y = c.Pos.Y, Part = c.PartId, Rot = c.Rotation }).ToList(),
    }, WriteOptions);

    private sealed class DesignFile
    {
        public int SchemaVersion { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<CellDto>? Cells { get; set; }
    }

    private sealed class CellDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string? Part { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Rot { get; set; }
    }
}
