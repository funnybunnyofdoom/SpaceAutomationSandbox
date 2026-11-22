using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpaceAutomationSandbox.Construction;

/// <summary>One tech from data/research/research.json (#37): what it costs, what it needs first, which parts it unlocks.</summary>
public sealed record TechDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double Cost { get; init; }
    public List<string>? Requires { get; init; }
    public List<string>? Parts { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>
/// The tech tree (#37), in file order. A part is locked until the tech that lists it is researched;
/// parts no tech lists are open from the start. <see cref="Empty"/> locks nothing (bare tests, tools).
/// </summary>
public sealed class ResearchCatalog
{
    public const int SchemaVersion = 1;

    public static readonly ResearchCatalog Empty = new(Array.Empty<TechDefinition>());

    private readonly List<TechDefinition> _techs = new();
    private readonly Dictionary<string, TechDefinition> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TechDefinition> _byPart = new(StringComparer.Ordinal);

    public ResearchCatalog(IEnumerable<TechDefinition> techs)
    {
        foreach (var tech in techs)
        {
            if (string.IsNullOrWhiteSpace(tech.Id)) throw new FormatException("Tech with empty id.");
            if (!(tech.Cost >= 0)) throw new FormatException($"Tech '{tech.Id}' needs a cost of 0 or more.");
            if (!_byId.TryAdd(tech.Id, tech)) throw new FormatException($"Duplicate tech id '{tech.Id}'.");
            foreach (var need in tech.Requires ?? new List<string>())
                if (!_byId.ContainsKey(need)) throw new FormatException($"Tech '{tech.Id}' requires '{need}', which must come earlier in the file.");
            foreach (var part in tech.Parts ?? new List<string>())
                if (!_byPart.TryAdd(part, tech)) throw new FormatException($"Part '{part}' is unlocked by two techs.");
            _techs.Add(tech);
        }
    }

    public IReadOnlyList<TechDefinition> Techs => _techs;

    public bool TryGet(string id, out TechDefinition tech) => _byId.TryGetValue(id, out tech!);

    /// <summary>The tech that unlocks a part, or null if it's open from the start.</summary>
    public TechDefinition? TechFor(string partId) => _byPart.TryGetValue(partId, out var tech) ? tech : null;

    /// <summary>Checks that every part a tech lists exists.</summary>
    public void Validate(PartCatalog parts)
    {
        foreach (var (part, tech) in _byPart)
            if (!parts.TryGet(part, out _)) throw new FormatException($"Tech '{tech.Id}' unlocks unknown part '{part}'.");
    }

    public static ResearchCatalog FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<ResearchFile>(json, PartCatalog.JsonOptions) ?? throw new FormatException("Research file is empty.");
        if (file.SchemaVersion != SchemaVersion) throw new FormatException($"Research file schemaVersion {file.SchemaVersion}, expected {SchemaVersion}.");
        return new ResearchCatalog(file.Techs ?? new List<TechDefinition>());
    }

    private sealed class ResearchFile
    {
        public int SchemaVersion { get; set; }
        public List<TechDefinition>? Techs { get; set; }
    }
}
