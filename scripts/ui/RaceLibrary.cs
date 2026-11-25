using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Finds the races (#116): every folder in data/races, with its race.json, portrait and, if it has them, ship.json and shipyard.json.</summary>
public static class RaceLibrary
{
    public const string Dir = "res://data/races";

    public static RaceCatalog Load(PartCatalog catalog)
    {
        var sources = new List<RaceSource>();
        if (DirAccess.DirExistsAbsolute(Dir))
            foreach (var folder in DirAccess.GetDirectoriesAt(Dir).OrderBy(f => f, System.StringComparer.Ordinal))
                sources.Add(new RaceSource(folder, Read($"{Dir}/{folder}/race.json"), Read($"{Dir}/{folder}/ship.json"), Read($"{Dir}/{folder}/shipyard.json")));
        var races = RaceCatalog.Load(sources, catalog, Laws.All.Select(l => l.Id).ToList());
        foreach (var problem in races.Problems) GD.PushWarning($"Race not usable: {problem}");
        return races;
    }

    private static string? Read(string path) => FileAccess.FileExists(path) ? FileAccess.GetFileAsString(path) : null;

    /// <summary>Its portrait (4:5), or a plain one in its colour.</summary>
    public static Texture2D Portrait(RaceDefinition race)
    {
        foreach (var extension in new[] { "webp", "png" })
            if ($"{Dir}/{race.Id}/portrait.{extension}" is var path && ResourceLoader.Exists(path)) return GD.Load<Texture2D>(path);
        var accent = race.Accent.Length > 0 && Color.HtmlIsValid(race.Accent) ? new Color(race.Accent) : UiTheme.Cyan;
        var gradient = new Gradient();
        gradient.SetColor(0, accent);
        gradient.SetColor(1, accent.Darkened(0.92f));
        gradient.AddPoint(0.28f, accent.Darkened(0.35f));
        gradient.AddPoint(0.3f, accent.Darkened(0.8f));
        return new GradientTexture2D { Gradient = gradient, Width = 320, Height = 400, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.42f), FillTo = new Vector2(0.5f, 1.1f) };
    }
}
