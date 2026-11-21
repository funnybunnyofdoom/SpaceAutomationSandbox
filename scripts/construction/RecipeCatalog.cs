using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpaceAutomationSandbox.Construction;

/// <summary>
/// One refining recipe from data/recipes/recipes.json: whole-kg inputs become whole-kg outputs.
/// Inputs and outputs must weigh the same (waste such as slag is an explicit output), so refining
/// never creates or loses mass.
/// </summary>
public sealed record RecipeDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, long>? InputsKg { get; init; }
    public Dictionary<string, long>? OutputsKg { get; init; }

    public long BatchKg => InputsKg?.Values.Sum() ?? 0;
}

/// <summary>All recipes, in file order (a refinery tries them in this order).</summary>
public sealed class RecipeCatalog
{
    public const int SchemaVersion = 1;

    public static readonly RecipeCatalog Empty = new(Array.Empty<RecipeDefinition>(), new MaterialCatalog(Array.Empty<MaterialDefinition>()));

    private readonly List<RecipeDefinition> _recipes = new();
    private readonly Dictionary<string, RecipeDefinition> _byId = new(StringComparer.Ordinal);

    public RecipeCatalog(IEnumerable<RecipeDefinition> recipes, MaterialCatalog materials)
    {
        foreach (var recipe in recipes)
        {
            if (string.IsNullOrWhiteSpace(recipe.Id)) throw new FormatException("Recipe with empty id.");
            Check(recipe, recipe.InputsKg, "inputsKg", materials);
            Check(recipe, recipe.OutputsKg, "outputsKg", materials);
            var inKg = recipe.InputsKg!.Values.Sum();
            var outKg = recipe.OutputsKg!.Values.Sum();
            if (inKg != outKg)
                throw new FormatException($"Recipe '{recipe.Id}' takes {inKg} kg but gives {outKg} kg; add the difference as waste (e.g. slag).");
            if (!_byId.TryAdd(recipe.Id, recipe)) throw new FormatException($"Duplicate recipe id '{recipe.Id}'.");
            _recipes.Add(recipe);
        }
    }

    private static void Check(RecipeDefinition recipe, Dictionary<string, long>? amounts, string field, MaterialCatalog materials)
    {
        if (amounts is not { Count: > 0 }) throw new FormatException($"Recipe '{recipe.Id}' needs {field}.");
        foreach (var (id, kg) in amounts)
        {
            if (!materials.Contains(id)) throw new FormatException($"Recipe '{recipe.Id}' uses unknown material '{id}'.");
            if (kg <= 0) throw new FormatException($"Recipe '{recipe.Id}': {field} '{id}' must be > 0 kg.");
        }
    }

    public IReadOnlyList<RecipeDefinition> Recipes => _recipes;

    public bool TryGet(string id, out RecipeDefinition recipe) => _byId.TryGetValue(id, out recipe!);

    public static RecipeCatalog FromJson(string json, MaterialCatalog materials)
    {
        var file = JsonSerializer.Deserialize<RecipeFile>(json, PartCatalog.JsonOptions)
                   ?? throw new FormatException("Recipe file is empty.");
        if (file.SchemaVersion != SchemaVersion)
            throw new FormatException($"Recipe file schemaVersion {file.SchemaVersion}, expected {SchemaVersion}.");
        return new RecipeCatalog(file.Recipes ?? new List<RecipeDefinition>(), materials);
    }

    private sealed class RecipeFile
    {
        public int SchemaVersion { get; set; }
        public List<RecipeDefinition>? Recipes { get; set; }
    }
}
