using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpaceAutomationSandbox.Construction;

/// <summary>
/// Part tiers (#133, #134): a part <c>x</c> is Mk1 and <c>x_mk2</c> to <c>x_mk4</c> are its better tiers. Upgrading a design to the
/// best researched tiers (#141) and comparing a tier with the one before it on part cards (#142) both use this.
/// </summary>
public static class PartTiers
{
    private static readonly Regex TierSuffix = new("_mk([2-9])$", RegexOptions.CultureInvariant);

    /// <summary>The Mk1 id and the tier of a part id: "drill_mk3" is ("drill", 3), "drill" is ("drill", 1).</summary>
    public static (string Base, int Tier) Split(string partId)
    {
        var m = TierSuffix.Match(partId);
        return m.Success ? (partId[..m.Index], m.Groups[1].Value[0] - '0') : (partId, 1);
    }

    public static string IdOf(string baseId, int tier) => tier <= 1 ? baseId : $"{baseId}_mk{tier}";

    /// <summary>The tiers of the part's family that exist, Mk1 first.</summary>
    public static IReadOnlyList<PartDefinition> TiersOf(PartCatalog catalog, string partId)
    {
        var (baseId, _) = Split(partId);
        var tiers = new List<PartDefinition>();
        for (var tier = 1; tier <= 9; tier++)
            if (catalog.TryGet(IdOf(baseId, tier), out var part)) tiers.Add(part);
        return tiers;
    }

    /// <summary>The tier just below this part, or null for Mk1 and parts without tiers.</summary>
    public static PartDefinition? Previous(PartCatalog catalog, string partId)
    {
        var (baseId, tier) = Split(partId);
        for (var t = tier - 1; t >= 1; t--)
            if (catalog.TryGet(IdOf(baseId, t), out var part)) return part;
        return null;
    }

    /// <summary>The highest tier above this part that <paramref name="unlocked"/> allows, or null if it's the best there is now.</summary>
    public static string? BestUnlocked(PartCatalog catalog, string partId, Func<string, bool> unlocked)
    {
        var (baseId, tier) = Split(partId);
        for (var t = 9; t > tier; t--)
            if (IdOf(baseId, t) is var id && catalog.TryGet(id, out _) && unlocked(id)) return id;
        return null;
    }

    /// <summary>
    /// The design with every part swapped for its best researched tier, in the same place and turned the same way, and how many
    /// changed. The id and name stay: it's the same design, upgraded.
    /// </summary>
    public static (ShipDesign Design, int Changed) Upgraded(ShipDesign design, PartCatalog catalog, Func<string, bool> unlocked)
    {
        var changed = 0;
        var cells = design.Cells.Select(c =>
        {
            if (BestUnlocked(catalog, c.PartId, unlocked) is not { } better) return c;
            changed++;
            return c with { PartId = better };
        }).ToList();
        return (changed == 0 ? design : new ShipDesign(design.Id, design.Name, cells), changed);
    }

    /// <summary>What a tier gains over a lower one, in words: "Mining 250 kg/s (+50%)", one a line, the part's main numbers first.</summary>
    public static IReadOnlyList<string> Compare(PartDefinition from, PartDefinition to)
    {
        var lines = new List<string>();
        void Stat(string name, double a, double b, string unit, string format = "0.##")
        {
            if (a == 0 && b == 0) return;
            var change = a == 0 ? "new" : $"{(b - a) / a * 100:+0;-0}%";
            lines.Add($"{name} {b.ToString(format, CultureInfo.InvariantCulture)}{unit} ({change})");
        }
        Stat("Thrust", from.ThrustN / 1000, to.ThrustN / 1000, " kN");
        Stat("Mining", from.MiningRateKgPerS, to.MiningRateKgPerS, " kg/s");
        Stat("Power", from.PowerGenerationKW, to.PowerGenerationKW, " kW");
        Stat("Storage", from.EnergyCapacityKJ / 1000, to.EnergyCapacityKJ / 1000, " MJ");
        Stat("Cargo", from.CargoCapacityKg / 1000, to.CargoCapacityKg / 1000, " t");
        Stat("Refining", from.RefiningRateKgPerS, to.RefiningRateKgPerS, " kg/s");
        Stat("Fabrication", from.FabricationRateKgPerS, to.FabricationRateKgPerS, " kg/s");
        Stat("Crew room", from.CrewCapacity, to.CrewCapacity, "");
        Stat("Robots", from.RobotCount, to.RobotCount, "");
        Stat("Food", from.FoodRateKgPerS, to.FoodRateKgPerS, " kg/s", "0.####");
        Stat("Research", from.ResearchRatePerS, to.ResearchRatePerS, "/s", "0.###");
        Stat("Salvage", from.SalvageRateKgPerS, to.SalvageRateKgPerS, " kg/s");
        Stat("Repair", from.RepairRateKgPerS, to.RepairRateKgPerS, " kg/s");
        Stat("Shield", from.ShieldCapacity, to.ShieldCapacity, "");
        if (from.Weapon is { } wa && to.Weapon is { } wb) Stat("Damage", wa.DamagePerShot, wb.DamagePerShot, " a shot");
        if (Math.Abs(to.Toughness - from.Toughness) > 1e-9) Stat("Toughness", from.Toughness, to.Toughness, "");
        Stat("Power use", from.PowerUseKW, to.PowerUseKW, " kW");
        Stat("Mass", from.MassKg / 1000, to.MassKg / 1000, " t", "0.###");
        return lines;
    }
}
