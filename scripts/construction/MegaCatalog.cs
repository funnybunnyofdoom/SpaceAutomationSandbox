using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceAutomationSandbox.Construction;

/// <summary>Where a megastructure goes (#126): round a planet, on a rocky planet, or round a star.</summary>
public enum MegaPlace { Planet, RockyPlanet, Star }

/// <summary>One stage of a megastructure: what it takes, and how long it then works.</summary>
public sealed record MegaStage
{
    public string Name { get; init; } = "";
    public SortedDictionary<string, long> MaterialsKg { get; init; } = new(StringComparer.Ordinal);
    public double WorkMinutes { get; init; }
}

/// <summary>What a finished megastructure does; a half-built one does its share by finished stages.</summary>
public sealed record MegaEffect
{
    /// <summary>Extra storage at its hub.</summary>
    public long StorageKg { get; init; }

    /// <summary>Power at its hub (a ring), or shared by your stations in the system (a swarm).</summary>
    public double PowerKW { get; init; }
    public bool PowerToSystem { get; init; }

    /// <summary>Cargo lifted between surface stations on its planet and its hub, kg a minute.</summary>
    public double LiftKgPerMinute { get; init; }

    /// <summary>Extra building rate at its hub.</summary>
    public double FabricationKgPerS { get; init; }
}

/// <summary>A kind of megastructure, from data/megastructures/&lt;id&gt;.json.</summary>
public sealed record MegaKind
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Tech { get; init; } = "";
    public MegaPlace Place { get; init; }

    /// <summary>Its hub's height above the body.</summary>
    public double AltitudeM { get; init; }
    public List<MegaStage> Stages { get; init; } = new();
    public MegaEffect Effect { get; init; } = new();

    /// <summary>What it uses up an hour once a stage is done; without it, it stops working until it has it.</summary>
    public SortedDictionary<string, long> UpkeepKgPerHour { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Grievance per finished stage of the civilization whose system it's in.</summary>
    public double Grievance { get; init; }

    /// <summary>Its hub has a fabricator (the orbital dock).</summary>
    public bool HubBuilds { get; init; }

    public long TotalKg => Stages.Sum(s => s.MaterialsKg.Values.Sum());
}

/// <summary>The kinds of megastructure, by id; one that can't be used is left out with the reason.</summary>
public sealed class MegaCatalog
{
    public const int SchemaVersion = 1;
    public const int MaxStages = 12;
    public static readonly MegaCatalog Empty = new(Array.Empty<MegaKind>(), Array.Empty<string>());

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private MegaCatalog(IReadOnlyList<MegaKind> kinds, IReadOnlyList<string> problems) => (Kinds, Problems) = (kinds, problems);

    public IReadOnlyList<MegaKind> Kinds { get; }
    public IReadOnlyList<string> Problems { get; }

    public MegaKind? Find(string id)
    {
        foreach (var kind in Kinds) if (kind.Id == id) return kind;
        return null;
    }

    /// <summary>Reads kinds from (file name without .json, text) pairs.</summary>
    public static MegaCatalog Load(IEnumerable<(string Name, string Json)> files, PartCatalog catalog)
    {
        var kinds = new List<MegaKind>();
        var problems = new List<string>();
        foreach (var (name, json) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            try { kinds.Add(Read(name, json, catalog)); }
            catch (Exception e) when (e is FormatException or JsonException) { problems.Add($"{name}: {e.Message}"); }
        }
        return new MegaCatalog(kinds, problems);
    }

    private static MegaKind Read(string name, string json, PartCatalog catalog)
    {
        var kind = JsonSerializer.Deserialize<MegaKind>(json, Options) ?? throw new FormatException("it's empty.");
        if (kind.SchemaVersion != SchemaVersion) throw new FormatException($"schemaVersion {kind.SchemaVersion}; this build reads {SchemaVersion}.");
        if (kind.Id != name) throw new FormatException($"its id '{kind.Id}' isn't its file's name.");
        if (kind.Stages is null || kind.Effect is null || kind.UpkeepKgPerHour is null || kind.Stages.Any(s => s?.MaterialsKg is null)) throw new FormatException("something in it is empty.");
        if (string.IsNullOrWhiteSpace(kind.Name)) throw new FormatException("it has no name.");
        if (kind.Tech.Length > 0 && !catalog.Research.TryGet(kind.Tech, out _)) throw new FormatException($"unknown tech '{kind.Tech}'.");
        if (kind.Stages.Count is 0 or > MaxStages) throw new FormatException($"it needs 1 to {MaxStages} stages.");
        if (!(kind.AltitudeM > 0)) throw new FormatException("altitudeM must be above 0.");
        foreach (var stage in kind.Stages)
        {
            if (!(stage.WorkMinutes > 0) || stage.MaterialsKg.Count == 0) throw new FormatException($"stage '{stage.Name}' needs materials and workMinutes.");
            foreach (var (material, kg) in stage.MaterialsKg.Concat(kind.UpkeepKgPerHour))
                if (!catalog.Materials.Contains(material) || kg <= 0) throw new FormatException($"'{material}' {kg} kg isn't right.");
        }
        var e = kind.Effect;
        if (e.StorageKg < 0 || !(e.PowerKW >= 0) || !(e.LiftKgPerMinute >= 0) || !(e.FabricationKgPerS >= 0) || !(kind.Grievance >= 0)) throw new FormatException("its effect can't be negative.");
        return kind;
    }
}
