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

/// <summary>All converters of one part type on a craft (#78): their total power use (kW) and one batch, whole kg in and out (same mass).</summary>
public sealed record ConverterStats(string PartId, double KW, IReadOnlyDictionary<string, long> InputsKg,
    IReadOnlyDictionary<string, long> OutputsKg, double KJPerBatch)
{
    public static ConverterStats From(PartDefinition part, double kw) => new(part.Id, kw,
        part.ConverterInputsKg!.ToDictionary(kv => kv.Key, kv => (long)kv.Value),
        part.ConverterOutputsKg!.ToDictionary(kv => kv.Key, kv => (long)kv.Value), part.ConverterKJPerBatch);
}

/// <summary>
/// All generators of one part type on a craft (#75): their total power (kW, health included), and the batch they
/// burn: whole kg in, whole kg left in stock, kJ out. The rest of a batch's mass leaves as exhaust (ejected mass).
/// </summary>
public sealed record GeneratorStats(string PartId, double KW, IReadOnlyDictionary<string, long> BurnsKg,
    IReadOnlyDictionary<string, long> MakesKg, double KJPerBatch)
{
    public static GeneratorStats From(PartDefinition part, double kw) => new(part.Id, kw,
        (part.GeneratorBurnsKg ?? new Dictionary<string, double>()).ToDictionary(kv => kv.Key, kv => (long)kv.Value),
        (part.GeneratorMakesKg ?? new Dictionary<string, double>()).ToDictionary(kv => kv.Key, kv => (long)kv.Value),
        part.GeneratorKJPerBatch);

    /// <summary>Kg of exhaust per batch: what's burned and not left behind.</summary>
    public long ExhaustKg => BurnsKg.Values.Sum() - MakesKg.Values.Sum();
}

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
    double RefiningRateKgPerS,
    double PowerGenerationKW = 0,
    double ThrustPowerKW = 0,
    double DrillPowerKW = 0,
    double RefineryPowerKW = 0,
    double FabricatorPowerKW = 0,
    double EjectorPowerKW = 0,
    double EjectionRateKgPerS = 0,
    double ReverseThrustN = 0,
    double LeftThrustN = 0,
    double RightThrustN = 0,
    double ReversePowerKW = 0,
    double LeftPowerKW = 0,
    double RightPowerKW = 0,
    int CrewCapacity = 0,
    double FoodRateKgPerS = 0,
    double HabitatPowerKW = 0,
    double FarmPowerKW = 0,
    int RobotCount = 0,
    double RobotPowerKW = 0,
    int WarpGateCount = 0,
    double ResearchRatePerS = 0,
    double LabPowerKW = 0,
    int GateDefenseCount = 0,
    IReadOnlyList<GeneratorStats>? Generators = null,
    IReadOnlyList<ConverterStats>? Converters = null,
    double GeothermalKW = 0)
{
    /// <summary>Converters by part type (#78); empty if none.</summary>
    public IReadOnlyList<ConverterStats> ConverterList => Converters ?? Array.Empty<ConverterStats>();

    /// <summary>Generators by part type (#75); empty if none.</summary>
    public IReadOnlyList<GeneratorStats> GeneratorList => Generators ?? Array.Empty<GeneratorStats>();

    /// <summary>Most power all generators give together (kW), health included.</summary>
    public double GeneratorKW => GeneratorList.Sum(g => g.KW);

    /// <summary>Craft up to this many parts run without crew (#11).</summary>
    public const int UncrewedMaxParts = 15;

    /// <summary>Above that, one crew member per this many parts (rounded up).</summary>
    public const int PartsPerCrew = 10;

    /// <summary>Crew needed aboard to fly or work (#11): none for small craft.</summary>
    public int CrewRequired => CellCount <= UncrewedMaxParts ? 0 : (CellCount + PartsPerCrew - 1) / PartsPerCrew;

    /// <summary>Everything running at once (full throttle, drilling, refining, building, ejecting), kW.</summary>
    public double TotalPowerUseKW => ThrustPowerKW + DrillPowerKW + RefineryPowerKW + FabricatorPowerKW + EjectorPowerKW + HabitatPowerKW + FarmPowerKW + RobotPowerKW + LabPowerKW;

    /// <summary>Can lock onto a body once landed (clamps release again; foundations never do).</summary>
    public bool CanAnchor => AnchorClampCount > 0 || FoundationCount > 0;

    /// <summary>A foundation makes the structure fixed: anchored for good as soon as it lands.</summary>
    public bool IsFixedStructure => FoundationCount > 0;

    /// <summary>
    /// Full-thrust forward acceleration of the empty craft in m/s². A thruster pushes the way it faces
    /// (#40): rotation 0 forward (<see cref="TotalThrustN"/>), 2 backward (<see cref="ReverseThrustN"/>),
    /// 1 right and 3 left (strafe). <see cref="NonForwardThrustN"/> is the sum of the last three.
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
        p.FabricationRateKgPerS > 0 || p.IsDockingPort || p.CargoCapacityKg > 0 || p.RefiningRateKgPerS > 0
        || p.CrewCapacity > 0 || p.FoodRateKgPerS > 0 || p.IsWarpGate || p.ResearchRatePerS > 0 || p.IsGateDefense || p.GeneratorKW > 0 || p.ConverterKW > 0 || p.GeothermalKW > 0;

    /// <summary>Human-readable problems for the given role; empty means the design is usable in that role.</summary>
    /// <param name="requireConnected">
    /// New designs must be one piece (#35). Craft already built before that rule (in saves) are
    /// loaded with false, so old games keep working.
    /// </param>
    public IReadOnlyList<string> Validate(PartCatalog catalog, DesignRole role = DesignRole.Ship, bool requireConnected = true)
    {
        var errors = new List<string>();
        if (Cells.Count == 0) { errors.Add("Design has no parts."); return errors; }
        if (requireConnected && LooseCells() is { Count: > 0 } loose)
            errors.Add($"{loose.Count} part(s) aren't connected to the rest (cells must touch edge to edge): "
                       + string.Join(", ", loose.Take(5).Select(p => $"({p.X},{p.Y})")) + (loose.Count > 5 ? ", …" : "") + ".");

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

    /// <summary>Advice that doesn't block use. None at the moment (every thruster direction is used since #40).</summary>
    public IReadOnlyList<string> Warnings(PartCatalog catalog) => Array.Empty<string>();

    /// <summary>
    /// Cells not edge-connected to the main piece (#35): the largest group of edge-touching cells
    /// (ties: the one holding the first cell in row order) counts as the craft, everything else is loose.
    /// </summary>
    public IReadOnlyList<GridPos> LooseCells()
    {
        var remaining = new HashSet<GridPos>(Cells.Select(c => c.Pos));
        List<GridPos>? main = null;
        foreach (var start in Cells.Select(c => c.Pos))
        {
            if (!remaining.Remove(start)) continue;
            var group = new List<GridPos> { start };
            for (var i = 0; i < group.Count; i++)
            {
                var p = group[i];
                foreach (var n in new[] { new GridPos(p.X + 1, p.Y), new GridPos(p.X - 1, p.Y), new GridPos(p.X, p.Y + 1), new GridPos(p.X, p.Y - 1) })
                    if (remaining.Remove(n)) group.Add(n);
            }
            if (main is null || group.Count > main.Count) main = group;
        }
        var inMain = new HashSet<GridPos>(main ?? new List<GridPos>());
        return Cells.Select(c => c.Pos).Where(p => !inMain.Contains(p)).ToList();
    }

    /// <summary>Totals and centre of mass (in cell units). Call only on a design whose parts are all known.</summary>
    /// <param name="health">
    /// Damaged parts (#34), 0..1 by cell; missing = undamaged. A damaged part's output (thrust, drill,
    /// refinery, fabricator, solar, ejector) scales with its health; mass, storage and power use don't.
    /// </param>
    public ShipStats ComputeStats(PartCatalog catalog, IReadOnlyDictionary<GridPos, double>? health = null)
    {
        double mass = 0, thrust = 0, otherThrust = 0, cargo = 0, energy = 0, fabrication = 0, mining = 0, refining = 0, momentX = 0, momentY = 0;
        double generation = 0, thrustPower = 0, drillPower = 0, refineryPower = 0, fabricatorPower = 0, ejectorPower = 0, ejection = 0;
        double reverse = 0, left = 0, right = 0, reversePower = 0, leftPower = 0, rightPower = 0;
        double food = 0, habitatPower = 0, farmPower = 0, robots = 0, robotPower = 0;
        var gates = 0;
        var defenses = 0;
        var generatorKW = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var converterKW = new SortedDictionary<string, double>(StringComparer.Ordinal);
        double geothermal = 0;
        double research = 0, labPower = 0;
        var crew = 0;
        int clamps = 0, foundations = 0;
        var ports = new List<DockingPortInfo>();
        foreach (var cell in Cells)
        {
            var part = catalog.Get(cell.PartId);
            var h = health is not null && health.TryGetValue(cell.Pos, out var cellHealth) ? Math.Clamp(cellHealth, 0, 1) : 1;
            mass += part.MassKg;
            // A thruster pushes the way it faces: 0 forward, 1 right, 2 backward, 3 left.
            switch (cell.Rotation)
            {
                case 0: thrust += part.ThrustN * h; break;
                case 1: right += part.ThrustN * h; break;
                case 2: reverse += part.ThrustN * h; break;
                default: left += part.ThrustN * h; break;
            }
            if (cell.Rotation != 0) otherThrust += part.ThrustN * h;
            cargo += part.CargoCapacityKg;
            energy += part.EnergyCapacityKJ;
            fabrication += part.FabricationRateKgPerS * h;
            mining += part.MiningRateKgPerS * h;
            refining += part.RefiningRateKgPerS * h;
            generation += part.PowerGenerationKW * h;
            ejection += part.EjectionRateKgPerS * h;
            crew += part.CrewCapacity;
            food += part.FoodRateKgPerS * h;
            robots += part.RobotCount * h; // a damaged bay has fewer working robots
            research += part.ResearchRatePerS * h;
            // Each part's power use goes to the activity it powers; thrusters by the direction they push.
            if (part.ThrustN > 0)
            {
                switch (cell.Rotation)
                {
                    case 0: thrustPower += part.PowerUseKW; break;
                    case 1: rightPower += part.PowerUseKW; break;
                    case 2: reversePower += part.PowerUseKW; break;
                    default: leftPower += part.PowerUseKW; break;
                }
            }
            else if (part.MiningRateKgPerS > 0) drillPower += part.PowerUseKW;
            else if (part.RefiningRateKgPerS > 0) refineryPower += part.PowerUseKW;
            else if (part.FabricationRateKgPerS > 0) fabricatorPower += part.PowerUseKW;
            else if (part.EjectionRateKgPerS > 0) ejectorPower += part.PowerUseKW;
            else if (part.CrewCapacity > 0) habitatPower += part.PowerUseKW;
            else if (part.FoodRateKgPerS > 0) farmPower += part.PowerUseKW;
            else if (part.RobotCount > 0) robotPower += part.PowerUseKW;
            else if (part.ResearchRatePerS > 0) labPower += part.PowerUseKW;
            if (part.IsWarpGate) gates++;
            if (part.IsGateDefense) defenses++;
            if (part.GeneratorKW > 0) generatorKW[part.Id] = generatorKW.GetValueOrDefault(part.Id) + part.GeneratorKW * h; // #75
            if (part.ConverterKW > 0) converterKW[part.Id] = converterKW.GetValueOrDefault(part.Id) + part.ConverterKW * h; // #78
            geothermal += part.GeothermalKW * h; // #77
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
            Math.Sqrt(radiusCellsSq) * CellSizeM, fabrication, clamps, foundations, otherThrust, ports, mining, refining,
            generation, thrustPower, drillPower, refineryPower, fabricatorPower, ejectorPower, ejection,
            reverse, left, right, reversePower, leftPower, rightPower, crew, food, habitatPower, farmPower,
            (int)Math.Floor(robots + 1e-9), robotPower, gates, research, labPower, defenses,
            generatorKW.Count == 0 ? null : generatorKW.Select(g => GeneratorStats.From(catalog.Get(g.Key), g.Value)).ToList(),
            converterKW.Count == 0 ? null : converterKW.Select(c => ConverterStats.From(catalog.Get(c.Key), c.Value)).ToList(),
            geothermal);
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
