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

/// <summary>A trait a race may have (#116): what it changes, and the words for more and less of it.</summary>
public sealed record RaceTrait(string Id, string Name, string More, string Less);

/// <summary>The traits there are. Each is a percentage from −<see cref="Limit"/> to +<see cref="Limit"/>; the simulation applies them.</summary>
public static class RaceTraits
{
    public const int Limit = 50;

    public const string Mining = "mining", Building = "building", Refining = "refining", Research = "research",
        Power = "power", CrewSupplies = "crewSupplies", CrewGrowth = "crewGrowth", RecruitCost = "recruitCost";

    public static readonly IReadOnlyList<RaceTrait> All = new[]
    {
        new RaceTrait(Mining, "Mining speed", "Mines {0}% faster", "Mines {0}% slower"),
        new RaceTrait(Building, "Building speed", "Builds, refits and repairs {0}% faster", "Builds, refits and repairs {0}% slower"),
        new RaceTrait(Refining, "Refining speed", "Refines {0}% faster", "Refines {0}% slower"),
        new RaceTrait(Research, "Research speed", "Researches {0}% faster", "Researches {0}% slower"),
        new RaceTrait(Power, "Machines' power use", "Machines use {0}% more power", "Machines use {0}% less power"),
        new RaceTrait(CrewSupplies, "Crew food and water", "Crew need {0}% more food and water", "Crew need {0}% less food and water"),
        new RaceTrait(CrewGrowth, "Crew growth", "Crew grow {0}% faster", "Crew grow {0}% slower"),
        new RaceTrait(RecruitCost, "Recruiting cost", "Recruiting costs {0}% more", "Recruiting costs {0}% less"),
    };
}

/// <summary>Who the player is (#97, #116), from data/races/&lt;id&gt;/race.json: how the game starts, its traits, and how civilizations see it.</summary>
public sealed record RaceDefinition
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Place in the list; the same: by id. Human is always first.</summary>
    public int Order { get; init; } = 100;

    /// <summary>Anything to say on the New Game screen beyond what its data says by itself.</summary>
    public List<string> Notes { get; init; } = new();

    /// <summary>The interface's accent colour, "#rrggbb"; empty for the usual one.</summary>
    public string Accent { get; init; } = "";

    /// <summary>By <see cref="RaceTraits"/> id: percent.</summary>
    public Dictionary<string, int> Traits { get; init; } = new();

    /// <summary>People at the starting shipyard.</summary>
    public int Crew { get; init; }

    /// <summary>The starting shipyard has food and water.</summary>
    public bool Supplies { get; init; }

    /// <summary>More (or less) of a material in the starting shipyard, kg.</summary>
    public Dictionary<string, long> StockKg { get; init; } = new();

    /// <summary>Parts of the starting shipyard that are something else, old id to new.</summary>
    public Dictionary<string, string> ShipyardParts { get; init; } = new();

    /// <summary>Tech it starts with.</summary>
    public List<string> Tech { get; init; } = new();

    /// <summary>Laws it can't pass, with the reason.</summary>
    public Dictionary<string, string> BlockedLaws { get; init; } = new();

    /// <summary>By law: what civilizations with a view on it think of this race.</summary>
    public Dictionary<string, RaceLeaning> Leanings { get; init; } = new();

    /// <summary>What the first ship and the shipyard are called.</summary>
    public string ShipName { get; init; } = "Player";
    public string ShipyardName { get; init; } = "Shipyard";

    /// <summary>Goal hints that differ, by goal id.</summary>
    public Dictionary<string, string> GoalHints { get; init; } = new();

    /// <summary>Its own starting designs (ship.json, shipyard.json), if it has any.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ShipDesign? Ship { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ShipDesign? Shipyard { get; init; }

    /// <summary>The civilization's lasting view of this race, from its stances on laws.</summary>
    public double Leaning(Func<string, int> stanceOn) =>
        Leanings.Sum(l => stanceOn(l.Key) is var stance && stance > 0 ? l.Value.Wants : stance < 0 ? l.Value.Opposes : 0);

    /// <summary>What a trait multiplies by: 1 without it.</summary>
    public double Factor(string trait) => Traits.TryGetValue(trait, out var percent) ? (100 + percent) / 100.0 : 1;
}

/// <summary>A race's files: its folder's name and what's in it.</summary>
public sealed record RaceSource(string Folder, string? Race, string? Ship = null, string? Shipyard = null);

/// <summary>The races, Human first; the default, and what older saves play. A race that can't be used is left out, with the reason.</summary>
public sealed class RaceCatalog
{
    public const int SchemaVersion = 2;
    public const string HumanId = "human";

