using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpaceAutomationSandbox.Construction;

/// <summary>One kind of material, loaded from data/materials/materials.json (see data/README.md).</summary>
public sealed record MaterialDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? PlaceholderColor { get; init; }

    /// <summary>What a kg is worth in trade (#89), before each civilization's own taste; 0 if nobody wants it.</summary>
    public double Value { get; init; }
}

/// <summary>All known materials by stable ID, in file order (used for display order).</summary>
public sealed class MaterialCatalog
{
    public const int SchemaVersion = 1;

    private readonly List<MaterialDefinition> _ordered = new();
    private readonly Dictionary<string, MaterialDefinition> _byId = new(StringComparer.Ordinal);

    public MaterialCatalog(IEnumerable<MaterialDefinition> materials)
    {
        foreach (var material in materials)
        {
            if (string.IsNullOrWhiteSpace(material.Id)) throw new FormatException("Material with empty id.");
            if (!(material.Value >= 0)) throw new FormatException($"Material '{material.Id}' has a negative value.");
            if (!_byId.TryAdd(material.Id, material)) throw new FormatException($"Duplicate material id '{material.Id}'.");
            _ordered.Add(material);
        }
    }

    public IReadOnlyList<MaterialDefinition> Materials => _ordered;

    public bool Contains(string id) => _byId.ContainsKey(id);

    public MaterialDefinition Get(string id) =>
        _byId.TryGetValue(id, out var m) ? m : throw new KeyNotFoundException($"Unknown material '{id}'.");

    /// <summary>Display index (file order); unknown ids sort last.</summary>
    public int OrderOf(string id)
    {
        var index = _ordered.FindIndex(m => m.Id == id);
        return index < 0 ? int.MaxValue : index;
    }

    public static MaterialCatalog FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<MaterialFile>(json, PartCatalog.JsonOptions)
                   ?? throw new FormatException("Material file is empty.");
        if (file.SchemaVersion != SchemaVersion)
            throw new FormatException($"Material file schemaVersion {file.SchemaVersion}, expected {SchemaVersion}.");
        return new MaterialCatalog(file.Materials ?? Enumerable.Empty<MaterialDefinition>());
    }

    private sealed class MaterialFile
    {
        public int SchemaVersion { get; set; }
        public List<MaterialDefinition>? Materials { get; set; }
    }
}

/// <summary>
/// Material amounts in kilograms, keyed by material id. Immutable; ordered by id so sums and
/// output are deterministic. Placeholder unit: double kg holding whole-kg data, which is exact
/// up to 2^53 kg. The authoritative inventory ledger unit is decided with mining (Phase 3).
/// </summary>
public sealed class MaterialBill
{
    public static readonly MaterialBill Empty = new(new Dictionary<string, double>());

    private readonly SortedDictionary<string, double> _kg;

    public MaterialBill(IEnumerable<KeyValuePair<string, double>> amounts)
    {
        _kg = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, kg) in amounts)
        {
            if (!(kg >= 0) || double.IsInfinity(kg)) throw new ArgumentOutOfRangeException(nameof(amounts), $"{id}: {kg} kg");
            if (kg == 0) continue;
            _kg[id] = _kg.TryGetValue(id, out var existing) ? existing + kg : kg;
        }
    }

    public IReadOnlyDictionary<string, double> Kg => _kg;

    public double TotalKg => _kg.Values.Sum();

    public double this[string id] => _kg.TryGetValue(id, out var kg) ? kg : 0;

    public MaterialBill Plus(MaterialBill other) => new(_kg.Concat(other._kg));

    /// <summary>The same amounts as whole kg (for inventories). Throws if any amount has a fraction.</summary>
    public SortedDictionary<string, long> ToWholeKg()
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (id, kg) in _kg)
        {
            if (kg != Math.Floor(kg) || kg > long.MaxValue) throw new InvalidOperationException($"{id}: {kg} kg is not a whole number of kg.");
            result[id] = (long)kg;
        }
        return result;
    }

    public override string ToString() => string.Join(", ", _kg.Select(kv => $"{kv.Key} {kv.Value:R} kg"));
}
