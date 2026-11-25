using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>What a production chain is short of (#121), worst first in a plan.</summary>
public enum PlanLimit { Survey, Miners, Refineries, Freight, Storage, Power, Fabrication, Unobtainable }

/// <summary>One material in a plan's tree: what's needed (kg, or kg an hour for a rate), what's in stock, and what's made an hour now.</summary>
public sealed record PlanNode(string Material, double Need, long StockKg, double MadePerHour, string How, IReadOnlyList<PlanNode> Inputs)
{
    /// <summary>For a rate: the share of it that's made now, 0..1. For a design: 1 when the stock covers it.</summary>
    public double Share { get; init; }
}

/// <summary>Something that holds a plan back, why, and where to go about it.</summary>
public sealed record PlanIssue(PlanLimit Limit, string Material, string Text, string Suggestion, TargetRef Target, CraftId From = default, CraftId To = default);

/// <summary>A plan: the tree, what holds it back, and how long it takes as things are and with the first issue dealt with (hours; null: never).</summary>
public sealed record ProductionPlan(IReadOnlyList<PlanNode> Tree, IReadOnlyList<PlanIssue> Issues, double? Hours, double? HoursWithFix, string Fix, double FabricationHours);

/// <summary>
/// The production planner (#121; the creator's choices: it advises, and each suggestion goes where it can be done; it looks at a system or
/// everything; goals are a design to build or a material rate). It's an estimate from the same numbers the simulation uses: drill, refinery
/// and fabricator rates with the debug multipliers and race traits, cargo and distance for miners' round trips, power shortages, and where
/// ore lies. Each refined material comes from the recipe that gives the most of it. Read-only: it changes nothing.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Docking, landing and unloading on each mining trip, beyond the drilling and the flight.</summary>
    public const double MiningTripOverheadS = 420; // measured against the simulation (PlannerTests)

    private sealed class PlanScope
    {
        public readonly List<CraftState> Stations = new(), Miners = new(), Refineries = new(), Yards = new();
        public readonly Dictionary<string, long> Stock = new();
        public readonly Dictionary<string, double> MinedPerHour = new();
        public readonly Dictionary<string, int> MinerCount = new();
        public double RefiningPerHour, FabricationPerHour;
        public readonly List<(PlanLimit Limit, string Material)> Extra = new();
    }

    private PlanScope ScopeOf(BodyId? star)
    {
        var scope = new PlanScope();
        foreach (var craft in _crafts)
        {
            if (!IsPlayers(craft) || craft.IsWreck || star is { } only && StarAt(craft.Position)?.Id != only) continue;
            if (!Movable(craft) || craft.IsShipyard || craft.Stats.TotalThrustN <= 0)
            {
                scope.Stations.Add(craft);
                foreach (var (material, kg) in craft.Inventory.Kg) scope.Stock[material] = scope.Stock.GetValueOrDefault(material) + kg;
            }
            if (craft.Stats.RefiningRateKgPerS > 0)
            {
                scope.Refineries.Add(craft);
                scope.RefiningPerHour += craft.Stats.RefiningRateKgPerS * Tuning.RefiningRateMultiplier * Trait(craft, _refining) * 3600 * (craft.PowerShort ? 0.5 : 1);
            }
            if (craft.Stats.FabricationRateKgPerS > 0 && craft.IsShipyard)
            {
                scope.Yards.Add(craft);
                scope.FabricationPerHour += craft.Stats.FabricationRateKgPerS * Tuning.FabricationRateMultiplier * Trait(craft, _building) * 3600;
            }
            if (craft.Flown is MiningJob { Active: true } mining && _byId.TryGetValue(mining.SourceBodyId, out var source) && _craftsById.TryGetValue(mining.DestinationId, out var to))
            {
                scope.Miners.Add(craft);
                scope.MinedPerHour[mining.MaterialId] = scope.MinedPerHour.GetValueOrDefault(mining.MaterialId) + MinerPerHour(craft.Stats, source, to, mining.ReturnFraction, craft);
                scope.MinerCount[mining.MaterialId] = scope.MinerCount.GetValueOrDefault(mining.MaterialId) + 1;
            }
        }
        foreach (var swarm in _swarms)
            if (_byId.TryGetValue(swarm.SourceBodyId, out var source) && _craftsById.TryGetValue(swarm.DestinationId, out var to) && (star is null || StarAt(to.Position)?.Id == star))
            {
                scope.MinedPerHour[swarm.MaterialId] = scope.MinedPerHour.GetValueOrDefault(swarm.MaterialId) + swarm.Count * MinerPerHour(swarm.Stats, source, to, swarm.ReturnFraction, null);
                scope.MinerCount[swarm.MaterialId] = scope.MinerCount.GetValueOrDefault(swarm.MaterialId) + swarm.Count;
            }
        return scope;
    }

    /// <summary>Kg an hour a miner brings: a hold's worth per round trip of drilling, flying both ways and docking.</summary>
    private double MinerPerHour(ShipStats stats, CelestialBodyState source, CraftState to, double returnFraction, CraftState? craft)
    {
        var drill = stats.MiningRateKgPerS * Tuning.DrillRateMultiplier * (craft is null ? _mining : Trait(craft, _mining));
        if (drill <= 0 || stats.CargoCapacityKg <= 0) return 0;
        var load = stats.CargoCapacityKg * Math.Clamp(returnFraction, 0.05, 1);
        var distance = Math.Max(0, GetPosition(source.Id).DistanceTo(to.Position) - source.RadiusM);
        var flight = distance > AutopilotCruiseMinDistanceM ? (distance - AutopilotCruiseArriveM) / CruiseSpeedMps + AutopilotCruiseArriveM / JobCruiseSpeedMps + 60 : distance / JobCruiseSpeedMps;
        return load / (load / drill + 2 * flight + MiningTripOverheadS) * 3600;
    }

    /// <summary>The recipe that gives the most of a material (by share of its output), first in the file on a tie; null for ores.</summary>
    public RecipeDefinition? RecipeFor(string material)
    {
        if (IsByProduct(material)) return null;
        RecipeDefinition? best = null;
        var share = 0.0;
        foreach (var recipe in Recipes.Recipes)
            if (recipe.OutputsKg!.TryGetValue(material, out var kg) && (double)kg / recipe.BatchKg is var s && (s > share + 1e-12 || best is { OnRequest: true } && !recipe.OnRequest && s >= share))
                (best, share) = (recipe, s);
        return best;
    }

    /// <summary>Something every recipe makes beside something worth more (slag): made by refining anything, not by a chain of its own.</summary>
    public bool IsByProduct(string material)
    {
        if (Parts is not { } parts || !parts.Materials.Contains(material)) return false;
        var value = parts.Materials.Get(material).Value;
        var any = false;
        foreach (var recipe in Recipes.Recipes)
        {
            if (!recipe.OutputsKg!.ContainsKey(material)) continue;
            any = true;
            if (!recipe.OutputsKg.Keys.Any(o => o != material && parts.Materials.Contains(o) && parts.Materials.Get(o).Value > value)) return false;
        }
        return any;
    }

    /// <summary>What the stations and miners in scope do an hour: kg mined by material, refinery input and fabrication.</summary>
    public (IReadOnlyDictionary<string, double> MinedPerHour, double RefiningPerHour, double FabricationPerHour) PlanRates(BodyId? star)
    {
        var scope = ScopeOf(star);
        return (scope.MinedPerHour, scope.RefiningPerHour, scope.FabricationPerHour);
    }

    /// <summary>A plan for a design (the first of them, at the yards in scope).</summary>
    public ProductionPlan PlanDesign(ShipDesign design, BodyId? star) =>
        Plan(design.ComputeBillOfMaterials(Parts!).ToWholeKg().ToDictionary(kv => kv.Key, kv => (double)kv.Value), perHour: false, star);

    /// <summary>A plan for keeping up so many kg an hour of a material.</summary>
    public ProductionPlan PlanRate(string material, double kgPerHour, BodyId? star) =>
        Plan(new Dictionary<string, double> { [material] = kgPerHour }, perHour: true, star);

    private ProductionPlan Plan(Dictionary<string, double> goal, bool perHour, BodyId? star)
    {
        var plan = Build(goal, perHour, ScopeOf(star));
        if (plan.Issues.FirstOrDefault(i => i.Limit is PlanLimit.Miners or PlanLimit.Refineries or PlanLimit.Fabrication) is not { } first) return plan;
        var better = ScopeOf(star);
        better.Extra.Add((first.Limit, first.Material));
        var fixedPlan = Build(goal, perHour, better);
        var fix = first.Limit switch { PlanLimit.Miners => $"one more miner on {Named(first.Material)}", PlanLimit.Refineries => "one more refinery", _ => "one more fabricator" };
        var after = perHour ? fixedPlan.Tree.Min(n => n.Share) * 100 : fixedPlan.Hours;
        return plan with { HoursWithFix = after, Fix = fix };
    }

    private ProductionPlan Build(Dictionary<string, double> goal, bool perHour, PlanScope scope)
    {
        var parts = Parts!;
        var miner = parts.TryGet("drill", out var drill) ? drill : null;
        foreach (var (limit, material) in scope.Extra)
            switch (limit)
            {
                case PlanLimit.Miners: // a miner like the ones on it, or a starter one
                    var like = scope.Miners.FirstOrDefault(m => m.Flown is MiningJob j && j.MaterialId == material);
                    var rate = like is not null && scope.MinerCount.GetValueOrDefault(material) > 0 ? scope.MinedPerHour.GetValueOrDefault(material) / scope.MinerCount[material]
                        : (miner?.MiningRateKgPerS ?? 2) * Tuning.DrillRateMultiplier * _mining * 3600 * 0.4;
                    scope.MinedPerHour[material] = scope.MinedPerHour.GetValueOrDefault(material) + rate;
                    break;
                case PlanLimit.Refineries: scope.RefiningPerHour += (parts.TryGet("refinery", out var r) ? r.RefiningRateKgPerS : 2) * Tuning.RefiningRateMultiplier * _refining * 3600; break;
                case PlanLimit.Fabrication: scope.FabricationPerHour += (parts.TryGet("fabricator", out var f) ? f.FabricationRateKgPerS : 1) * Tuning.FabricationRateMultiplier * _building * 3600; break;
            }

        // What's to be made, by material: for a design, what the stock doesn't cover (down the chain); for a rate, all of it.
        var missing = new Dictionary<string, double>();
        var stockLeft = scope.Stock.ToDictionary(kv => kv.Key, kv => (double)kv.Value);
        void Need(string material, double amount, int depth)
        {
            if (amount <= 0 || depth > 8) return;
            if (!perHour)
            {
                var have = stockLeft.GetValueOrDefault(material);
                var used = Math.Min(have, amount);
                stockLeft[material] = have - used;
                amount -= used;
                if (amount <= 0) return;
            }
            missing[material] = missing.GetValueOrDefault(material) + amount;
            if (RecipeFor(material) is not { } recipe) return;
            var batches = amount / recipe.OutputsKg![material];
            foreach (var (input, kg) in recipe.InputsKg!) Need(input, batches * kg, depth + 1);
        }
        foreach (var (material, amount) in goal) Need(material, amount, 0);

        // Refinery hours: input kg of every batch to run, against what the refineries do an hour.
        var refineKg = missing.Where(m => RecipeFor(m.Key) is not null).Sum(m => m.Value / RecipeFor(m.Key)!.OutputsKg![m.Key] * RecipeFor(m.Key)!.BatchKg);
        var refineShare = refineKg <= 0 ? 1 : scope.RefiningPerHour <= 0 ? 0 : perHour ? Math.Min(1, scope.RefiningPerHour / refineKg) : 1;
        var refineHours = refineKg <= 0 ? 0 : scope.RefiningPerHour <= 0 ? double.PositiveInfinity : refineKg / scope.RefiningPerHour;

        // Each material's time (design) or share (rate), from its ore up.
        var memo = new Dictionary<string, (double Hours, double Share, double Made)>();
        (double Hours, double Share, double Made) Supply(string material, int depth)
        {
            if (memo.TryGetValue(material, out var known)) return known;
            var need = missing.GetValueOrDefault(material);
            (double, double, double) result;
            if (IsByProduct(material))
            {
                var made = scope.RefiningPerHour * 0.4;
                result = need <= 0 ? (0, 1, made) : made <= 0 ? (double.PositiveInfinity, 0, 0) : (need / made, Math.Min(1, made / need), made);
            }
            else if (RecipeFor(material) is not { } recipe || depth > 8)
            {
                var mined = scope.MinedPerHour.GetValueOrDefault(material);
                result = need <= 0 ? (0, 1, mined) : mined <= 0 ? (double.PositiveInfinity, 0, 0) : (need / mined, Math.Min(1, mined / need), mined);
            }
            else
            {
                var hours = 0.0;
                var share = refineShare;
                foreach (var input in recipe.InputsKg!.Keys)
                {
                    var (h, s, _) = Supply(input, depth + 1);
                    hours = Math.Max(hours, h);
                    share = Math.Min(share, s);
                }
                hours = need <= 0 ? 0 : Math.Max(hours, refineHours);
                result = (hours, need <= 0 ? 1 : share, need * share);
            }
            memo[material] = result;
            return result;
        }

        PlanNode Node(string material, double amount, int depth)
        {
            var (hours, share, made) = Supply(material, depth);
            var recipe = depth <= 8 ? RecipeFor(material) : null;
            var toMake = perHour ? amount : Math.Min(amount, missing.GetValueOrDefault(material));
            var inputs = recipe is null || toMake <= 0 ? new List<PlanNode>()
                : recipe.InputsKg!.Select(i => Node(i.Key, toMake / recipe.OutputsKg![material] * i.Value, depth + 1)).ToList();
            var how = IsByProduct(material) ? "a by-product of refining" : recipe is null ? scope.MinedPerHour.ContainsKey(material) ? $"mined: {scope.MinedPerHour[material] / 1000:0.#} t/h" : "mined: no miners on it"
                : $"{recipe.Name.ToLowerInvariant()}";
            return new PlanNode(material, amount, scope.Stock.GetValueOrDefault(material), recipe is null ? scope.MinedPerHour.GetValueOrDefault(material) : made, how, inputs)
            {
                Share = perHour ? share : missing.GetValueOrDefault(material) <= 0 ? 1 : double.IsInfinity(hours) ? 0 : 1 - Math.Min(1, missing[material] / Math.Max(amount, 1)),
            };
        }
        var tree = goal.Select(g => Node(g.Key, g.Value, 0)).ToList();

        // What holds it back, worst first.
        var issues = new List<PlanIssue>();
        foreach (var (material, amount) in missing.OrderByDescending(m => Supply(m.Key, 0).Hours).ThenBy(m => m.Key, StringComparer.Ordinal))
        {
            if (RecipeFor(material) is not null || IsByProduct(material)) continue;
            var mined = scope.MinedPerHour.GetValueOrDefault(material);
            var (hours, share, _) = Supply(material, 0);
            var where = SurveyedSource(material, scope);
            if (mined <= 0)
            {
                if (where is null)
                {
                    var unsurveyed = _bodies.Where(b => b.IsMineable && !b.Surveyed && !b.Unscanned && b.Id.Value < CometBodyIdBase)
                        .OrderBy(b => _crafts.FirstOrDefault(c => c.Id == PilotCraftId) is { } p ? GetPosition(b.Id).DistanceTo(p.Position) : 0).FirstOrDefault();
                    issues.Add(new(PlanLimit.Survey, material, $"No surveyed {Named(material)} {(scope.Stations.Count > 0 ? "within reach" : "anywhere")}.",
                        unsurveyed is null ? "Look in other systems." : $"Survey {unsurveyed.Name} and others like it.", unsurveyed is null ? TargetRef.None : TargetRef.Of(unsurveyed)));
                }
                else issues.Add(new(PlanLimit.Miners, material, $"Nothing mines {Named(material)}.", $"Start a mining job at {where.Name}.", TargetRef.Of(where)));
                continue;
            }
            if (perHour ? share < 0.999 : hours > 0.25)
                issues.Add(new(PlanLimit.Miners, material, perHour ? $"Miners bring {mined / 1000:0.#} t/h of {Named(material)}; {amount / 1000:0.#} t/h are needed." : $"{Named(material)}: {amount / 1000:0.#} t to mine at {mined / 1000:0.#} t/h.",
                    $"Put {Math.Max(1, (int)Math.Ceiling(amount / Math.Max(mined / Math.Max(1, scope.MinerCount.GetValueOrDefault(material)), 1)) - scope.MinerCount.GetValueOrDefault(material))} more miners on it.", where is null ? TargetRef.None : TargetRef.Of(where)));
        }
        if (refineKg > 0 && (scope.RefiningPerHour <= 0 || (perHour ? refineShare < 0.999 : refineHours > 0.25)))
            issues.Add(new(PlanLimit.Refineries, missing.Keys.FirstOrDefault(m => RecipeFor(m) is not null) ?? "", scope.RefiningPerHour <= 0 ? "No refinery here."
                : $"Refineries do {scope.RefiningPerHour / 1000:0.#} t/h; {refineKg / 1000:0.#} t{(perHour ? "/h" : "")} of ore {(perHour ? "are" : "is")} to refine.", "Add refineries to a station (a refit at the shipyard).",
                scope.Refineries.FirstOrDefault() is { } refinery ? TargetRef.Of(refinery) : scope.Yards.FirstOrDefault() is { } yard ? TargetRef.Of(yard) : TargetRef.None));
        // Ore piling up where nothing refines it, with no freighter taking it away.
        foreach (var station in scope.Stations.Where(s => s.Stats.RefiningRateKgPerS <= 0))
            foreach (var material in missing.Keys.Where(m => RecipeFor(m) is null && station.Inventory[m] >= 5_000))
                if (scope.Refineries.FirstOrDefault() is { } refinery && !_crafts.Any(c => c.Job is HaulJob { Active: true } h && h.PickupId == station.Id && h.MaterialId == material))
                    issues.Add(new(PlanLimit.Freight, material, $"{station.Inventory[material] / 1000:0} t of {Named(material)} wait at {station.Name}, which can't refine.", $"Send a freighter to {refinery.Name}.", TargetRef.Of(station), station.Id, refinery.Id));
        foreach (var refinery in scope.Refineries.Where(r => r.HasCargoLimit && r.CargoCapacityKg > 0 && r.CargoKg >= 0.95 * r.CargoCapacityKg))
            issues.Add(new(PlanLimit.Storage, "", $"{refinery.Name} is full: refined goods have nowhere to go.", "Move goods out or add storage.", TargetRef.Of(refinery)));
        foreach (var station in scope.Refineries.Concat(scope.Yards).Distinct().Where(c => c.PowerShort && !c.LegacyFreePower))
            issues.Add(new(PlanLimit.Power, "", $"{station.Name} is short of power: it works slower.", "Add solar panels, batteries or a generator.", TargetRef.Of(station)));
        foreach (var material in missing.Keys.Where(m => RecipeFor(m) is null && scope.MinedPerHour.GetValueOrDefault(m) <= 0 && !_bodies.Any(b => b.Deposit(m) > 0)))
        {
            var at = issues.FindIndex(i => i.Material == material && i.Limit == PlanLimit.Survey);
            if (at >= 0 && _bodies.All(b => b.Surveyed || !b.IsMineable)) issues[at] = new(PlanLimit.Unobtainable, material, $"No {Named(material)} is known anywhere.", "Open new star systems through warp gates.", TargetRef.None);
        }

        var fabrication = perHour ? 0 : scope.FabricationPerHour <= 0 ? double.PositiveInfinity : goal.Values.Sum() / scope.FabricationPerHour;
        if (!perHour && scope.FabricationPerHour <= 0) issues.Add(new(PlanLimit.Fabrication, "", "No shipyard here to build it.", "Build or deploy a shipyard.", TargetRef.None));
        double? total = perHour ? null : missing.Keys.Select(m => Supply(m, 0).Hours).DefaultIfEmpty(0).Max() + fabrication;
        if (total is { } t && double.IsInfinity(t)) total = null;
        return new ProductionPlan(tree, issues.OrderBy(i => i.Limit == PlanLimit.Power ? 2 : i.Limit is PlanLimit.Storage or PlanLimit.Freight ? 1 : 0).ToList(), total, null, "", fabrication);
    }

    /// <summary>The nearest body with a surveyed deposit of an ore, to a station in scope.</summary>
    private CelestialBodyState? SurveyedSource(string material, PlanScope scope)
    {
        var from = scope.Stations.FirstOrDefault()?.Position;
        return _bodies.Where(b => b.SurveyedDeposit(material) > 0 && (from is null || StarAt(GetPosition(b.Id))?.Id == StarAt(from.Value)?.Id))
            .OrderBy(b => from is { } at ? GetPosition(b.Id).DistanceTo(at) : 0).ThenBy(b => b.Id.Value).FirstOrDefault();
    }
}
