using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpaceAutomationSandbox.Construction;

/// <summary>How a civilization that wants, or opposes, a law sees a race: points on its starting and lasting attitude.</summary>
public sealed record RaceLeaning
{
    public double Wants { get; init; }
    public double Opposes { get; init; }
}

/// <summary>Who the player is (#97), from data/races/races.json: how the game starts, and how civilizations see it.</summary>
public sealed record RaceDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>What choosing it means, for the New Game screen.</summary>
    public List<string> Effects { get; init; } = new();

    /// <summary>People at the starting shipyard.</summary>
    public int Crew { get; init; }

    /// <summary>The starting shipyard has food and water.</summary>
    public bool Supplies { get; init; }

    /// <summary>Parts of the starting shipyard that are something else, old id to new.</summary>
    public Dictionary<string, string> ShipyardParts { get; init; } = new();

    /// <summary>Tech it starts with.</summary>
    public List<string> Tech { get; init; } = new();

    /// <summary>Laws it can't pass, with the reason.</summary>
    public Dictionary<string, string> BlockedLaws { get; init; } = new();

    /// <summary>By law: what civilizations with a view on it think of this race.</summary>
    public Dictionary<string, RaceLeaning> Leanings { get; init; } = new();

    /// <summary>The interface's accent colour, "#rrggbb".</summary>
    public string Accent { get; init; } = "";

    /// <summary>The civilization's lasting view of this race, from its stances on laws.</summary>
    public double Leaning(Func<string, int> stanceOn) =>
        Leanings.Sum(l => stanceOn(l.Key) is var stance && stance > 0 ? l.Value.Wants : stance < 0 ? l.Value.Opposes : 0);
}

/// <summary>The races, in file order; the first is the default, and what older saves play.</summary>
public sealed class RaceCatalog
{
    public const int SchemaVersion = 1;
    public const string HumanId = "human";

    /// <summary>Human alone, as the game played before races (bare tests, tools).</summary>
    public static readonly RaceCatalog HumanOnly = new(new[] { new RaceDefinition { Id = HumanId, Name = "Human", Crew = 4, Supplies = true } });

    private readonly List<RaceDefinition> _races;

    public RaceCatalog(IEnumerable<RaceDefinition> races)
    {
        _races = races.ToList();
        if (_races.Count == 0 || _races[0].Id != HumanId) throw new FormatException($"The first race must be '{HumanId}'.");
        if (_races.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new FormatException($"Duplicate race '{twice.Key}'.");
        foreach (var race in _races)
            if (string.IsNullOrWhiteSpace(race.Id) || string.IsNullOrWhiteSpace(race.Name) || race.Crew < 0) throw new FormatException($"Race '{race.Id}' is invalid: it needs an id, a name and 0 or more crew.");
    }

    public IReadOnlyList<RaceDefinition> Races => _races;

    public RaceDefinition Default => _races[0];

    public bool TryGet(string id, out RaceDefinition race) => (race = _races.FirstOrDefault(r => r.Id == id)!) is not null;

    /// <summary>Reads data/races; parts and tech must exist.</summary>
    public static RaceCatalog FromJson(string json, PartCatalog catalog)
    {
        var file = JsonSerializer.Deserialize<RaceFile>(json, PartCatalog.JsonOptions) ?? throw new FormatException("Race file is empty.");
        if (file.SchemaVersion != SchemaVersion) throw new FormatException($"Race schemaVersion {file.SchemaVersion}; this build reads {SchemaVersion}.");
        var races = new RaceCatalog(file.Races ?? new());
        foreach (var race in races.Races)
        {
            foreach (var part in race.ShipyardParts.SelectMany(p => new[] { p.Key, p.Value }))
                if (!catalog.TryGet(part, out _)) throw new FormatException($"Race '{race.Id}' names unknown part '{part}'.");
            foreach (var tech in race.Tech)
                if (!catalog.Research.TryGet(tech, out _)) throw new FormatException($"Race '{race.Id}' names unknown tech '{tech}'.");
        }
        return races;
    }

    private sealed class RaceFile
    {
        public int SchemaVersion { get; set; }
        public List<RaceDefinition>? Races { get; set; }
    }
}
