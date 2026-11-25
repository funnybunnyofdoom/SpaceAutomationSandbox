using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>A module file: the module, or why it can't be used.</summary>
public sealed record ModuleEntry(string Id, ShipModule? Module, string? Problem, bool IsBuiltIn);

/// <summary>Finds and stores modules (#113): built-in ones read-only in res://data/modules, the player's in user://modules, beside designs.</summary>
public static class ModuleLibrary
{
    public const string BuiltInDir = "res://data/modules";
    public const string UserDir = "user://modules";

    /// <summary>The player's first, then the built-in ones, by name.</summary>
    public static List<ModuleEntry> List(PartCatalog catalog)
    {
        var entries = new List<ModuleEntry>();
        foreach (var (dir, builtIn) in new[] { (UserDir, false), (BuiltInDir, true) })
        {
            if (!DirAccess.DirExistsAbsolute(dir)) continue;
            foreach (var file in DirAccess.GetFilesAt(dir).Where(f => f.EndsWith(".json")).OrderBy(f => f, StringComparer.Ordinal))
            {
                var id = file[..^5];
                try { entries.Add(new(id, ShipModule.FromJson(Godot.FileAccess.GetFileAsString($"{dir}/{file}"), catalog), null, builtIn)); }
                catch (FormatException e) { entries.Add(new(id, null, e.Message, builtIn)); }
            }
        }
        return entries;
    }

    private static string PathFor(string id) => ProjectSettings.GlobalizePath($"{UserDir}/{id}.json");

    public static bool Exists(string id) => System.IO.File.Exists(PathFor(id));

    public static bool IsBuiltInId(string id) => Godot.FileAccess.FileExists($"{BuiltInDir}/{id}.json");

    /// <summary>Writes it via a temp file.</summary>
    public static void Save(ShipModule module)
    {
        var path = PathFor(module.Id);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, module.ToJson());
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string id) => System.IO.File.Delete(PathFor(id));
}
