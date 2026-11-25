using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceAutomationSandbox.Construction;

/// <summary>
/// A named group of parts to stamp into designs (#113). A stamp is a copy (the creator's choice): designs don't know where their parts
/// came from, so changing a module changes no design. Immutable; its corner is at (0, 0).
/// </summary>
public sealed class ShipModule
{
    public const int SchemaVersion = 1;
    public const string FileKind = "module";
    public const int MaxNameLength = 40;

    private const int MaxSide = 2 * BlueprintEditor.LargeHalfExtent + 1;

    public ShipModule(string name, IEnumerable<DesignCell> cells)
    {
        Name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (Name.Length == 0) throw new FormatException("A module needs a name.");
        if (Name.Length > MaxNameLength) throw new FormatException($"A module's name has at most {MaxNameLength} characters.");
        var given = cells.ToList();
        if (given.Count == 0) throw new FormatException("A module needs at least one part.");
        if (given.Any(c => string.IsNullOrEmpty(c.PartId))) throw new FormatException("A cell has no part.");
        if (given.FirstOrDefault(c => c.Rotation is < 0 or > 3) is { PartId: not null } turned) throw new FormatException($"The part at ({turned.Pos.X},{turned.Pos.Y}) has rotation {turned.Rotation}; it must be 0 to 3.");
        if (given.GroupBy(c => c.Pos).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new FormatException($"Two parts at ({twice.Key.X},{twice.Key.Y}).");
        Width = (int)Math.Min(int.MaxValue, (long)given.Max(c => c.Pos.X) - given.Min(c => c.Pos.X) + 1);
        Height = (int)Math.Min(int.MaxValue, (long)given.Max(c => c.Pos.Y) - given.Min(c => c.Pos.Y) + 1);
        if (Width > MaxSide || Height > MaxSide) throw new FormatException($"It's {Width} x {Height}; a module has at most {MaxSide} x {MaxSide} cells.");
        Cells = BlueprintTools.ToCorner(given);
        Design = new ShipDesign(Id, Name, Cells);
    }

    public string Name { get; }

    /// <summary>File name, from the name.</summary>
    public string Id => BlueprintEditor.SlugFromName(Name);

    public IReadOnlyList<DesignCell> Cells { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Its parts as a design: for its picture, stats and cost.</summary>
    public ShipDesign Design { get; }

    public ShipModule Renamed(string name) => new(name, Cells);

    /// <exception cref="FormatException">It's not a module this build can use; the message says why.</exception>
    public static ShipModule FromJson(string json, PartCatalog catalog)
    {
        ModuleFile? file;
        try { file = JsonSerializer.Deserialize<ModuleFile>(json, PartCatalog.JsonOptions); }
        catch (JsonException e) { throw new FormatException($"Not valid JSON: {e.Message}", e); }
        if (file is null) throw new FormatException("The file is empty.");
        if (file.Kind != FileKind) throw new FormatException("Not a module file.");
        if (file.SchemaVersion is < 1 or > SchemaVersion) throw new FormatException($"Module schemaVersion {file.SchemaVersion}; this build reads 1 to {SchemaVersion}.");
        var module = new ShipModule(file.Name ?? "", (file.Cells ?? new()).Select(c => c is null ? throw new FormatException("A cell is empty.") : new DesignCell(new GridPos(c.X, c.Y), c.Part ?? "", c.Rot)));
        if (module.Cells.FirstOrDefault(c => !catalog.TryGet(c.PartId, out _)) is { PartId: not null } unknown) throw new FormatException($"Unknown part '{unknown.PartId}'.");
        return module;
    }

    /// <summary>Deterministic: cells in row, then column order; rot omitted when 0.</summary>
    public string ToJson() => JsonSerializer.Serialize(new ModuleFile
    {
        SchemaVersion = SchemaVersion, Kind = FileKind, Name = Name,
        Cells = Cells.Select(c => new CellDto { X = c.Pos.X, Y = c.Pos.Y, Part = c.PartId, Rot = c.Rotation }).ToList(),
    }, WriteOptions);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class ModuleFile
    {
        public int SchemaVersion { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public List<CellDto>? Cells { get; set; }
    }

    private sealed class CellDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string? Part { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Rot { get; set; }
    }
}
