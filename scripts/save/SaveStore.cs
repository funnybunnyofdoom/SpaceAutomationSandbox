using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpaceAutomationSandbox.Save;

/// <summary>What kind of slot a save file is (from its file name).</summary>
public enum SaveKind
{
    /// <summary>Saved by the player, with a name.</summary>
    Manual,
    /// <summary>The quicksave slot (F6 / F9).</summary>
    Quick,
    /// <summary>One of the rotating autosave slots (timer, and closing the window).</summary>
    Auto,
}

/// <summary>One save file in the list. <see cref="Error"/> is set when its header can't be read (it's shown as broken).</summary>
public sealed record SaveSlot(string Path, SaveKind Kind, SaveHeader? Header, int SchemaVersion, string? Error, DateTime ModifiedUtc)
{
    public string Name => Header?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>When it was saved: the header's time, else the file's.</summary>
    public DateTime SavedUtc => Header is { } h && DateTime.TryParse(h.SavedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : ModifiedUtc;

    public bool Broken => Error is not null;
}

/// <summary>
/// Save slots (#60): every save is a file in one folder (user://saves/ in the game), with a small header
/// at the top that the list reads without parsing the sector.
/// <list type="bullet">
/// <item>Manual saves are named by the player (<c>save_*.json</c>); the quicksave is <c>quicksave.json</c>;
/// autosaves rotate through <c>autosave_1..3.json</c> (the oldest is replaced).</item>
/// <item>Writes go to a temporary file first and then replace the old one, so a crash mid-write never
/// destroys a save.</item>
/// <item>A file whose header can't be read is still listed, as broken, with the reason; nothing is
/// moved or deleted behind the player's back.</item>
/// <item>The single save from before slots (<c>user://savegame.json</c>) moves into the folder the first time.</item>
/// </list>
/// Plain .NET with a folder path, so it's tested without Godot.
/// </summary>
public static class SaveStore
{
    public const string QuickFile = "quicksave.json";
    public const int AutosaveSlots = 3;

    /// <summary>How many bytes from the start of a file the list reads (the header comes before the sector).</summary>
    private const int HeaderReadBytes = 64 * 1024;

    public static SaveKind KindOf(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (string.Equals(name, QuickFile, StringComparison.OrdinalIgnoreCase)) return SaveKind.Quick;
        return name.StartsWith("autosave_", StringComparison.OrdinalIgnoreCase) ? SaveKind.Auto : SaveKind.Manual;
    }

    /// <summary>All saves in the folder, newest first (broken ones included, by file time).</summary>
    public static IReadOnlyList<SaveSlot> List(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<SaveSlot>();
        return Directory.GetFiles(folder, "*.json")
            .Select(Read)
            .OrderByDescending(s => s.SavedUtc)
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The newest save that isn't broken, or null.</summary>
    public static SaveSlot? Newest(string folder) => List(folder).FirstOrDefault(s => !s.Broken);

    /// <summary>Reads a file's version and header from its first bytes, without parsing the sector.</summary>
    public static SaveSlot Read(string path)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        var kind = KindOf(path);
        try
        {
            byte[] bytes;
            using (var stream = File.OpenRead(path))
            {
                bytes = new byte[Math.Min(stream.Length, HeaderReadBytes)];
                var read = 0;
                while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
            }
            var whole = bytes.Length == new FileInfo(path).Length;
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0; // a UTF-8 byte-order mark
            var reader = new Utf8JsonReader(bytes.AsSpan(start), isFinalBlock: whole, state: default);
            int? version = null;
            string? game = null;
            SaveHeader? header = null;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new FormatException("not a JSON object");
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var property = reader.GetString();
                if (string.Equals(property, "sector", StringComparison.OrdinalIgnoreCase)) break; // the rest is the sector
                if (!reader.Read()) break;
                if (string.Equals(property, "schemaVersion", StringComparison.OrdinalIgnoreCase)) version = reader.GetInt32();
                else if (string.Equals(property, "game", StringComparison.OrdinalIgnoreCase)) game = reader.GetString();
                else if (string.Equals(property, "header", StringComparison.OrdinalIgnoreCase)) header = ReadHeader(ref reader);
                else reader.Skip();
            }
            if (game != SaveGameSerializer.GameId) throw new FormatException("not a SpaceAutomationSandbox save");
            if (version is not { } v) throw new FormatException("no save-format version");
            if (v > SaveGameSerializer.SchemaVersion) throw new FormatException($"made by a newer version of the game (save format {v})");
            return new SaveSlot(path, kind, header, v, null, modified);
        }
        catch (Exception e) when (e is JsonException or FormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new SaveSlot(path, kind, null, 0, e is JsonException ? "unreadable (damaged or not a save)" : e.Message, modified);
        }
    }

    private static SaveHeader ReadHeader(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new FormatException("bad header");
        string name = "", saved = "";
        double play = 0;
        long tick = 0;
        ulong seed = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString()?.ToLowerInvariant();
            reader.Read();
            switch (property)
            {
                case "name": name = reader.GetString() ?? ""; break;
                case "savedatutc": saved = reader.GetString() ?? ""; break;
                case "playtimes": play = reader.GetDouble(); break;
                case "tick": tick = reader.GetInt64(); break;
                case "seed": seed = reader.GetUInt64(); break;
                default: reader.Skip(); break;
            }
        }
        return new SaveHeader(name, saved, play, tick, seed);
    }

    /// <summary>Writes a save safely: to a temporary file, then into place.</summary>
    public static void Write(string path, string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A file name for a new manual save (unique: from the time, with a counter if needed).</summary>
    public static string NewManualPath(string folder, DateTime nowUtc)
    {
        var stem = $"save_{nowUtc:yyyyMMdd_HHmmss}";
        var path = System.IO.Path.Combine(folder, stem + ".json");
        for (var i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(folder, $"{stem}_{i}.json");
        return path;
    }

    public static string QuickPath(string folder) => System.IO.Path.Combine(folder, QuickFile);

    /// <summary>The autosave slot to write next: an unused one, else the oldest.</summary>
    public static string NextAutosavePath(string folder)
    {
        var slots = Enumerable.Range(1, AutosaveSlots).Select(i => System.IO.Path.Combine(folder, $"autosave_{i}.json")).ToList();
        return slots.FirstOrDefault(p => !File.Exists(p)) ?? slots.OrderBy(File.GetLastWriteTimeUtc).ThenBy(p => p, StringComparer.Ordinal).First();
    }

    /// <summary>Renames a save (its header name; the file keeps its place). The rest of the file is untouched.</summary>
    public static void Rename(string path, string name)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("not a save");
        if (root["header"] is JsonObject header) header["name"] = name;
        else
        {
            // An old save without a header: give it one (first, so the list can read it).
            var rebuilt = new JsonObject();
            foreach (var (key, value) in root.ToList())
            {
                root.Remove(key);
                rebuilt[key] = value;
                if (key == "game")
                    rebuilt["header"] = new JsonObject { ["name"] = name, ["savedAtUtc"] = File.GetLastWriteTimeUtc(path).ToString("O"), ["playTimeS"] = 0, ["tick"] = 0, ["seed"] = 0 };
            }
            root = rebuilt;
        }
        Write(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete(string path) => File.Delete(path);

    /// <summary>Moves the single save from before slots into the folder (once). Returns the new path, or null.</summary>
    public static string? MigrateLegacy(string legacyPath, string folder)
    {
        if (!File.Exists(legacyPath)) return null;
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, "save_from_before_slots.json");
        if (File.Exists(target)) return null;
        File.Move(legacyPath, target);
        return target;
    }
}
