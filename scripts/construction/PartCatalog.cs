using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpaceAutomationSandbox.Construction;

/// <summary>One kind of grid part, loaded from data/parts/parts.json (see data/README.md).</summary>
public sealed record PartDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double MassKg { get; init; }
    public string? PlaceholderColor { get; init; }
    public bool IsControlSource { get; init; }
    public double ThrustN { get; init; }
    public double EnergyCapacityKJ { get; init; }
    public double CargoCapacityKg { get; init; }
    public double MiningRateKgPerS { get; init; }

    /// <summary>Lets a landed craft lock itself to the body it's on, and release again (mobile structures).</summary>
    public bool IsAnchorClamp { get; init; }

    /// <summary>Permanently anchors the structure when it lands; it can never be released (fixed structures).</summary>
    public bool IsFoundation { get; init; }

    /// <summary>One berth where a ship can dock; it opens in the direction the part faces.</summary>
    public bool IsDockingPort { get; init; }

    /// <summary>Power drawn while working, kW (thrusters at full throttle, drills, refineries, fabricators, ejectors).</summary>
    public double PowerUseKW { get; init; }

    /// <summary>Power produced, kW (solar panels; constant for now, no day/night).</summary>
    public double PowerGenerationKW { get; init; }

    /// <summary>Slag thrown away per second (slag ejector).</summary>
    public double EjectionRateKgPerS { get; init; }

    /// <summary>Crew this part houses (habitat, #11).</summary>
    public int CrewCapacity { get; init; }

    /// <summary>Research points made per second (research lab, #37), using power and a little electronics.</summary>
    public double ResearchRatePerS { get; init; }

    /// <summary>A warp gate (#22): the station it's on can send craft to its linked gate in another star system.</summary>
    public bool IsWarpGate { get; init; }

    // --- Generators (#75): a part that turns a batch of materials into energy. Data-driven: any fuel, any products. ---

    /// <summary>Most power one generator part gives (kW). It burns only what's needed (#75).</summary>
    public double GeneratorKW { get; init; }

    /// <summary>One batch it burns, whole kg by material (e.g. 1 kg fuel).</summary>
    public Dictionary<string, double>? GeneratorBurnsKg { get; init; }

    /// <summary>What a batch leaves behind in the craft's stock (whole kg); the rest of the batch's mass is exhaust (ejected).</summary>
    public Dictionary<string, double>? GeneratorMakesKg { get; init; }

    /// <summary>Energy from one batch (kJ).</summary>
    public double GeneratorKJPerBatch { get; init; }

    /// <summary>A gate defense battery (#36): with the Armed gates law, each makes a raid on its gate less likely to succeed.</summary>
    public bool IsGateDefense { get; init; }

    /// <summary>Robots this part holds (robot bay, #13): they count as crew for running a craft, using power instead of food.</summary>
    public int RobotCount { get; init; }

    /// <summary>Food grown per second from the same mass of water (farm, #11).</summary>
    public double FoodRateKgPerS { get; init; }

    /// <summary>How many kg of recipe input this part refines per second (refineries).</summary>
    public double RefiningRateKgPerS { get; init; }

    /// <summary>How many kg of finished craft this part assembles per second (shipyards).</summary>
    public double FabricationRateKgPerS { get; init; }

    /// <summary>What one part is built from (kg per material id). Must add up to <see cref="MassKg"/>.</summary>
    public Dictionary<string, double>? MaterialsKg { get; init; }

    public MaterialBill Bill => new(MaterialsKg ?? new Dictionary<string, double>());
}

/// <summary>
/// All known parts by stable ID. Immutable after loading. Every part's materials must be known
/// and add up exactly to its mass: building a part neither creates nor destroys mass (docs/02).
/// </summary>
public sealed class PartCatalog
{
    /// <summary>v2 added the required <c>materialsKg</c>.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Allowed rounding gap between a part's mass and the sum of its materials, relative to mass.</summary>
    private const double MassBalanceTolerance = 1e-9;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, PartDefinition> _parts;
    private readonly List<PartDefinition> _ordered = new();

