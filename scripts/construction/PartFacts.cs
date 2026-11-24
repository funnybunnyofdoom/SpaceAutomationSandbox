using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SpaceAutomationSandbox.Construction;

/// <summary>One thing a part does, for its tooltip: an interface icon and a line of text.</summary>
public readonly record struct PartFact(string Icon, string Text);

/// <summary>What a part does, in words, from its data. Every part has at least one line.</summary>
public static class PartFacts
{
    public static IReadOnlyList<PartFact> Of(PartDefinition p, MaterialCatalog materials)
    {
        var facts = new List<PartFact>();
        void Add(bool when, string icon, string text) { if (when) facts.Add(new(icon, text)); }
        string Name(string id) => materials.Contains(id) ? materials.Get(id).Name.ToLowerInvariant() : id;
        string Mix(IEnumerable<KeyValuePair<string, double>> amounts, string unit) => string.Join(" + ", amounts.Select(a => $"{N(a.Value)} {unit} {Name(a.Key)}"));

        Add(p.IsControlSource, "crew", "Control: a ship needs one to be flown or automated.");
        Add(p.ThrustN > 0, "speed", $"Thrust {N(p.ThrustN / 1000)} kN, the way its arrow points.");
        if (p.PropellantKgPerS is { Count: > 0 } burns) Add(true, "mat_" + burns.Keys.First(), $"Burns {Mix(burns, "kg/s")} at full thrust.");
        Add(p.CargoCapacityKg > 0, "cargo", $"Holds {N(p.CargoCapacityKg / 1000)} t.");
        Add(p.EnergyCapacityKJ > 0, "battery", $"Stores {N(p.EnergyCapacityKJ / 1000)} MJ.");
        Add(p.PowerGenerationKW > 0, "power", $"Solar power: {N(p.PowerGenerationKW)} kW at Home's sunlight.");
        if (p.GeneratorKW > 0)
        {
            Add(true, "power", $"Makes up to {N(p.GeneratorKW)} kW, only what solar and batteries can't cover.");
            Add(p.GeneratorBurnsKg is { Count: > 0 }, "mat_" + (p.GeneratorBurnsKg?.Keys.FirstOrDefault() ?? ""), $"Burns {Mix(p.GeneratorBurnsKg ?? new(), "kg")} for {N(p.GeneratorKJPerBatch / 1000)} MJ.");
            Add(p.GeneratorMakesKg is { Count: > 0 }, "mat_" + (p.GeneratorMakesKg?.Keys.FirstOrDefault() ?? ""), $"Leaves {Mix(p.GeneratorMakesKg ?? new(), "kg")}.");
        }
        Add(p.GeothermalKW > 0, "power", $"Makes {N(p.GeothermalKW)} kW anchored on a hot spot, or on a body near its star.");
        Add(p.StellarHeatKW > 0, "star", $"Makes up to {N(p.StellarHeatKW)} kW close to a star: none below twice Home's sunlight, full at four times.");
        if (p.ConverterKW > 0)
        {
            Add(true, "work", $"Turns {Mix(p.ConverterInputsKg ?? new(), "kg")} into {Mix(p.ConverterOutputsKg ?? new(), "kg")}, for {N(p.ConverterKJPerBatch / 1000)} MJ.");
            Add(true, "power", $"Uses up to {N(p.ConverterKW)} kW, only spare power.");
        }
        Add(p.MiningRateKgPerS > 0, "mining", $"Mines {N(p.MiningRateKgPerS)} kg/s while landed.");
        Add(p.RefiningRateKgPerS > 0, "work", $"Refines {N(p.RefiningRateKgPerS)} kg/s of ore into materials.");
        Add(p.FabricationRateKgPerS > 0, "shipyard", $"Builds ships at {N(p.FabricationRateKgPerS)} kg/s: makes a station a shipyard.");
        Add(p.EjectionRateKgPerS > 0, "mat_slag", $"Throws out waste at {N(p.EjectionRateKgPerS)} kg/s.");
        Add(p.CrewCapacity > 0, "crew", $"Houses {p.CrewCapacity} crew, who need food and water.");
        Add(p.RobotCount > 0, "crew", $"{p.RobotCount} robots: work like crew, need only power.");
        Add(p.FoodRateKgPerS > 0, "mat_food", $"Grows {N(p.FoodRateKgPerS * 3600)} kg of food an hour, from the same weight of water.");
        Add(p.ResearchRatePerS > 0, "research", $"Makes {N(p.ResearchRatePerS * 60)} research points a minute, from power and electronics.");
        Add(p.IsDockingPort, "docked", "One berth: a ship docks where its arrow points.");
        Add(p.IsAnchorClamp, "landed", "Holds a landed craft to the ground, and lets go again.");
        Add(p.IsFoundation, "landed", "Anchors for good where it lands: stations only.");
        Add(p.IsWarpGate, "gate", "Jumps ships to another star system. The station needs a docking port.");
        Add(p.IsGateLock, "gate", "Lets you close the warp gate it's on (Fleet panel).");
        Add(p.IsGateDefense, "weapon", "Defends a warp gate.");
        if (p.Weapon is { } w)
        {
            Add(true, "weapon", $"{(w.PointDefense ? "Point defense: shoots missiles first" : w.Kind.ToString())}, range {(w.RangeM >= 1000 ? $"{N(w.RangeM / 1000)} km" : $"{N(w.RangeM)} m")}, {(w.ArcDeg >= 360 ? "all round" : $"{N(w.ArcDeg)}° arc")}.");
            Add(true, "danger", $"Damage {N(w.DamagePerShot)} every {N(w.IntervalS)} s ({N(w.DamagePerShot / w.IntervalS)} a second; an ordinary part takes 1).");
            Add(w.EnergyKJPerShot > 0, "battery", $"{N(w.EnergyKJPerShot)} kJ a shot.");
            if (w.AmmoMaterial is { } ammo) Add(true, "mat_" + ammo, $"{w.AmmoKgPerShot} kg of {Name(ammo)} a shot.");
            Add(w.NeedsArmedGates, "laws", "Fires only with the Armed gates law.");
        }
        Add(p.IsArmour, "hull", "Armour: far-off hits land on it first.");
        Add(p.Toughness > 1, "hull", $"Takes {N(p.Toughness)} ordinary parts' worth of damage.");
        Add(p.ShieldCapacity > 0, "shield", $"Shield: takes {N(p.ShieldCapacity)} parts' worth of damage, full again in {N(p.ShieldCapacity / Math.Max(p.ShieldRechargePerS, 1e-9))} s.");
        Add(p.SalvageRateKgPerS > 0, "salvage", $"Cuts up wrecks within reach at {N(p.SalvageRateKgPerS)} kg/s.");
        Add(p.RepairRateKgPerS > 0, "hull", $"Repairs this craft and craft docked to it at {N(p.RepairRateKgPerS)} kg/s.");
        Add(p.NanoRepairKgPerS > 0, "hull", $"Nanites repair this craft at {N(p.NanoRepairKgPerS)} kg/s and rebuild lost parts.");
        Add(p.HarvestRateKgPerS > 0, "life", $"Harvests native plants at {N(p.HarvestRateKgPerS)} kg/s.");
        Add(p.IsRepeller, "life", "Keeps native animals off the site it's anchored on.");
        Add(p.RanchRateKgPerS > 0, "mat_food", $"Takes native animals as food at {N(p.RanchRateKgPerS)} kg/s.");
        Add(p.PenCapacityKg > 0, "life", $"Carries up to {N(p.PenCapacityKg / 1000)} t of live animals to another world.");
        Add(p.AntimatterCapacityKg > 0, "mat_antimatter", $"Contains {N(p.AntimatterCapacityKg)} kg of antimatter. It explodes if the power fails for long.");
        Add(p.IsQuantumDrive, "speed", "Charges, then jumps to its target within the star system.");
        if (facts.Count == 0) facts.Add(new("hull", "Joins parts together."));
        Add(p.PowerUseKW > 0, "power", $"Uses {N(p.PowerUseKW)} kW while working.");
        return facts;
    }

    private static string N(double value) => value.ToString("#,0.####", CultureInfo.InvariantCulture);
}
