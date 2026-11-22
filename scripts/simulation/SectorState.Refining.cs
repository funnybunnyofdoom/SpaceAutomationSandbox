using System;
using System.Collections.Generic;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>A refining batch in progress: its inputs are held here (out of the inventory) until outputs appear.</summary>
public sealed class RefiningBatch
{
    public RefiningBatch(string recipeId, IReadOnlyDictionary<string, long> inputsKg, double progressKg = 0)
    {
        RecipeId = recipeId;
        InputsKg = new SortedDictionary<string, long>(new Dictionary<string, long>(inputsKg), StringComparer.Ordinal);
        ProgressKg = progressKg;
    }

    public string RecipeId { get; }
    public IReadOnlyDictionary<string, long> InputsKg { get; }
    public double ProgressKg { get; internal set; }
}

/// <summary>
/// Refineries: a craft with refinery parts runs one batch at a time. A batch starts with the first
/// recipe (file order) whose inputs are all in stock; the inputs leave the inventory then. It
/// progresses at the refinery rate (kg of input per second), and on completion its outputs, of
/// exactly the same total mass, enter the inventory. If they don't fit, the batch waits.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Recipes for refineries (game definitions, not saved). Set when a game is created or loaded.</summary>
    public RecipeCatalog Recipes { get; set; } = RecipeCatalog.Empty;

    /// <summary>Part definitions (game definitions, not saved), for per-part rules such as anchoring. Set with <see cref="Recipes"/>.</summary>
    public PartCatalog? Parts { get; set; }

    private PartDefinition? Catalog(string partId) => Parts is { } parts && parts.TryGet(partId, out var part) ? part : null;

    private void StepRefining()
    {
        foreach (var craft in _crafts)
        {
            if (craft.Stats.RefiningRateKgPerS <= 0) continue;

            if (craft.RefiningBatch is null)
            {
                foreach (var recipe in Recipes.Recipes)
                {
                    if (craft.Inventory.Shortfall(recipe.InputsKg!).Count > 0) continue;
                    craft.Inventory.TryRemove(recipe.InputsKg!);
                    craft.RefiningBatch = new RefiningBatch(recipe.Id, recipe.InputsKg!);
                    break;
                }
                if (craft.RefiningBatch is null) continue;
            }

            var batch = craft.RefiningBatch;
            if (!Recipes.TryGet(batch.RecipeId, out var running)) continue; // unknown after a data change: hold it untouched
            if (batch.ProgressKg < running.BatchKg && !Operational(craft)) continue;
            if (batch.ProgressKg < running.BatchKg && !TryUsePower(craft, craft.Stats.RefineryPowerKW * FixedStepClock.TickSeconds)) continue;
            batch.ProgressKg = Math.Min(running.BatchKg,
                batch.ProgressKg + craft.Stats.RefiningRateKgPerS * Tuning.RefiningRateMultiplier * FixedStepClock.TickSeconds);
            if (batch.ProgressKg < running.BatchKg) continue;

            long outputKg = 0;
            foreach (var kg in running.OutputsKg!.Values) outputKg += kg;
            if (craft.FreeCargoKg < outputKg) continue; // finished but no room: wait until stock is taken away
            craft.Inventory.Add(running.OutputsKg!);
            craft.RefiningBatch = null;
        }
    }

    /// <summary>Human-readable refinery state, for HUD and panels.</summary>
    public string RefiningStatus(CraftState craft)
    {
        if (craft.Stats.RefiningRateKgPerS <= 0) return "";
        if (craft.RefiningBatch is not { } batch) return "refinery idle (no recipe inputs in stock)";
        var name = Recipes.TryGet(batch.RecipeId, out var r) ? r.Name : batch.RecipeId;
        var total = r?.BatchKg ?? 1;
        if (batch.ProgressKg >= total) return $"{name}: done, waiting for storage room";
        return $"{name} {batch.ProgressKg / total * 100:0}%" + (craft.PowerShort && !craft.LegacyFreePower ? " (no power)" : "");
    }
}
