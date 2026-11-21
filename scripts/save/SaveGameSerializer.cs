using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Save;

/// <summary>A loaded game: the sector plus which craft the player flies.</summary>
public sealed record LoadedGame(SectorState Sector, CraftId PlayerCraftId);

/// <summary>
/// Converts a whole <see cref="SectorState"/> to and from versioned JSON (format in docs/08_SAVE_FORMAT.md).
/// Saves store the full state, not "seed + changes", so retuning the generator never changes an
/// existing save. References are stable IDs. Ship designs are embedded, so a save still loads if
/// the blueprint file changes; part stats are re-read from the current part catalog.
/// Pure C#, deterministic: the same state always gives byte-identical JSON.
/// </summary>
public static class SaveGameSerializer
{
    public const int SchemaVersion = 1;
    public const string GameId = "SpaceAutomationSandbox";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(SectorState sector, CraftId playerCraftId)
    {
        var file = new SaveFile
        {
            SchemaVersion = SchemaVersion,
            Game = GameId,
            PlayerCraftId = playerCraftId.Value,
            Sector = new SectorDto
            {
                Seed = sector.Seed,
                Tick = sector.Tick,
                Bodies = sector.Bodies.Select(b => new BodyDto
                {
                    Id = b.Id.Value,
                    Name = b.Name,
                    Kind = b.Kind,
                    MassKg = b.MassKg,
                    DensityKgPerM3 = b.DensityKgPerM3,
                    IsMineable = b.IsMineable,
                    Orbit = b.Orbit is { } o
                        ? new OrbitDto { ParentId = o.Parent.Value, RadiusM = o.RadiusM, PeriodS = o.PeriodS, PhaseAtZeroRad = o.PhaseAtZeroRad }
                        : null,
                }).ToList(),
                Crafts = sector.Crafts.Select(c => new CraftDto
                {
                    Id = c.Id.Value,
                    Name = c.Name,
                    Design = new DesignDto
                    {
                        Id = c.Design.Id,
                        Name = c.Design.Name,
                        Cells = c.Design.Cells.Select(cell => new CellDto { X = cell.Pos.X, Y = cell.Pos.Y, Part = cell.PartId }).ToList(),
                    },
                    Position = Vec(c.Position),
                    Velocity = Vec(c.Velocity),
                    HeadingRad = c.HeadingRad,
                    Controls = new ControlsDto { Throttle = c.Controls.Throttle, Turn = c.Controls.Turn, Brake = c.Controls.Brake },
                    TargetBodyId = c.TargetBodyId.Value,
                }).ToList(),
            },
        };
        return JsonSerializer.Serialize(file, Options);
    }

    /// <exception cref="FormatException">The file is not a valid save; the message says why.</exception>
    public static LoadedGame FromJson(string json, PartCatalog catalog)
    {
        SaveFile? file;
        try { file = JsonSerializer.Deserialize<SaveFile>(json, Options); }
        catch (JsonException e) { throw new FormatException($"Save file is not valid JSON: {e.Message}", e); }

        if (file is null) throw new FormatException("Save file is empty.");
        if (file.Game != GameId) throw new FormatException($"Not a {GameId} save (game '{file.Game}').");
        if (file.SchemaVersion != SchemaVersion)
            throw new FormatException($"Save schemaVersion {file.SchemaVersion}; this build reads {SchemaVersion}.");
        var dto = file.Sector ?? throw new FormatException("Save has no sector.");

        try
        {
            var sector = new SectorState(dto.Seed, dto.Tick);
            AddBodiesParentsFirst(sector, dto.Bodies ?? new List<BodyDto>());

            foreach (var c in dto.Crafts ?? new List<CraftDto>())
            {
                var d = c.Design ?? throw new FormatException($"Craft {c.Id} has no design.");
                var design = new ShipDesign(d.Id ?? "", d.Name ?? "",
                    (d.Cells ?? new List<CellDto>()).Select(cell =>
                        new DesignCell(new GridPos(cell.X, cell.Y), cell.Part ?? throw new FormatException("Cell without a part."))));
                var errors = design.Validate(catalog);
                if (errors.Count > 0)
                    throw new FormatException($"Craft {c.Id} design '{design.Id}' is invalid: {string.Join("; ", errors)}");

                var craft = new CraftState(new CraftId(c.Id), c.Name ?? "", design, design.ComputeStats(catalog),
                    ToVec(c.Position, "position"), c.HeadingRad)
                {
                    Velocity = ToVec(c.Velocity, "velocity"),
                    Controls = c.Controls is { } k ? new ShipControls(k.Throttle, k.Turn, k.Brake) : ShipControls.Idle,
                };
                sector.AddCraft(craft);
                sector.SetTarget(craft.Id, new BodyId(c.TargetBodyId));
            }

            var player = new CraftId(file.PlayerCraftId);
            if (!player.IsNone) sector.GetCraft(player); // must exist
            return new LoadedGame(sector, player);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException($"Save file is inconsistent: {e.Message}", e);
        }
    }