    public PartCatalog(IEnumerable<PartDefinition> parts, MaterialCatalog materials, RecipeCatalog? recipes = null)
    {
        Materials = materials;
        Recipes = recipes ?? RecipeCatalog.Empty;
        _parts = new Dictionary<string, PartDefinition>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Id)) throw new FormatException("Part with empty id.");
            if (!(part.MassKg > 0)) throw new FormatException($"Part '{part.Id}' needs massKg > 0.");
            if (part.ThrustN < 0 || part.CargoCapacityKg < 0 || part.EnergyCapacityKJ < 0 || part.MiningRateKgPerS < 0
                || part.FabricationRateKgPerS < 0 || part.RefiningRateKgPerS < 0
                || part.PowerUseKW < 0 || part.PowerGenerationKW < 0 || part.EjectionRateKgPerS < 0
                || part.CrewCapacity < 0 || part.FoodRateKgPerS < 0 || part.RobotCount < 0 || part.ResearchRatePerS < 0)
                throw new FormatException($"Part '{part.Id}' has a negative stat.");
            ValidateMaterials(part, materials);
            ValidateGenerator(part, materials);
            if (!_parts.TryAdd(part.Id, part)) throw new FormatException($"Duplicate part id '{part.Id}'.");
            _ordered.Add(part);
        }
    }

    public MaterialCatalog Materials { get; }

    /// <summary>Refining recipes, carried with the parts so every loader gets the same definitions.</summary>
    public RecipeCatalog Recipes { get; }

    /// <summary>The tech tree (#37), carried with the parts. Empty: nothing is locked.</summary>
    public ResearchCatalog Research { get; private set; } = ResearchCatalog.Empty;

    /// <summary>Attaches the tech tree (checked against these parts). Returns this catalog.</summary>
    public PartCatalog WithResearch(ResearchCatalog research)
    {
        research.Validate(this);
        Research = research;
        return this;
    }

    private static void ValidateGenerator(PartDefinition part, MaterialCatalog materials)
    {
        if (part.GeneratorKW <= 0) return;
        if (part.GeneratorBurnsKg is not { Count: > 0 } burns || !(part.GeneratorKJPerBatch > 0))
            throw new FormatException($"Generator '{part.Id}' needs generatorBurnsKg and generatorKJPerBatch.");
        double burned = 0, made = 0;
        foreach (var (id, kg) in burns.Concat(part.GeneratorMakesKg ?? new Dictionary<string, double>()))
        {
            if (!materials.Contains(id)) throw new FormatException($"Generator '{part.Id}' uses unknown material '{id}'.");
            if (!(kg > 0) || kg != Math.Floor(kg)) throw new FormatException($"Generator '{part.Id}': '{id}' must be a positive whole number of kg.");
        }
        foreach (var kg in burns.Values) burned += kg;
        foreach (var kg in (part.GeneratorMakesKg ?? new Dictionary<string, double>()).Values) made += kg;
        if (made > burned) throw new FormatException($"Generator '{part.Id}' makes more mass than it burns.");
    }

    private static void ValidateMaterials(PartDefinition part, MaterialCatalog materials)
    {
        if (part.MaterialsKg is not { Count: > 0 } amounts)
            throw new FormatException($"Part '{part.Id}' needs materialsKg.");
        foreach (var (id, kg) in amounts)
        {
            if (!materials.Contains(id)) throw new FormatException($"Part '{part.Id}' uses unknown material '{id}'.");
            if (!(kg > 0) || double.IsInfinity(kg)) throw new FormatException($"Part '{part.Id}' needs a positive amount of '{id}'.");
            // Inventories hold whole kilograms (docs/06), so costs must be whole too.
            if (kg != Math.Floor(kg)) throw new FormatException($"Part '{part.Id}': '{id}' must be a whole number of kg (got {kg}).");
        }
        var sum = amounts.Values.Sum();
        if (Math.Abs(sum - part.MassKg) > part.MassKg * MassBalanceTolerance)
            throw new FormatException($"Part '{part.Id}' weighs {part.MassKg} kg but its materials add up to {sum} kg.");
    }

    /// <summary>All parts in file order (the editor palette uses this order).</summary>
    public IReadOnlyList<PartDefinition> Parts => _ordered;

    public bool TryGet(string id, out PartDefinition part) => _parts.TryGetValue(id, out part!);

    public PartDefinition Get(string id) =>
        _parts.TryGetValue(id, out var part) ? part : throw new KeyNotFoundException($"Unknown part '{id}'.");

    public static PartCatalog FromJson(string json, MaterialCatalog materials, RecipeCatalog? recipes = null)
    {
        var file = JsonSerializer.Deserialize<PartFile>(json, JsonOptions)
                   ?? throw new FormatException("Part file is empty.");
        if (file.SchemaVersion != SchemaVersion)
            throw new FormatException($"Part file schemaVersion {file.SchemaVersion}, expected {SchemaVersion}.");
        return new PartCatalog(file.Parts ?? Enumerable.Empty<PartDefinition>(), materials, recipes);
    }

    private sealed class PartFile
    {
        public int SchemaVersion { get; set; }
        public List<PartDefinition>? Parts { get; set; }
    }
}
