using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceAutomationSandbox.Construction;

/// <summary>What a kind of point of interest is (#122).</summary>
public enum PointType { Derelict, Ruin, Anomaly }

/// <summary>What scanning an anomaly shows.</summary>
public enum AnomalyFind { Ore, Energy, Research }

public sealed record PointGoods(string Material, long MinKg, long MaxKg);

public sealed record PointAmounts(double Rare, double Normal, double Often);

public sealed record PointOutcome
{
    public AnomalyFind Find { get; init; }
    public double Weight { get; init; } = 1;
    public string Text { get; init; } = "";
    public List<PointGoods> Ore { get; init; } = new();
    public double ChargeKW { get; init; }
    public double RangeM { get; init; }
}

/// <summary>A kind of point of interest, from data/points/&lt;id&gt;/kind.json.</summary>
public sealed record PointKind
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public PointType Type { get; init; }

    /// <summary>How many a star system has, on average, by the game's option.</summary>
    public PointAmounts PerSystem { get; init; } = new(0, 0, 0);

    /// <summary>How near a ship finds it, and how wide the hint on the map is. A ruin's is above its ground.</summary>
    public double DetectM { get; init; }
    public double HintM { get; init; }

    /// <summary>Height above the planet it orbits (not a ruin).</summary>
    public double MinOrbitM { get; init; }
    public double MaxOrbitM { get; init; }

    /// <summary>Names it may have.</summary>
    public List<string> Names { get; init; } = new();

    /// <summary>How near to be to use it, and how long studying or scanning it takes.</summary>
    public double UseM { get; init; }
    public double StudySeconds { get; init; }

    public double MinResearch { get; init; }
    public double MaxResearch { get; init; }

    /// <summary>A ruin's free technology costs at most this.</summary>
    public double MaxTechCost { get; init; }

    /// <summary>What a derelict may hold (as much as fits), what claiming it takes, and what's damaged or missing of it (shares).</summary>
    public List<PointGoods> Cargo { get; init; } = new();
    public SortedDictionary<string, long> ClaimKg { get; init; } = new(StringComparer.Ordinal);
    public double Damaged { get; init; }
    public double Missing { get; init; }

    public List<PointOutcome> Outcomes { get; init; } = new();

    /// <summary>The designs of a derelict: the other files in its folder.</summary>
    [JsonIgnore]
    public IReadOnlyList<ShipDesign> Designs { get; init; } = Array.Empty<ShipDesign>();
}

/// <summary>A kind's files: its folder's name, kind.json, and its designs.</summary>
public sealed record PointSource(string Folder, string? Kind, IReadOnlyList<string> Designs);

/// <summary>The kinds of points of interest, by id. One that can't be used is left out, with the reason.</summary>
public sealed class PointCatalog
{
    public const int SchemaVersion = 1;
    public static readonly PointCatalog Empty = new(Array.Empty<PointKind>(), Array.Empty<string>());

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private PointCatalog(IReadOnlyList<PointKind> kinds, IReadOnlyList<string> problems) => (Kinds, Problems) = (kinds, problems);

    public IReadOnlyList<PointKind> Kinds { get; }
    public IReadOnlyList<string> Problems { get; }

    public PointKind? Find(string id)
    {
        foreach (var kind in Kinds) if (kind.Id == id) return kind;
        return null;
    }

    public static PointCatalog Load(IEnumerable<PointSource> sources, PartCatalog catalog)
    {
        var kinds = new List<PointKind>();
        var problems = new List<string>();
        foreach (var source in sources.OrderBy(s => s.Folder, StringComparer.Ordinal))
        {
            try { kinds.Add(Read(source, catalog)); }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException) { problems.Add($"{source.Folder}: {e.Message}"); }
        }
        return new PointCatalog(kinds, problems);
    }

    private static PointKind Read(PointSource source, PartCatalog catalog)
    {
        var kind = JsonSerializer.Deserialize<PointKind>(source.Kind ?? throw new FormatException("it has no kind.json."), Options) ?? throw new FormatException("kind.json is empty.");
        if (kind.SchemaVersion != SchemaVersion) throw new FormatException($"kind.json is schemaVersion {kind.SchemaVersion}; this build reads {SchemaVersion}.");
        if (kind.Names is null || kind.PerSystem is null || kind.Cargo is null || kind.ClaimKg is null || kind.Outcomes is null || kind.Cargo.Contains(null!) || kind.Outcomes.Any(o => o?.Ore is null || o.Ore.Contains(null!))) throw new FormatException("something in it is empty.");
        if (kind.Id != source.Folder) throw new FormatException($"its id '{kind.Id}' isn't its folder's name.");
        if (string.IsNullOrWhiteSpace(kind.Name) || kind.Names.Count == 0 || kind.Names.Any(string.IsNullOrWhiteSpace)) throw new FormatException("it needs a name and names for what's found.");
        if (!(kind.PerSystem.Rare >= 0 && kind.PerSystem.Normal >= kind.PerSystem.Rare && kind.PerSystem.Often >= kind.PerSystem.Normal && kind.PerSystem.Often <= 20)) throw new FormatException("perSystem must go up from rare to often, at most 20.");
        if (!(kind.DetectM > 0) || !(kind.HintM > 0) || !(kind.UseM > 0)) throw new FormatException("detectM, hintM and useM must be above 0.");
        if (kind.Type != PointType.Ruin && !(kind.MinOrbitM > 0 && kind.MaxOrbitM > kind.MinOrbitM)) throw new FormatException("it needs minOrbitM and a larger maxOrbitM.");
        if (!(kind.MinResearch >= 0) || !(kind.MaxResearch >= kind.MinResearch)) throw new FormatException("maxResearch must be at least minResearch.");
        if (kind.Type != PointType.Derelict && !(kind.StudySeconds > 0)) throw new FormatException("studySeconds must be above 0.");
        foreach (var goods in kind.Cargo.Concat(kind.Outcomes.SelectMany(o => o.Ore)))
            if (!catalog.Materials.Contains(goods.Material ?? "") || goods.MinKg < 0 || goods.MaxKg < goods.MinKg) throw new FormatException($"'{goods.Material}' {goods.MinKg} to {goods.MaxKg} kg isn't right.");
        foreach (var (material, kg) in kind.ClaimKg)
            if (!catalog.Materials.Contains(material) || kg <= 0) throw new FormatException($"claimKg: '{material}' {kg} kg isn't right.");
        if (kind.Damaged is not (>= 0 and <= 1) || kind.Missing is not (>= 0 and <= 0.9)) throw new FormatException("damaged and missing are shares.");
        if (kind.Type == PointType.Anomaly && (kind.Outcomes.Count == 0 || kind.Outcomes.Any(o => !(o.Weight > 0)))) throw new FormatException("an anomaly needs outcomes, each with a weight.");
        if (kind.Outcomes.Any(o => o.Find == AnomalyFind.Energy && !(o.ChargeKW > 0 && o.RangeM > 0) || o.Find == AnomalyFind.Ore && o.Ore.Count == 0)) throw new FormatException("an energy outcome needs chargeKW and rangeM; an ore one needs ore.");
        var designs = source.Designs.Select(ShipDesign.FromJson).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        foreach (var design in designs)
            if (design.Validate(catalog, design.SuggestedRole(catalog)) is { Count: > 0 } errors) throw new FormatException($"{design.Id}: {string.Join("; ", errors)}");
        if (kind.Type == PointType.Derelict && designs.Count == 0) throw new FormatException("a derelict needs at least one design.");
        return kind with { Designs = designs };
    }
}