    // SectorState requires parents before children; saves list bodies by id, which may not be that order.
    private static void AddBodiesParentsFirst(SectorState sector, List<BodyDto> bodies)
    {
        var pending = bodies.ToList();
        var added = new HashSet<uint>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(b => b.Orbit is null || added.Contains(b.Orbit.ParentId)).ToList();
            if (ready.Count == 0)
                throw new FormatException($"Bodies {string.Join(", ", pending.Select(b => b.Id))} orbit missing or circular parents.");
            foreach (var b in ready)
            {
                var orbit = b.Orbit is { } o ? new CircularOrbit(new BodyId(o.ParentId), o.RadiusM, o.PeriodS, o.PhaseAtZeroRad) : null;
                sector.AddBody(new CelestialBodyState(new BodyId(b.Id), b.Name ?? "", b.Kind, b.MassKg, b.DensityKgPerM3, b.IsMineable, orbit));
                added.Add(b.Id);
                pending.Remove(b);
            }
        }
    }

    private static VecDto Vec(Vector2D v) => new() { X = v.X, Y = v.Y };

    private static Vector2D ToVec(VecDto? v, string what) =>
        v is null ? throw new FormatException($"Craft is missing {what}.") : new Vector2D(v.X, v.Y);

    // --- File shape (schema 1). Change only together with SchemaVersion and docs/08. ---

    private sealed class SaveFile
    {
        public int SchemaVersion { get; set; }
        public string? Game { get; set; }
        public uint PlayerCraftId { get; set; }
        public SectorDto? Sector { get; set; }
    }

    private sealed class SectorDto
    {
        public ulong Seed { get; set; }
        public long Tick { get; set; }
        public List<BodyDto>? Bodies { get; set; }
        public List<CraftDto>? Crafts { get; set; }
    }

    private sealed class BodyDto
    {
        public uint Id { get; set; }
        public string? Name { get; set; }
        public BodyKind Kind { get; set; }
        public double MassKg { get; set; }
        public double DensityKgPerM3 { get; set; }
        public bool IsMineable { get; set; }
        public OrbitDto? Orbit { get; set; }
    }

    private sealed class OrbitDto
    {
        public uint ParentId { get; set; }
        public double RadiusM { get; set; }
        public double PeriodS { get; set; }
        public double PhaseAtZeroRad { get; set; }
    }

    private sealed class CraftDto
    {
        public uint Id { get; set; }
        public string? Name { get; set; }
        public DesignDto? Design { get; set; }
        public VecDto? Position { get; set; }
        public VecDto? Velocity { get; set; }
        public double HeadingRad { get; set; }
        public ControlsDto? Controls { get; set; }
        public uint TargetBodyId { get; set; }
    }

    private sealed class DesignDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<CellDto>? Cells { get; set; }
    }

    private sealed class CellDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string? Part { get; set; }
    }

    private sealed class VecDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class ControlsDto
    {
        public double Throttle { get; set; }
        public double Turn { get; set; }
        public bool Brake { get; set; }
    }
}
