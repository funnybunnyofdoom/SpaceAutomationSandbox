using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceAutomationSandbox.Construction;

/// <summary>What a race costs (#117): good things cost points, bad things give them back. Over the budget it's unbalanced, not forbidden (the creator's choice).</summary>
public static class RacePoints
{
    public const double Budget = 8;

    /// <summary>Points for 5% of a trait for the better; the last three are better when less.</summary>
    private static readonly Dictionary<string, double> Weights = new()
    {
        [RaceTraits.Mining] = 1, [RaceTraits.Building] = 1, [RaceTraits.Refining] = 1, [RaceTraits.Research] = 1,
        [RaceTraits.Power] = -1, [RaceTraits.CrewSupplies] = -0.5, [RaceTraits.CrewGrowth] = 0.5, [RaceTraits.RecruitCost] = -0.25,
    };

    private static readonly Dictionary<string, double> PerTonne = new() { ["metal"] = 0.2, ["electronics"] = 0.75, ["composites"] = 0.6, ["slag"] = 0.05, ["fuel"] = 0.3 };

    public static double Trait(string id, int percent) => percent / 5.0 * Weights.GetValueOrDefault(id);

    public static double Tech(TechDefinition tech) => Math.Ceiling(tech.Cost / 40);

    public static double Stock(string material, long kg) => kg / 1000.0 * PerTonne.GetValueOrDefault(material, 0.5);

    /// <summary>Everything that costs or gives points, and how many.</summary>
    public static IReadOnlyList<(string What, double Points)> Items(RaceDefinition race, PartCatalog catalog)
    {
        var items = new List<(string, double)>();
        void Add(string what, double points) { if (Math.Abs(points) > 1e-9) items.Add((what, points)); }
        foreach (var trait in RaceTraits.All) Add(trait.Name, Trait(trait.Id, race.Traits.GetValueOrDefault(trait.Id)));
        Add("Crew", race.Crew - 4);
        Add("No food and water", race.Supplies ? 0 : -2);
        foreach (var (from, to) in race.ShipyardParts) Add($"{Name(to)} for {Name(from).ToLowerInvariant()}", (from, to) == ("habitat", "robot_bay") ? 6 : 1);
        foreach (var id in race.Tech) if (catalog.Research.TryGet(id, out var tech)) Add(tech.Name, Tech(tech));
        foreach (var (material, kg) in race.StockKg) Add($"Stock: {(catalog.Materials.Contains(material) ? catalog.Materials.Get(material).Name.ToLowerInvariant() : material)}", Stock(material, kg));
        Add("Civilizations' views", race.Leanings.Values.Sum(l => l.Wants + l.Opposes) / 10);
        Add("Laws it can't pass", -race.BlockedLaws.Count);
        if (race.Ship is { } ship) Add($"Starting ship: {ship.Name}", DesignPoints(ship, catalog, "starter_ship")); // #125
        if (race.Shipyard is { } yard) Add($"Starting shipyard: {yard.Name}", DesignPoints(yard, catalog, "starter_shipyard"));
        return items;
        string Name(string part) => catalog.TryGet(part, out var p) ? p!.Name : part;
    }

    /// <summary>A starting design costs by what its materials are worth beyond the usual one's, about 4 points per 10 t of metal's worth; a smaller one gives back.</summary>
    public static double DesignPoints(ShipDesign design, PartCatalog catalog, string usualId)
    {
        double Worth(ShipDesign d) => d.ComputeBillOfMaterials(catalog).Kg.Sum(m => m.Value * (catalog.Materials.Contains(m.Key) ? catalog.Materials.Get(m.Key).Value : 1));
        var usual = usualId == "starter_ship" ? UsualShipWorth : UsualShipyardWorth;
        return usual <= 0 ? 0 : Math.Round((Worth(design) - usual) / (10_000 * (catalog.Materials.Contains("metal") ? catalog.Materials.Get("metal").Value : 1)) * 4 * 4) / 4;
    }

    /// <summary>What the usual starting designs are worth, set when the game's data loads.</summary>
    public static double UsualShipWorth { get; set; }
    public static double UsualShipyardWorth { get; set; }

    public static void SetUsual(ShipDesign ship, ShipDesign shipyard, PartCatalog catalog)
    {
        double Worth(ShipDesign d) => d.ComputeBillOfMaterials(catalog).Kg.Sum(m => m.Value * (catalog.Materials.Contains(m.Key) ? catalog.Materials.Get(m.Key).Value : 1));
        (UsualShipWorth, UsualShipyardWorth) = (Worth(ship), Worth(shipyard));
    }

    public static double Total(RaceDefinition race, PartCatalog catalog) => Math.Round(Items(race, catalog).Sum(i => i.Points), 2);

    public static bool Unbalanced(RaceDefinition race, PartCatalog catalog) => Total(race, catalog) > Budget;
}

/// <summary>The player's own races (#117): their rules beyond what any race's are, and the one file a race is shared as.</summary>
public static class RaceFiles
{
    public const string CustomPrefix = "custom_";
    public const int ShareVersion = 1;
    public const string ShareKind = "race";

    /// <summary>The biggest shared file read, and the biggest picture in one.</summary>
    public const int MaxShareBytes = 8_000_000, MaxPictureBytes = 4_000_000;

    public const int MaxCrew = 6, MaxLeaning = 50;
    public const long MaxStockKg = 20_000;

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(RaceDefinition race) => JsonSerializer.Serialize(race with { SchemaVersion = RaceCatalog.SchemaVersion }, Options);

