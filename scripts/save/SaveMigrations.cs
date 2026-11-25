using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Save;

/// <summary>
/// Upgrades an older save, one schema version at a time, on the raw JSON before it is read (#26).
/// Each step turns version N into N+1 and documents what changed, so the loader only ever reads the
/// current format. To change the format: bump <see cref="SaveGameSerializer.SchemaVersion"/>, add one
/// step here (even if it changes nothing, so the history stays explicit), and update docs/08.
/// </summary>
public static class SaveMigrations
{
    private delegate void Step(JsonObject root, PartCatalog catalog);

    /// <summary>Step that upgrades version key → key + 1.</summary>
    private static readonly SortedDictionary<int, (string What, Step Apply)> Steps = new()
    {
        [1] = ("v2 added landing: absent fields already mean 'flying'", (_, _) => { }),
        [2] = ("v3 added rail orbits, inventories, build queues: absent means none", (_, _) => { }),
        [3] = ("v4 added 'anchored': a landed craft with a foundation was always anchored", MarkLandedFoundationsAnchored),
        [4] = ("v5 added rotation, craft targets, docking, landing sites: absent means none", (_, _) => { }),
        [5] = ("v6 added deposits, drill remainder, mining jobs: absent means none", (_, _) => { }),
        [6] = ("v7 added survey: older games had no survey, so every body counts as surveyed; jobs were all mining", MarkAllSurveyedAndJobsMining),
        [7] = ("v8 added debug tuning and job retry ticks: absent means default", (_, _) => { }),
        [8] = ("v9 limited storage to storage parts: stations that held stock without any keep unlimited storage", KeepLegacyStationsUnlimited),
        [9] = ("v10 added power: craft from older games have no solar panels, so they get free power and full batteries", GiveLegacyCraftFreePower),
        [10] = ("v11 replaced landing sites with construction kits: queued station orders now finish as kits; requests and kits start empty", DropLandingSites),
        [11] = ("v12 added the star system (new games only), the cruise drive and solar power by distance: older saves keep their sector, nothing cruises, full sunlight without a star", (_, _) => { }),
        [12] = ("v13 added part health and local gravity: older craft are undamaged; gravity simply applies from now on", (_, _) => { }),
        [13] = ("v14 added crew and life support: craft from older games need no crew, so they keep working", ExemptLegacyCraftFromCrew),
        [14] = ("v15 added abstract transit for craft far from the player: nothing is in transit in an older save", (_, _) => { }),
        [15] = ("v16 added repairs and survey jobs: parts that broke off in an older save can't be rebuilt (their blueprint wasn't kept); damage can be repaired", (_, _) => { }),
        [16] = ("v17 added refit, dismantle and pack orders and the craft id counter: no orders in an older save; the counter follows from its craft", (_, _) => { }),
        [17] = ("v18 added warp gates and further star systems: an older save has one system and no gates", (_, _) => { }),
        [18] = ("v19 added research: a game started before it keeps everything unlocked (it was all open then)",
            (root, _) => { if (root["sector"] is System.Text.Json.Nodes.JsonObject sector) sector["researchAllUnlocked"] = true; }),
        [19] = ("v20 added swarms: an older save has none", (_, _) => { }),
        [20] = ("v21 added combat: every craft in an older save is the player's and whole, and no raid is on", (_, _) => { }),
        [21] = ("v22 added salvage jobs: an older save has none", (_, _) => { }),
        [22] = ("v23 added missiles and shields: an older save has none", (_, _) => { }),
        [23] = ("v24 added guard, escort and patrol jobs, and civilization patrols: an older save has none (patrols appear after loading)", (_, _) => { }),
        [24] = ("v25 added trade stations, trade routes and goodwill: an older save has none (stations appear after loading)", (_, _) => { }),
        [25] = ("v26 added native life: no body of an older save has any; systems opened later may", (_, _) => { }),
        [26] = ("v27 added native animals: living worlds of an older save have none (they can be settled)", (_, _) => { }),
        [27] = ("v28 added antimatter containment and quantum drives: an older save has none", (_, _) => { }),
        [28] = ("v29 added milestones and closable gates: what an older game already reached is noted without a time; its gates are open", (_, _) => { }),
        [29] = ("v30 added flying to a target: an older save has none under way", (_, _) => { }),
        [30] = ("v31 added what events are about: older events are kept as plain ones", (_, _) => { }),
        [31] = ("v32 added large-ship building: opens in an older game once a Fabricator Mk3 works", (_, _) => { }),
        [32] = ("v33 added favourite targets and names given to bodies: none in older games", (_, _) => { }),
        [33] = ("v34 added races: older games are Human", (_, _) => { }),
        [34] = ("v35 added races of the player's own: older games have none", (_, _) => { }),
        [35] = ("v36 gave civilizations races: those in older games get one that fits, and see you as before", (_, _) => { }),
        [36] = ("older games have none", (_, _) => { }),
        [37] = ("v38 added world options and points of interest: older games have them off", (_, _) => { }),
        [38] = ("v39 added hazards: older games have them off", (_, _) => { }),
        [39] = ("v40 added contracts: older games have them off", (_, _) => { }),
        [40] = ("v41 gave civilizations' races traits and designs: civilizations met before play as they did", (_, _) => { }),
    };

