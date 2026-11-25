using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Who the player is (#97), and its traits at work (#116). Traits change only the player's own craft.</summary>
public sealed partial class SectorState
{
    private RaceDefinition _race = RaceCatalog.HumanOnly.Default;
    private double _mining = 1, _building = 1, _refining = 1, _research = 1, _power = 1, _crewSupplies = 1, _crewGrowth = 1, _recruitCost = 1;

    /// <summary>Human, as before there were races, unless the game was started as another.</summary>
    public RaceDefinition Race
    {
        get => _race;
        internal set
        {
            _race = value;
            _mining = value.Factor(RaceTraits.Mining);
            _building = value.Factor(RaceTraits.Building);
            _refining = value.Factor(RaceTraits.Refining);
            _research = value.Factor(RaceTraits.Research);
            _power = value.Factor(RaceTraits.Power);
            _crewSupplies = value.Factor(RaceTraits.CrewSupplies);
            _crewGrowth = value.Factor(RaceTraits.CrewGrowth);
            _recruitCost = value.Factor(RaceTraits.RecruitCost);
        }
    }

    /// <summary>The player's machines' power, as a multiple of what their parts say (drills, refineries, fabricators, labs, habitats, farms, robots…; not engines, weapons, shields or gates).</summary>
    public double MachinePowerFactor => _power;

    private double Trait(CraftState craft, double factor) => IsPlayers(craft) ? factor : 1;

    /// <summary>Takes a machine's power, by the race's trait.</summary>
    private bool UseMachinePower(CraftState craft, double kJ) => TryUsePower(craft, kJ * Trait(craft, _power));

    /// <summary>Food and water one recruit costs.</summary>
    public (long FoodKg, long WaterKg) RecruitCostKg => ((long)Math.Round(RecruitFoodKg * _recruitCost), (long)Math.Round(RecruitWaterKg * _recruitCost));

    /// <summary>Why the player can't pass a law, or null.</summary>
    public string? LawBlocker(string lawId) => Race.BlockedLaws.GetValueOrDefault(lawId);

    /// <summary>What a race's data means, in plain words, for the New Game screen.</summary>
    public static IReadOnlyList<string> Describe(RaceDefinition race, PartCatalog catalog)
    {
        string Part(string id) => catalog.TryGet(id, out var p) ? p!.Name : id;
        var lines = new List<string>();
        lines.Add((race.Crew > 0 ? $"{race.Crew} crew at the shipyard" : "No crew at the shipyard") + (race.Supplies ? ", with food and water" : "; no food or water"));
        foreach (var (from, to) in race.ShipyardParts) lines.Add($"The shipyard has a {Part(to).ToLowerInvariant()} instead of a {Part(from).ToLowerInvariant()}");
        foreach (var trait in RaceTraits.All)
            if (race.Traits.GetValueOrDefault(trait.Id) is not 0 and var percent) lines.Add(string.Format(percent > 0 ? trait.More : trait.Less, Math.Abs(percent)));
        if (race.Tech.Count > 0) lines.Add("Knows " + string.Join(", ", race.Tech.Select(t => catalog.Research.TryGet(t, out var tech) ? tech.Name : t)));
        foreach (var (material, kg) in race.StockKg.Where(s => s.Value != 0).OrderBy(s => s.Key, StringComparer.Ordinal))
            lines.Add($"{Math.Abs(kg) / 1000.0:0.#} t {(kg > 0 ? "more" : "less")} {(catalog.Materials.Contains(material) ? catalog.Materials.Get(material).Name.ToLowerInvariant() : material)} at the start");
        foreach (var (law, view) in race.Leanings)
        {
            string Of(double points) => $"{Math.Abs(points):0} {(points > 0 ? "warmer" : "colder")}";
            var name = Laws.Get(law).Name;
            if (view.Wants != 0) lines.Add($"Civilizations that want {name} are {Of(view.Wants)}" + (view.Opposes != 0 ? $", those against it {Of(view.Opposes)}" : ""));
            else if (view.Opposes != 0) lines.Add($"Civilizations against {name} are {Of(view.Opposes)}");
        }
        foreach (var law in race.BlockedLaws.Keys) lines.Add($"Can't pass the law {Laws.Get(law).Name}");
        if (race.Ship is not null) lines.Add($"Starts with its own ship, {race.Ship.Name}");
        if (race.Shipyard is not null) lines.Add($"Starts with its own shipyard, {race.Shipyard.Name}");
        lines.AddRange(race.Notes);
        return lines;
    }
}
