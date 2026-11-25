using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>Finds the kinds of points of interest (#122): every folder in data/points, with its kind.json and, for derelicts, its designs.</summary>
public static class PointLibrary
{
    public const string Dir = "res://data/points";

    public static PointCatalog Load(PartCatalog catalog)
    {
        var sources = new List<PointSource>();
        if (DirAccess.DirExistsAbsolute(Dir))
            foreach (var folder in DirAccess.GetDirectoriesAt(Dir).OrderBy(f => f, System.StringComparer.Ordinal))
            {
                var files = DirAccess.GetFilesAt($"{Dir}/{folder}").Where(f => f.EndsWith(".json")).OrderBy(f => f, System.StringComparer.Ordinal).ToList();
                string Text(string file) => FileAccess.GetFileAsString($"{Dir}/{folder}/{file}");
                sources.Add(new PointSource(folder, files.Contains("kind.json") ? Text("kind.json") : null, files.Where(f => f != "kind.json").Select(Text).ToList()));
            }
        var points = PointCatalog.Load(sources, catalog);
        foreach (var problem in points.Problems) GD.PushWarning($"Point of interest not usable: {problem}");
        return points;
    }
}