    /// <summary>Human alone, as the game played before races (bare tests, tools, and if its own file can't be used).</summary>
    public static readonly RaceCatalog HumanOnly = new(new[] { new RaceDefinition { SchemaVersion = SchemaVersion, Id = HumanId, Name = "Human", Crew = 4, Supplies = true } }, Array.Empty<string>());

    private readonly List<RaceDefinition> _races;

    private RaceCatalog(IEnumerable<RaceDefinition> races, IReadOnlyList<string> problems)
    {
        _races = races.OrderBy(r => r.Id != HumanId).ThenBy(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        Problems = problems;
    }

    public IReadOnlyList<RaceDefinition> Races => _races;

    /// <summary>Races that couldn't be used, and why.</summary>
    public IReadOnlyList<string> Problems { get; }

    public RaceDefinition Default => _races[0];

    public bool TryGet(string id, out RaceDefinition race) => (race = _races.FirstOrDefault(r => r.Id == id)!) is not null;

    public static RaceCatalog Load(IEnumerable<RaceSource> sources, PartCatalog catalog, IReadOnlyCollection<string> laws)
    {
        var races = new List<RaceDefinition>();
        var problems = new List<string>();
        foreach (var source in sources.OrderBy(s => s.Folder, StringComparer.Ordinal))
        {
            try { races.Add(Read(source, catalog, laws)); }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException) { problems.Add($"{source.Folder}: {e.Message}"); }
        }
        if (races.All(r => r.Id != HumanId)) races.Add(HumanOnly.Default);
        return new RaceCatalog(races, problems);
    }

    /// <exception cref="FormatException">It can't be used; the message says why.</exception>
    public static RaceDefinition Read(RaceSource source, PartCatalog catalog, IReadOnlyCollection<string> laws)
    {
        if (source.Race is null) throw new FormatException("it has no race.json.");
        var race = JsonSerializer.Deserialize<RaceDefinition>(source.Race, PartCatalog.JsonOptions) ?? throw new FormatException("race.json is empty.");
        if (race.SchemaVersion != SchemaVersion) throw new FormatException($"race.json is schemaVersion {race.SchemaVersion}; this build reads {SchemaVersion}.");
        if (race.Id != source.Folder) throw new FormatException($"its id is '{race.Id}'; it must be the folder's name.");
        if (string.IsNullOrWhiteSpace(race.Name)) throw new FormatException("it has no name.");
        if (race.Crew < 0) throw new FormatException("crew can't be below 0.");
        if (race.Accent.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(race.Accent, "^#[0-9a-fA-F]{6}$")) throw new FormatException($"accent '{race.Accent}' isn't #rrggbb.");
        if (string.IsNullOrWhiteSpace(race.ShipName) || string.IsNullOrWhiteSpace(race.ShipyardName)) throw new FormatException("the ship and the shipyard need names.");
        foreach (var (trait, percent) in race.Traits)
        {
            if (RaceTraits.All.All(t => t.Id != trait)) throw new FormatException($"unknown trait '{trait}'. There are: {string.Join(", ", RaceTraits.All.Select(t => t.Id))}.");
            if (Math.Abs(percent) > RaceTraits.Limit) throw new FormatException($"trait '{trait}' is {percent}%; it must be from -{RaceTraits.Limit} to {RaceTraits.Limit}.");
        }
        foreach (var part in race.ShipyardParts.SelectMany(p => new[] { p.Key, p.Value }))
            if (!catalog.TryGet(part, out _)) throw new FormatException($"unknown part '{part}'.");
        foreach (var tech in race.Tech)
            if (!catalog.Research.TryGet(tech, out _)) throw new FormatException($"unknown tech '{tech}'.");
        foreach (var material in race.StockKg.Keys)
            if (!catalog.Materials.Contains(material)) throw new FormatException($"unknown material '{material}'.");
        foreach (var law in race.BlockedLaws.Keys.Concat(race.Leanings.Keys))
            if (!laws.Contains(law)) throw new FormatException($"unknown law '{law}'.");
        return race with
        {
            Ship = Design(source.Ship, "ship.json", DesignRole.Ship),
            Shipyard = Design(source.Shipyard, "shipyard.json", DesignRole.Station),
        };

        ShipDesign? Design(string? json, string file, DesignRole role)
        {
            if (json is null) return null;
            var design = ShipDesign.FromJson(json);
            var errors = design.Validate(catalog, role);
            return errors.Count == 0 ? design : throw new FormatException($"{file}: {errors[0]}");
        }
    }
}
