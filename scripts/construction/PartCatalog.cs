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
}

/// <summary>All known parts by stable ID. Immutable after loading.</summary>
public sealed class PartCatalog
{
    public const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, PartDefinition> _parts;

    public PartCatalog(IEnumerable<PartDefinition> parts)
    {
        _parts = new Dictionary<string, PartDefinition>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Id)) throw new FormatException("Part with empty id.");
            if (!(part.MassKg > 0)) throw new FormatException($"Part '{part.Id}' needs massKg > 0.");
            if (part.ThrustN < 0 || part.CargoCapacityKg < 0 || part.EnergyCapacityKJ < 0 || part.MiningRateKgPerS < 0)
                throw new FormatException($"Part '{part.Id}' has a negative stat.");
            if (!_parts.TryAdd(part.Id, part)) throw new FormatException($"Duplicate part id '{part.Id}'.");
        }
    }

    public IReadOnlyCollection<PartDefinition> Parts => _parts.Values;

    public bool TryGet(string id, out PartDefinition part) => _parts.TryGetValue(id, out part!);

    public PartDefinition Get(string id) =>
        _parts.TryGetValue(id, out var part) ? part : throw new KeyNotFoundException($"Unknown part '{id}'.");

    public static PartCatalog FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<PartFile>(json, JsonOptions)
                   ?? throw new FormatException("Part file is empty.");
        if (file.SchemaVersion != SchemaVersion)
            throw new FormatException($"Part file schemaVersion {file.SchemaVersion}, expected {SchemaVersion}.");
        return new PartCatalog(file.Parts ?? Enumerable.Empty<PartDefinition>());
    }

    private sealed class PartFile
    {
        public int SchemaVersion { get; set; }
        public List<PartDefinition>? Parts { get; set; }
    }
}
