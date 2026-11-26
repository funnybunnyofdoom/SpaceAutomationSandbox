using System;
using System.IO;
using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// The game's name and version (#136, #137). The version lives in one place, <c>application/config/version</c> in project.godot;
/// the exe's own version numbers in export_presets.cfg follow it by hand (Windows wants four plain numbers there).
/// </summary>
public static class GameInfo
{
    /// <summary>The game's name, as the window and title screen show it.</summary>
    public const string Name = "Starjux";

    /// <summary>The folder name the game used before it had a name: <c>%APPDATA%\Godot\app_userdata\SpaceAutomationSandbox</c>.</summary>
    public const string OldUserFolder = "SpaceAutomationSandbox";

    /// <summary>The version, e.g. "1.0.0-rc1", or "dev" if project.godot has none.</summary>
    public static string Version
    {
        get
        {
            var v = ProjectSettings.GetSetting("application/config/version").AsString();
            return string.IsNullOrWhiteSpace(v) ? "dev" : v;
        }
    }

    /// <summary>"v1.0.0-rc1", for corners of the screen.</summary>
    public static string VersionLabel => "v" + Version;

    /// <summary>
    /// Once, when the game first runs under its name: copies saves, designs, races and settings from the folder it used before it had
    /// one, so nothing is lost. Copies, never moves: the old folder stays as it was, as a backup. A file already in the new folder is
    /// never overwritten. Runs before anything reads <c>user://</c>.
    /// </summary>
    public static void BringOverOldUserData()
    {
        try
        {
            var now = ProjectSettings.GlobalizePath("user://");
            var marker = Path.Combine(now, ".brought_over_from_" + OldUserFolder);
            if (File.Exists(marker)) return;
            var parent = Directory.GetParent(now.TrimEnd('/', '\\'));
            var old = parent is null ? null : Path.Combine(parent.FullName, OldUserFolder);
            Directory.CreateDirectory(now);
            if (old is not null && Directory.Exists(old) && !SamePath(old, now))
            {
                var copied = 0;
                foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(old, file);
                    if (relative.StartsWith("logs", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("shader_cache", StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith("vulkan", StringComparison.OrdinalIgnoreCase)) continue;
                    var to = Path.Combine(now, relative);
                    if (File.Exists(to)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to);
                    copied++;
                }
                GD.Print($"Brought over {copied} files from {old} (left in place as a backup).");
            }
            File.WriteAllText(marker, $"Files from {old} were copied here once, on {DateTime.UtcNow:O}. The old folder was left as it was.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Couldn't bring over the old saves: {e.Message}. They are still in the {OldUserFolder} folder.");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
}
