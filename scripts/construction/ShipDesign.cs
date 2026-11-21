using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpaceAutomationSandbox.Construction;

/// <summary>Integer cell on a design grid. Forward is −Y.</summary>
public readonly record struct GridPos(int X, int Y);

/// <summary>One occupied cell of a design.</summary>
public readonly record struct DesignCell(GridPos Pos, string PartId);

/// <summary>
/// What a design is meant to be. All roles use the same grid and data; only validation differs.
/// Ship: flyable (cockpit and thrust). Station: hosts facilities (for now, at least one fabricator).
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
    int FoundationCount)
{
    /// <summary>Can lock onto a body once landed (clamps release again; foundations never do).</summary>
    public bool CanAnchor => AnchorClampCount > 0 || FoundationCount > 0;

    /// <summary>A foundation makes the structure fixed: anchored for good as soon as it lands.</summary>
    public bool IsFixedStructure => FoundationCount > 0;

    /// <summary>Full-thrust acceleration in m/s².</summary>
    public double MaxAccelerationMps2 => TotalMassKg > 0 ? TotalThrustN / TotalMassKg : 0;
}

/// <summary>
/// A square-grid layout of parts: the same data type for ships now, and for stations and
/// facilities later. Immutable. Structural problems (bad JSON, duplicate cells) throw on load;
/// gameplay problems are reported by <see cref="Validate"/> so the UI can show why.
/// </summary>
public sealed class ShipDesign
{
    public const int SchemaVersion = 1;

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
            list.Add(cell);
        }
        // Deterministic order (row, then column) for stats sums and drawing.
        Cells = list.OrderBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).ToList();
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<DesignCell> Cells { get; }

    /// <summary>Human-readable problems for the given role; empty means the design is usable in that role.</summary>
    public IReadOnlyList<string> Validate(PartCatalog catalog, DesignRole role = DesignRole.Ship)
    {
        var errors = new List<string>();
        if (Cells.Count == 0) { errors.Add("Design has no parts."); return errors; }

        var controlSources = 0;
        var foundations = 0;
        double thrust = 0, fabrication = 0;
        foreach (var cell in Cells)
        {
            if (!catalog.TryGet(cell.PartId, out var part))
            {
                errors.Add($"Unknown part '{cell.PartId}' at ({cell.Pos.X},{cell.Pos.Y}).");
                continue;
            }
            if (part.IsControlSource) controlSources++;
            thrust += part.ThrustN;
            fabrication += part.FabricationRateKgPerS;
            if (part.IsFoundation) foundations++;
        }

        switch (role)
        {
            case DesignRole.Ship:
                if (controlSources != 1) errors.Add($"Needs exactly one control source (cockpit); found {controlSources}.");
                if (thrust <= 0) errors.Add("Needs at least one thruster.");
                // A foundation would lock the ship down for good the first time it lands.
                if (foundations > 0) errors.Add("Foundations make a fixed structure; a ship can't have one (use an anchor clamp).");
                break;
            case DesignRole.Station:
                if (fabrication <= 0) errors.Add("A station needs at least one fabricator.");
                break;
        }
        return errors;
    }

    /// <summary>Totals and centre of mass (in cell units). Call only on a design that validates.</summary>
    public ShipStats ComputeStats(PartCatalog catalog)
    {
        double mass = 0, thrust = 0, cargo = 0, energy = 0, fabrication = 0, momentX = 0, momentY = 0;
        int clamps = 0, foundations = 0;
        foreach (var cell in Cells)
        {
            var part = catalog.Get(cell.PartId);
            mass += part.MassKg;
            thrust += part.ThrustN;
            cargo += part.CargoCapacityKg;
            energy += part.EnergyCapacityKJ;
            fabrication += part.FabricationRateKgPerS;
            if (part.IsAnchorClamp) clamps++;
            if (part.IsFoundation) foundations++;
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
            Math.Sqrt(radiusCellsSq) * CellSizeM, fabrication, clamps, foundations);
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
        if (file.SchemaVersion != SchemaVersion)
            throw new FormatException($"Design schemaVersion {file.SchemaVersion}, expected {SchemaVersion}.");
        var cells = (file.Cells ?? new List<CellDto>()).Select(c =>
            new DesignCell(new GridPos(c.X, c.Y), c.Part ?? throw new FormatException("Cell without a part.")));
        return new ShipDesign(file.Id ?? "", file.Name ?? file.Id ?? "", cells);
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Same format as data/blueprints (schema 1). Deterministic: cells in row, then column order.</summary>
    public string ToJson() => JsonSerializer.Serialize(new DesignFile
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        Name = Name,
        Cells = Cells.Select(c => new CellDto { X = c.Pos.X, Y = c.Pos.Y, Part = c.PartId }).ToList(),
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
    }
}