    /// <summary>An id for a race of this name that nothing has yet.</summary>
    public static string NewId(string name, Func<string, bool> taken)
    {
        var slug = CustomPrefix + BlueprintEditor.SlugFromName(name);
        for (var i = 1; ; i++)
            if ((i == 1 ? slug : $"{slug}_{i}") is var id && !taken(id)) return id;
    }

    /// <summary>Why a race of the player's can't be played, or null. (Over the points budget is only unbalanced.)</summary>
    /// <summary>The game's parts, for checking a shipyard of the player's own (set when the data loads).</summary>
    public static PartCatalog? Catalog { get; set; }

    public static string? Problem(RaceDefinition race)
    {
        var robots = race.ShipyardParts.GetValueOrDefault("habitat") == "robot_bay";
        if (string.IsNullOrWhiteSpace(race.Name)) return "Give it a name.";
        if (race.Shipyard is { } yard && race.ShipyardParts.Count > 0) return "With a shipyard of its own, choose its parts there, not by swapping the habitat.";
        if (race.Shipyard is { } own && Catalog is not null) // #125: it must work with the crew it has
        {
            var stats = own.ComputeStats(Catalog!);
            if (stats.FabricationRateKgPerS <= 0 || stats.DockingPorts.Count == 0) return "Its shipyard needs a fabricator and a docking port.";
            if (race.Crew > stats.CrewCapacity) return $"Its shipyard's habitats hold {stats.CrewCapacity} crew.";
            if (stats.CrewRequired > race.Crew + stats.RobotCount) return $"Its shipyard needs {stats.CrewRequired} crew or robots to work.";
            if (race.Crew > 0 && !race.Supplies) return "Crew need food and water at the start.";
            return race.StockKg.Values.Any(kg => Math.Abs(kg) > MaxStockKg) ? $"At most {MaxStockKg / 1000} t more or less of a material." : null;
        }
        if (race.ShipyardParts.Any(p => (p.Key, p.Value) != ("habitat", "robot_bay"))) return "Only the shipyard's habitat can be swapped, for a robot bay.";
        if (robots && race.Crew > 0) return "With a robot bay instead of the habitat, the shipyard has nowhere for crew.";
        if (!robots && race.Crew < 2) return "Its shipyard needs 2 crew, or robots, to work.";
        if (race.Crew > MaxCrew) return $"The shipyard's habitat holds {MaxCrew} crew.";
        if (race.Crew > 0 && !race.Supplies) return "Crew need food and water at the start.";
        if (race.StockKg.Values.Any(kg => Math.Abs(kg) > MaxStockKg)) return $"At most {MaxStockKg / 1000} t more or less of a material.";
        if (race.Leanings.Values.Any(l => !(Math.Abs(l.Wants) <= MaxLeaning) || !(Math.Abs(l.Opposes) <= MaxLeaning))) return $"Civilizations' views are from -{MaxLeaning} to {MaxLeaning}.";
        return null;
    }

    /// <summary>A race and its picture as one file.</summary>
    public static string ToShareFile(RaceDefinition race, byte[]? picture) => JsonSerializer.Serialize(new ShareFile
    {
        SchemaVersion = ShareVersion, Kind = ShareKind, Race = JsonSerializer.SerializeToElement(race with { SchemaVersion = RaceCatalog.SchemaVersion }, Options), Picture = picture is null ? null : Convert.ToBase64String(picture),
    }, Options);

    /// <summary>Reads a shared race. Its id is only a suggestion: the one who imports it gives it a free one.</summary>
    /// <exception cref="FormatException">It can't be used; the message says why.</exception>
    public static (RaceDefinition Race, byte[]? Picture) FromShareFile(string json, PartCatalog catalog, IReadOnlyCollection<string> laws)
    {
        if (json.Length > MaxShareBytes) throw new FormatException("The file is too big to be a race.");
        ShareFile? file;
        try { file = JsonSerializer.Deserialize<ShareFile>(json, Options); }
        catch (JsonException) { throw new FormatException("It isn't a race file."); }
        if (file is null || file.Kind != ShareKind || file.Race.ValueKind != JsonValueKind.Object) throw new FormatException("It isn't a race file.");
        if (file.SchemaVersion != ShareVersion) throw new FormatException($"The file is version {file.SchemaVersion}; this build reads {ShareVersion}.");
        RaceDefinition race;
        try
        {
            var read = JsonSerializer.Deserialize<RaceDefinition>(file.Race.GetRawText(), Options) ?? throw new FormatException("It has no race.");
            race = RaceCatalog.Check(read, catalog, laws) with { Id = NewId(read.Name, _ => false) };
        }
        catch (JsonException e) { throw new FormatException($"Its race can't be read: {e.Message}"); }
        if (Problem(race) is { } why) throw new FormatException(why);
        byte[]? picture = null;
        if (file.Picture is { Length: > 0 })
        {
            try { picture = Convert.FromBase64String(file.Picture); }
            catch (FormatException) { throw new FormatException("Its picture is damaged."); }
            if (picture.Length > MaxPictureBytes) throw new FormatException("Its picture is too big.");
        }
        return (race, picture);
    }

    /// <summary>The part of a picture kept: the middle, 4:5. Null if it's too small.</summary>
    public static (int X, int Y, int Width, int Height)? Crop(int width, int height)
    {
        if (width < 64 || height < 80) return null;
        var (w, h) = width * 5 > height * 4 ? (height * 4 / 5, height) : (width, width * 5 / 4);
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    private sealed class ShareFile
    {
        public int SchemaVersion { get; set; }
        public string? Kind { get; set; }
        public JsonElement Race { get; set; }
        public string? Picture { get; set; }
    }
}