    /// <summary>Versions this build can upgrade from, oldest first (for tests and docs).</summary>
    public static IReadOnlyList<int> UpgradableFrom => Steps.Keys.ToList();

    /// <summary>Applies every step from the file's version up to the current one; returns the steps applied.</summary>
    public static IReadOnlyList<string> Migrate(JsonObject root, PartCatalog catalog)
    {
        var applied = new List<string>();
        var version = root["schemaVersion"]?.GetValue<int>() ?? 0;
        while (version < SaveGameSerializer.SchemaVersion)
        {
            if (!Steps.TryGetValue(version, out var step))
                throw new FormatException($"No upgrade from save schemaVersion {version}.");
            step.Apply(root, catalog);
            version++;
            root["schemaVersion"] = version;
            applied.Add($"v{version}: {step.What}");
        }
        return applied;
    }

    private static IEnumerable<JsonObject> Crafts(JsonObject root) =>
        (root["sector"]?["crafts"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    private static IEnumerable<JsonObject> Bodies(JsonObject root) =>
        (root["sector"]?["bodies"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    /// <summary>The parts of a saved design that the catalog knows (unknown ones fail later, with a reason).</summary>
    private static IEnumerable<PartDefinition> Parts(JsonObject craft, PartCatalog catalog) =>
        ((craft["design"]?["cells"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        .Select(cell => cell["part"]?.GetValue<string>())
        .Where(id => id != null && catalog.TryGet(id, out _))
        .Select(id => catalog.Get(id!));

    private static void MarkLandedFoundationsAnchored(JsonObject root, PartCatalog catalog)
    {
        foreach (var craft in Crafts(root))
        {
            var landed = (craft["landedOnBodyId"]?.GetValue<uint>() ?? 0) != 0;
            craft["anchored"] = landed && Parts(craft, catalog).Any(p => p.IsFoundation);
        }
    }

    private static void MarkAllSurveyedAndJobsMining(JsonObject root, PartCatalog catalog)
    {
        foreach (var body in Bodies(root)) body["surveyed"] = true;
        foreach (var craft in Crafts(root))
            if (craft["job"] is JsonObject job && job["kind"] is null) job["kind"] = "mining";
    }

    private static void GiveLegacyCraftFreePower(JsonObject root, PartCatalog catalog)
    {
        foreach (var craft in Crafts(root))
        {
            craft["legacyFreePower"] = true;
            craft["storedEnergyKJ"] = Parts(craft, catalog).Sum(p => p.EnergyCapacityKJ);
        }
    }

    private static void ExemptLegacyCraftFromCrew(JsonObject root, PartCatalog catalog)
    {
        foreach (var craft in Crafts(root)) craft["legacyNoCrew"] = true;
    }

    private static void DropLandingSites(JsonObject root, PartCatalog catalog)
    {
        foreach (var craft in Crafts(root))
            foreach (var order in (craft["buildQueue"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                order.Remove("landingSiteBodyId");
    }

    private static void KeepLegacyStationsUnlimited(JsonObject root, PartCatalog catalog)
    {
        foreach (var craft in Crafts(root))
        {
            var parts = Parts(craft, catalog).ToList();
            var flies = parts.Any(p => p.ThrustN > 0);
            var storage = parts.Sum(p => p.CargoCapacityKg);
            if (!flies && storage <= 0) craft["legacyUnlimitedStorage"] = true;
        }
    }
}
