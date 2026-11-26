using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>Finds the kinds of points of interest (#122): every folder in data/points, with its kind.json and, for derelicts, its designs.</summary>
public static class PointLibrary
{
    public const string Dir = "res://data/points";

    /// <summary>The kinds of megastructure (#126): every file in data/megastructures.</summary>
    public static MegaCatalog LoadMegastructures(PartCatalog catalog)
    {
        const string dir = "res://data/megastructures";
        var files = DirAccess.DirExistsAbsolute(dir) ? DirAccess.GetFilesAt(dir).Where(f => f.EndsWith(".json")).Select(f => (f[..^5], FileAccess.GetFileAsString($"{dir}/{f}"))).ToList() : new();
        var kinds = MegaCatalog.Load(files, catalog);
        foreach (var problem in kinds.Problems) GD.PushWarning($"Megastructure not usable: {problem}");
        return kinds;
    }

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
