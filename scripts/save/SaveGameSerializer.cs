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
    /// <summary>
    /// Version written. v2 added the landed state; v3 added rail orbits, inventories, build queues
    /// and the order counter; v4 added the anchored flag; v5 added part rotation, craft targets,
    /// docking, and order landing sites; v6 added body deposits, the drill remainder and mining jobs;
    /// v7 added survey flags, refinery batches and freight jobs; v8 added debug tuning and job retry
    /// times. Older files still load (missing parts read as empty / none / default; before v7 every
    /// body counts as surveyed).
    /// </summary>
    public const int SchemaVersion = 8;
    public const int OldestReadableSchemaVersion = 1;
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
                NextOrderId = sector.NextOrderId,
                Tuning = sector.Tuning.IsDefault ? null : new TuningDto
                {
                    DrillRateMultiplier = sector.Tuning.DrillRateMultiplier,
                    RefiningRateMultiplier = sector.Tuning.RefiningRateMultiplier,
                    FabricationRateMultiplier = sector.Tuning.FabricationRateMultiplier,
                },
                Bodies = sector.Bodies.Select(b => new BodyDto
                {
                    Id = b.Id.Value,
                    Name = b.Name,
                    Kind = b.Kind,
                    MassKg = b.MassKg,
                    DensityKgPerM3 = b.DensityKgPerM3,
                    IsMineable = b.IsMineable,
                    DepositsKg = b.DepositsKg.Count > 0
                        ? new SortedDictionary<string, long>(b.DepositsKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
                        : null,
                    Orbit = b.Orbit is { } o ? OrbitToDto(o) : null,
                    Surveyed = b.Surveyed,
                }).ToList(),
                Crafts = sector.Crafts.Select(c => new CraftDto
                {
                    Id = c.Id.Value,
                    Name = c.Name,
                    Design = DesignToDto(c.Design),
                    Position = Vec(c.Position),
                    Velocity = Vec(c.Velocity),
                    HeadingRad = c.HeadingRad,
                    Controls = new ControlsDto { Throttle = c.Controls.Throttle, Turn = c.Controls.Turn, Brake = c.Controls.Brake },
                    TargetBodyId = c.TargetBodyId.Value,
                    TargetCraftId = c.TargetCraftId.Value,
                    DockedToCraftId = c.DockedToCraftId.Value,
                    DockPort = c.DockPortIndex,
                    LandedOnBodyId = c.LandedOnBodyId.Value,
                    LandedOffset = c.IsLanded ? Vec(c.LandedOffset) : null,
                    Anchored = c.IsAnchored,
                    MiningRemainderKg = c.MiningRemainderKg,
                    RefiningBatch = c.RefiningBatch is { } rb
                        ? new BatchDto
                        {
                            RecipeId = rb.RecipeId, ProgressKg = rb.ProgressKg,
                            InputsKg = new SortedDictionary<string, long>(rb.InputsKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
                        }
                        : null,
                    Job = c.Job switch
                    {
                        MiningJob j => new JobDto
                        {
                            Kind = j.Kind, SourceBodyId = j.SourceBodyId.Value, MaterialId = j.MaterialId, DestinationId = j.DestinationId.Value,
                            ReturnFraction = j.ReturnFraction, Active = j.Active, State = j.State, ResumeState = j.ResumeState,
                            WaitReason = j.WaitReason, CompletedCycles = j.CompletedCycles, DeliveredKg = j.DeliveredKg, RetryAtTick = j.RetryAtTick,
                        },
                        HaulJob h => new JobDto
                        {
                            Kind = h.Kind, PickupId = h.PickupId.Value, MaterialId = h.MaterialId, DestinationId = h.DestinationId.Value,
                            MinPickupKg = h.MinPickupKg, KeepStockedKg = h.KeepStockedKg, Active = h.Active,
                            HaulState = h.State, HaulResumeState = h.ResumeState,
                            WaitReason = h.WaitReason, CompletedCycles = h.CompletedCycles, DeliveredKg = h.DeliveredKg, RetryAtTick = h.RetryAtTick,
                        },
                        _ => null,
                    },
                    RailOrbit = c.RailOrbit is { } r ? OrbitToDto(r) : null,
                    InventoryKg = c.Inventory.Kg.Count > 0 ? new SortedDictionary<string, long>(c.Inventory.Kg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal) : null,
                    BuildQueue = c.BuildQueue.Count > 0
                        ? c.BuildQueue.Select(o => new OrderDto
                        {
                            OrderId = o.Id.Value,
                            ShipName = o.ShipName,
                            Design = DesignToDto(o.Design),
                            MaterialsKg = new SortedDictionary<string, long>(o.MaterialsKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
                            ProgressKg = o.ProgressKg,
                            LandingSiteBodyId = o.LandingSite.Value,
                        }).ToList()
                        : null,
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
        if (file.SchemaVersion < OldestReadableSchemaVersion || file.SchemaVersion > SchemaVersion)
            throw new FormatException(
                $"Save schemaVersion {file.SchemaVersion}; this build reads {OldestReadableSchemaVersion} to {SchemaVersion}.");
        var dto = file.Sector ?? throw new FormatException("Save has no sector.");

        try
        {
            var sector = new SectorState(dto.Seed, dto.Tick) { Recipes = catalog.Recipes };
            if (dto.Tuning is { } t)
            {
                sector.Tuning.DrillRateMultiplier = t.DrillRateMultiplier;
                sector.Tuning.RefiningRateMultiplier = t.RefiningRateMultiplier;
                sector.Tuning.FabricationRateMultiplier = t.FabricationRateMultiplier;
            }
            AddBodiesParentsFirst(sector, dto.Bodies ?? new List<BodyDto>(), file.SchemaVersion);

            foreach (var c in dto.Crafts ?? new List<CraftDto>())
            {
                var design = DesignFromDto(c.Design, $"Craft {c.Id}");
                // A craft must be usable as a ship or as a station.
                var shipErrors = design.Validate(catalog, DesignRole.Ship);
                if (shipErrors.Count > 0 && design.Validate(catalog, DesignRole.Station).Count > 0)
                    throw new FormatException($"Craft {c.Id} design '{design.Id}' is invalid: {string.Join("; ", shipErrors)}");

                var craft = new CraftState(new CraftId(c.Id), c.Name ?? "", design, design.ComputeStats(catalog),
                    ToVec(c.Position, "position"), c.HeadingRad)
                {
                    Velocity = ToVec(c.Velocity, "velocity"),
                    Controls = c.Controls is { } k ? new ShipControls(k.Throttle, k.Turn, k.Brake) : ShipControls.Idle,
                };
                sector.AddCraft(craft);
                sector.SetTarget(craft.Id, new BodyId(c.TargetBodyId));
                // v1 has no landed fields: they read as 0/null, i.e. flying. That is the whole v1 -> v2 migration.
                if (c.LandedOnBodyId != 0)
                    sector.RestoreLanding(craft.Id, new BodyId(c.LandedOnBodyId), ToVec(c.LandedOffset, "landedOffset"));
                // Before v4 nothing could be anchored, except that a landed foundation always is.
                sector.RestoreAnchored(craft.Id, file.SchemaVersion >= 4 ? c.Anchored : craft.IsLanded && craft.Stats.IsFixedStructure);
                // v1/v2 have none of the following; null reads as "not on rails, empty, nothing queued".
                if (c.RailOrbit is { } rail) sector.RestoreRails(craft.Id, OrbitFromDto(rail));
                foreach (var (material, kg) in c.InventoryKg ?? new SortedDictionary<string, long>())
                {
                    if (!catalog.Materials.Contains(material)) throw new FormatException($"Craft {c.Id} stores unknown material '{material}'.");
                    if (kg <= 0) throw new FormatException($"Craft {c.Id} stores {kg} kg of '{material}'.");
                    craft.Inventory.Add(material, kg);
                }
                foreach (var o in c.BuildQueue ?? new List<OrderDto>())
                {
                    var orderDesign = DesignFromDto(o.Design, $"Order {o.OrderId}");
                    var orderErrors = orderDesign.Validate(catalog, orderDesign.SuggestedRole(catalog));
                    if (orderErrors.Count > 0)
                        throw new FormatException($"Order {o.OrderId} design '{orderDesign.Id}' is invalid: {string.Join("; ", orderErrors)}");
                    var materials = o.MaterialsKg ?? new SortedDictionary<string, long>();
                    if (materials.Any(kv => !catalog.Materials.Contains(kv.Key) || kv.Value < 0))
                        throw new FormatException($"Order {o.OrderId} holds unknown or negative materials.");
                    sector.RestoreOrder(craft.Id, new BuildOrder(new OrderId(o.OrderId), o.ShipName ?? "", orderDesign,
                        orderDesign.ComputeStats(catalog), materials, o.ProgressKg, new BodyId(o.LandingSiteBodyId)));
                    if (o.LandingSiteBodyId != 0) sector.GetBody(new BodyId(o.LandingSiteBodyId));
                }
            }
            if (dto.NextOrderId > 0) sector.RestoreNextOrderId(dto.NextOrderId);

            // Links between crafts, after every craft exists (v5; older files have none).
            foreach (var c in dto.Crafts ?? new List<CraftDto>())
            {
                if (c.TargetCraftId != 0) sector.SetTargetCraft(new CraftId(c.Id), new CraftId(c.TargetCraftId));
                if (c.DockedToCraftId != 0) sector.RestoreDock(new CraftId(c.Id), new CraftId(c.DockedToCraftId), c.DockPort);
                var restored = sector.GetCraft(new CraftId(c.Id));
                if (c.MiningRemainderKg is < 0 or >= 1) throw new FormatException($"Craft {c.Id} has a drill remainder of {c.MiningRemainderKg} kg.");
                restored.MiningRemainderKg = c.MiningRemainderKg;
                if (c.RefiningBatch is { } batch)
                {
                    var inputs = batch.InputsKg ?? new SortedDictionary<string, long>();
                    if (inputs.Any(kv => !catalog.Materials.Contains(kv.Key) || kv.Value <= 0) || batch.ProgressKg < 0)
                        throw new FormatException($"Craft {c.Id} has an invalid refining batch.");
                    restored.RefiningBatch = new RefiningBatch(batch.RecipeId ?? "", inputs, batch.ProgressKg);
                }
                if (c.Job is { } j)
                {
                    if (!catalog.Materials.Contains(j.MaterialId ?? "")) throw new FormatException($"Craft {c.Id} job uses unknown material '{j.MaterialId}'.");
                    sector.GetCraft(new CraftId(j.DestinationId));
                    if (j.Kind == "freight")
                    {
                        sector.GetCraft(new CraftId(j.PickupId));
                        sector.RestoreJob(restored.Id, new HaulJob(new CraftId(j.PickupId), j.MaterialId!, new CraftId(j.DestinationId), j.MinPickupKg, j.KeepStockedKg)
                        {
                            Active = j.Active, State = j.HaulState, ResumeState = j.HaulResumeState, WaitReason = j.WaitReason,
                            CompletedCycles = j.CompletedCycles, DeliveredKg = j.DeliveredKg, RetryAtTick = j.RetryAtTick,
                        });
                    }
                    else
                    {
                        sector.GetBody(new BodyId(j.SourceBodyId));
                        sector.RestoreJob(restored.Id, new MiningJob(new BodyId(j.SourceBodyId), j.MaterialId!, new CraftId(j.DestinationId), j.ReturnFraction)
                        {
                            Active = j.Active, State = j.State, ResumeState = j.ResumeState, WaitReason = j.WaitReason,
                            CompletedCycles = j.CompletedCycles, DeliveredKg = j.DeliveredKg, RetryAtTick = j.RetryAtTick,
                        });
                    }
                }
            }

            var player = new CraftId(file.PlayerCraftId);
            if (!player.IsNone) sector.GetCraft(player); // must exist
            sector.PilotCraftId = player;
            return new LoadedGame(sector, player);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException($"Save file is inconsistent: {e.Message}", e);
        }
    }

    // SectorState requires parents before children; saves list bodies by id, which may not be that order.
    private static void AddBodiesParentsFirst(SectorState sector, List<BodyDto> bodies, int schemaVersion)
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
                sector.AddBody(new CelestialBodyState(new BodyId(b.Id), b.Name ?? "", b.Kind, b.MassKg, b.DensityKgPerM3, b.IsMineable, orbit,
                    b.DepositsKg));
                // Before v7 there was no survey: treat every body as surveyed so old games keep working.
                if (schemaVersion < 7 || b.Surveyed) sector.MarkSurveyed(new BodyId(b.Id));
                added.Add(b.Id);
                pending.Remove(b);
            }
        }
    }

    private static VecDto Vec(Vector2D v) => new() { X = v.X, Y = v.Y };

    private static OrbitDto OrbitToDto(CircularOrbit o) =>
        new() { ParentId = o.Parent.Value, RadiusM = o.RadiusM, PeriodS = o.PeriodS, PhaseAtZeroRad = o.PhaseAtZeroRad };

    private static CircularOrbit OrbitFromDto(OrbitDto o) => new(new BodyId(o.ParentId), o.RadiusM, o.PeriodS, o.PhaseAtZeroRad);

    private static DesignDto DesignToDto(ShipDesign d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Cells = d.Cells.Select(cell => new CellDto { X = cell.Pos.X, Y = cell.Pos.Y, Part = cell.PartId, Rot = cell.Rotation }).ToList(),
    };

    private static ShipDesign DesignFromDto(DesignDto? d, string owner)
    {
        if (d is null) throw new FormatException($"{owner} has no design.");
        return new ShipDesign(d.Id ?? "", d.Name ?? "",
            (d.Cells ?? new List<CellDto>()).Select(cell =>
                new DesignCell(new GridPos(cell.X, cell.Y), cell.Part ?? throw new FormatException("Cell without a part."), cell.Rot)));
    }

    private static Vector2D ToVec(VecDto? v, string what) =>
        v is null ? throw new FormatException($"Craft is missing {what}.") : new Vector2D(v.X, v.Y);

    // --- File shape (schema 8). Change only together with SchemaVersion and docs/08. ---

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
        public long NextOrderId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] // omitted while every multiplier is 1
        public TuningDto? Tuning { get; set; }
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
        public SortedDictionary<string, long>? DepositsKg { get; set; }
        public bool Surveyed { get; set; }
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
        public uint TargetCraftId { get; set; }
        public uint DockedToCraftId { get; set; }
        public int DockPort { get; set; }
        public uint LandedOnBodyId { get; set; }
        public VecDto? LandedOffset { get; set; }
        public bool Anchored { get; set; }
        public double MiningRemainderKg { get; set; }
        public BatchDto? RefiningBatch { get; set; }
        public JobDto? Job { get; set; }
        public OrbitDto? RailOrbit { get; set; }
        public SortedDictionary<string, long>? InventoryKg { get; set; }
        public List<OrderDto>? BuildQueue { get; set; }
    }

    private sealed class TuningDto
    {
        public double DrillRateMultiplier { get; set; } = 1;
        public double RefiningRateMultiplier { get; set; } = 1;
        public double FabricationRateMultiplier { get; set; } = 1;
    }

    private sealed class BatchDto
    {
        public string? RecipeId { get; set; }
        public SortedDictionary<string, long>? InputsKg { get; set; }
        public double ProgressKg { get; set; }
    }

    private sealed class JobDto
    {
        /// <summary>"mining" (v6 default) or "freight" (v7).</summary>
        public string? Kind { get; set; }
        public uint SourceBodyId { get; set; }
        public uint PickupId { get; set; }
        public long MinPickupKg { get; set; }
        public long KeepStockedKg { get; set; }
        public HaulJobState HaulState { get; set; }
        public HaulJobState HaulResumeState { get; set; }
        public string? MaterialId { get; set; }
        public uint DestinationId { get; set; }
        public double ReturnFraction { get; set; }
        public bool Active { get; set; }
        public MiningJobState State { get; set; }
        public MiningJobState ResumeState { get; set; }
        public string? WaitReason { get; set; }
        public int CompletedCycles { get; set; }
        public long DeliveredKg { get; set; }
        public long RetryAtTick { get; set; }
    }

    private sealed class OrderDto
    {
        public long OrderId { get; set; }
        public string? ShipName { get; set; }
        public DesignDto? Design { get; set; }
        public SortedDictionary<string, long>? MaterialsKg { get; set; }
        public double ProgressKg { get; set; }
        public uint LandingSiteBodyId { get; set; }
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

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Rot { get; set; }
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
