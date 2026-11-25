using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;
using FileAccess = Godot.FileAccess;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Finds the races: every folder in data/races (#116), with its race.json, portrait and, if it has them, ship.json and shipyard.json; and
/// the player's own in user://races (#117), each with race.json and its picture.
/// </summary>
public static class RaceLibrary
{
    public const string Dir = "res://data/races", UserDir = "user://races";

    private const int PictureWidth = 800, PictureHeight = 1000, MaxImageBytes = 30_000_000, MaxImageSide = 8_000;

    public static IReadOnlyList<string> LawIds { get; } = Laws.All.Select(l => l.Id).ToList();

    public static RaceCatalog Load(PartCatalog catalog)
    {
        var sources = new List<RaceSource>();
        foreach (var (dir, custom) in new[] { (Dir, false), (UserDir, true) })
            if (DirAccess.DirExistsAbsolute(dir))
                foreach (var folder in DirAccess.GetDirectoriesAt(dir).OrderBy(f => f, StringComparer.Ordinal))
                    sources.Add(new RaceSource(folder, Read($"{dir}/{folder}/race.json"), custom ? null : Read($"{dir}/{folder}/ship.json"), custom ? null : Read($"{dir}/{folder}/shipyard.json"), custom,
                        custom ? null : Read($"{dir}/{folder}/patrol.json"), custom ? null : Read($"{dir}/{folder}/raider.json"), custom ? null : Read($"{dir}/{folder}/trade_station.json")));
        var races = RaceCatalog.Load(sources, catalog, LawIds);
        foreach (var problem in races.Problems) GD.PushWarning($"Race not usable: {problem}");
        return races;
    }

    private static string? Read(string path) => FileAccess.FileExists(path) ? FileAccess.GetFileAsString(path) : null;

    private static string FolderOf(string id) => ProjectSettings.GlobalizePath($"{UserDir}/{id}");
    private static string PictureOf(string id) => Path.Combine(FolderOf(id), "portrait.webp");

    public static bool Exists(string id) => Directory.Exists(FolderOf(id));

    // --- Pictures ---

    private static readonly Dictionary<string, (DateTime Written, Texture2D Texture)> Pictures = new();

    /// <summary>Its portrait (4:5), or a plain one in its colour. <paramref name="picture"/> is one not saved yet.</summary>
    public static Texture2D Portrait(RaceDefinition race, Image? picture = null)
    {
        if (picture is not null) return Texture(picture);
        if (race.IsCustom && race.Picture.Length == 0 && Own(race.Id) is { } own) return own;
        var from = race.IsCustom ? race.Picture : race.Id;
        if (from.Length > 0 && from.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            foreach (var extension in new[] { "webp", "png" })
                if ($"{Dir}/{from}/portrait.{extension}" is var path && ResourceLoader.Exists(path)) return GD.Load<Texture2D>(path);
        var accent = race.Accent.Length > 0 && Color.HtmlIsValid(race.Accent) ? new Color(race.Accent) : UiTheme.Cyan;
        var gradient = new Gradient();
        gradient.SetColor(0, accent);
        gradient.SetColor(1, accent.Darkened(0.92f));
        gradient.AddPoint(0.28f, accent.Darkened(0.35f));
        gradient.AddPoint(0.3f, accent.Darkened(0.8f));
        return new GradientTexture2D { Gradient = gradient, Width = 320, Height = 400, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.42f), FillTo = new Vector2(0.5f, 1.1f) };
    }

    private static Texture2D Texture(Image image)
    {
        if (!image.HasMipmaps()) image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D? Own(string id)
    {
        var path = PictureOf(id);
        if (!File.Exists(path)) return null;
        var written = File.GetLastWriteTimeUtc(path);
        if (Pictures.TryGetValue(id, out var had) && had.Written == written) return had.Texture;
        var image = new Image();
        if (image.LoadWebpFromBuffer(File.ReadAllBytes(path)) != Error.Ok) return null;
        var texture = Texture(image);
        Pictures[id] = (written, texture);
        return texture;
    }

    public static bool HasPicture(string id) => File.Exists(PictureOf(id));

    /// <summary>Reads a picture from disk: checked, cropped to 4:5 and scaled. Null with the reason when it can't be used.</summary>
    public static Image? ReadPicture(string path, out string problem)
    {
        try
        {
            if (new FileInfo(path).Length > MaxImageBytes) { problem = $"The picture is over {MaxImageBytes / 1_000_000} MB."; return null; }
            return Fit(File.ReadAllBytes(path), out problem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = e.Message; return null; }
    }

    /// <summary>A picture's bytes, whatever kind they are: the kind is told by its first bytes, not its name.</summary>
    public static Image? Fit(byte[] bytes, out string problem)
    {
        problem = "It isn't a PNG, JPG or WebP picture.";
        var image = new Image();
        bool Starts(params byte[] with) => bytes.Length > 16 && bytes.AsSpan(0, with.Length).SequenceEqual(with);
        var error = Starts(0x89, 0x50, 0x4E, 0x47) ? image.LoadPngFromBuffer(bytes)
            : Starts(0xFF, 0xD8, 0xFF) ? image.LoadJpgFromBuffer(bytes)
            : Starts(0x52, 0x49, 0x46, 0x46) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? image.LoadWebpFromBuffer(bytes) : Error.FileUnrecognized;
        if (error != Error.Ok || image.IsEmpty()) return null;
        var (width, height) = (image.GetWidth(), image.GetHeight());
        if (width > MaxImageSide || height > MaxImageSide) { problem = $"The picture is {width} x {height}; at most {MaxImageSide} a side."; return null; }
        if (RaceFiles.Crop(width, height) is not { } crop) { problem = $"The picture is {width} x {height}; it's too small."; return null; }
        var cut = image.GetRegion(new Rect2I(crop.X, crop.Y, crop.Width, crop.Height));
        if (cut.GetWidth() > PictureWidth) cut.Resize(PictureWidth, PictureHeight, Image.Interpolation.Lanczos);
        cut.Convert(Image.Format.Rgb8);
        problem = "";
        return cut;
    }

    // --- The player's own ---

    /// <summary>Saves a race of the player's; a new picture replaces the old one, and one that isn't used any more goes.</summary>
    public static void Save(RaceDefinition race, Image? picture, string? pictureFrom = null)
    {
        if (!race.IsCustom) throw new ArgumentException("Only the player's own races can be saved.", nameof(race));
        Directory.CreateDirectory(FolderOf(race.Id));
        Write(Path.Combine(FolderOf(race.Id), "race.json"), System.Text.Encoding.UTF8.GetBytes(RaceFiles.ToJson(race)));
        if (picture is not null) Write(PictureOf(race.Id), Bytes(picture));
        else if (pictureFrom is not null && pictureFrom != race.Id && HasPicture(pictureFrom)) File.Copy(PictureOf(pictureFrom), PictureOf(race.Id), overwrite: true); // a copy of another race
        Pictures.Remove(race.Id);
    }

    private static byte[] Bytes(Image picture)
    {
        var plain = (Image)picture.Duplicate();
        plain.ClearMipmaps();
        return plain.SaveWebpToBuffer(lossy: true, quality: 0.9f);
    }

    private static void Write(string path, byte[] bytes)
    {
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static void Delete(string id)
    {
        if (!id.StartsWith(RaceFiles.CustomPrefix, StringComparison.Ordinal) || id.IndexOfAny(new[] { '/', '\\', '.' }) >= 0) return;
        if (Directory.Exists(FolderOf(id))) Directory.Delete(FolderOf(id), recursive: true);
        Pictures.Remove(id);
    }

    public static void Export(RaceDefinition race, string path) =>
        Write(path, System.Text.Encoding.UTF8.GetBytes(RaceFiles.ToShareFile(race, race.Picture.Length == 0 && HasPicture(race.Id) ? File.ReadAllBytes(PictureOf(race.Id)) : null)));

    /// <summary>Takes in a shared race under a free id.</summary>
    /// <exception cref="FormatException">It can't be used; the message says why.</exception>
    public static RaceDefinition Import(string path, PartCatalog catalog)
    {
        if (new FileInfo(path).Length > RaceFiles.MaxShareBytes) throw new FormatException("The file is too big to be a race.");
        var (race, bytes) = RaceFiles.FromShareFile(File.ReadAllText(path), catalog, LawIds);
        Image? picture = null;
        if (bytes is not null) picture = Fit(bytes, out var problem) ?? throw new FormatException(problem);
        race = race with { Id = RaceFiles.NewId(race.Name, Exists) };
        Save(race, picture);
        return race;
    }
}
