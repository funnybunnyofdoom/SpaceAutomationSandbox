using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>One design file the editor can open.</summary>
public sealed record BlueprintEntry(string Id, string Label, string Path, bool IsBuiltIn);

/// <summary>
/// Finds and stores design files. Built-in designs ship read-only in res://data/blueprints.
/// Player designs live in user://blueprints (writable in exported games too). The file name is
/// the design id. Saving can't use a built-in id, so the shipped files are never shadowed.
/// </summary>
public static class BlueprintLibrary
{
    public const string BuiltInDir = "res://data/blueprints";
    public const string UserDir = "user://blueprints";

    public static List<BlueprintEntry> List()
    {
        var entries = new List<BlueprintEntry>();
        foreach (var (dir, builtIn) in new[] { (BuiltInDir, true), (UserDir, false) })
        {
            if (!DirAccess.DirExistsAbsolute(dir)) continue;
            foreach (var file in DirAccess.GetFilesAt(dir).Where(f => f.EndsWith(".json")).OrderBy(f => f))
            {
                var path = $"{dir}/{file}";
                var id = file[..^5];
                var label = id;
                try { label = ShipDesign.FromJson(Godot.FileAccess.GetFileAsString(path)).Name; }
                catch (System.FormatException) { label = $"{id} (unreadable)"; }
                catch (System.Text.Json.JsonException) { label = $"{id} (unreadable)"; }
                entries.Add(new BlueprintEntry(id, builtIn ? $"{label} (built-in)" : label, path, builtIn));
            }
        }
        return entries;
    }

    public static ShipDesign Load(BlueprintEntry entry) => ShipDesign.FromJson(Godot.FileAccess.GetFileAsString(entry.Path));

    public static bool IsBuiltInId(string id) => Godot.FileAccess.FileExists($"{BuiltInDir}/{id}.json");

    public static string UserPathFor(string id) => ProjectSettings.GlobalizePath($"{UserDir}/{id}.json");

    public static bool UserDesignExists(string id) => System.IO.File.Exists(UserPathFor(id));

    /// <summary>Writes the design to user://blueprints/&lt;id&gt;.json via a temp file. Returns the path.</summary>
    public static string Save(ShipDesign design)
    {
        var path = UserPathFor(design.Id);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, design.ToJson());
        System.IO.File.Move(tmp, path, overwrite: true);
        return path;
    }
}
