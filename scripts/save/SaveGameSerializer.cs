using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Save;

/// <summary>A loaded game: the sector plus which craft the player flies.</summary>
public sealed record LoadedGame(SectorState Sector, CraftId PlayerCraftId, SaveHeader? Header = null);

/// <summary>
/// The small summary at the top of a save (#60): what the save list shows without reading the sector.
/// Optional: saves from before slots have none.
/// </summary>
/// <remarks>
/// Later optional fields: <c>GameName</c> (#62); <c>GoalsOn</c> and <c>GoalsDone</c>, the new-player goal
/// list and the ids of goals met (#65). Saves without them load with the goal list off.
/// </remarks>
public sealed record SaveHeader(string Name, string SavedAtUtc, double PlayTimeS, long Tick, ulong Seed, string GameName = "",
    bool GoalsOn = false, string[]? GoalsDone = null);

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
    /// times; v9 limited storage to storage parts; v10 added power and slag ejection; v11 replaced order
    /// landing sites with construction kits and added station requests and serve-requests freight.
    /// v12 added the star system (new games), the cruise drive and solar power by distance; v13 added
    /// part health (impact damage) and local gravity; v14 added crew and life support; v15 added abstract
    /// transit legs for craft far from the player; v16 added repairs (intact design, repair progress,
    /// servicing) and survey jobs; v17 added refit/dismantle/pack orders and the craft id counter
    /// (removed craft's ids are never reused, and jobs may still name them); v18 added warp gates and
    /// further star systems (their bodies are ordinary bodies; gates record their link and queue); v19
    /// added research (points, techs done; older saves have everything unlocked) and star types; v20 added
    /// swarms (#30) and the swarm a build order is for; v21 added combat (#84: whose craft, wrecks, weapons, shots, raids); v22 added salvage jobs (#86); v23 added missiles (#87) and shields (#90); v24 added guard, escort and patrol jobs (#88) and civilization patrols (#89);
    /// v25 added trade stations, trade routes and diplomacy (#89); v26 added native life (#91, #92); v27 added
    /// native animals (#93); v28 added antimatter and quantum drives (#81, #82); v29 added milestones (#38)
    /// and closable gates (#95); v30 added
    /// flying to a target (#108); v31 added what events are about (#100); v32 added large-ship building (#111).
    /// Older files are upgraded step by step by
    /// <see cref="SaveMigrations"/> before reading, so the loader below only reads the current format.
    /// </summary>
    public const int SchemaVersion = 32;
    public const int OldestReadableSchemaVersion = 1;
    public const string GameId = "SpaceAutomationSandbox";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(SectorState sector, CraftId playerCraftId, SaveHeader? header = null)
    {
        var file = new SaveFile
        {
            SchemaVersion = SchemaVersion,
            Game = GameId,
            Header = header,
            PlayerCraftId = playerCraftId.Value,
            Sector = new SectorDto
            {
                Seed = sector.Seed,
                Tick = sector.Tick,
                NextOrderId = sector.NextOrderId,
                NextCraftId = sector.NextCraftId,
                NextBodyId = sector.NextBodyId,
                DiscoveredKg = sector.DiscoveredKg,
                ResearchPoints = sector.ResearchPoints,
                UnlockedTech = sector.UnlockedTech.Count > 0 ? sector.UnlockedTech.ToList() : null,
                ResearchAllUnlocked = sector.ResearchAllUnlocked,
                Factions = sector.Factions.Count > 0
                    ? sector.Factions.Select(f => new FactionDto
                    {
                        Id = f.Id, Name = f.Name, Culture = f.Culture.Id, HomeStarId = f.HomeStarId.Value, Baseline = f.Baseline,
                        Attitude = f.Attitude, Grievance = f.Grievance, NextEventTick = f.NextEventTick, Warned = sector.FactionWarned(f.Id),
                        RaidGateId = f.RaidGateId.Value, RaidEndTick = f.RaidGateId.IsNone ? 0 : f.RaidEndTick,
                        NextPatrolTick = f.NextPatrolTick, PatrolShipsBuilt = f.PatrolShipsBuilt,
                        Invading = f.Invading, Goodwill = f.Goodwill, TruceUntilTick = f.TruceUntilTick, NextStationTick = f.NextStationTick,
                    }).ToList()
                    : null,
                Laws = sector.ActiveLaws.Count > 0 ? sector.ActiveLaws.ToList() : null,
                Events = sector.Events.Count > 0 ? sector.Events.Select(e => new EventDto { Tick = e.Tick, Text = e.Text, Kind = (int)e.Kind, CraftId = e.Target.Craft.Value, BodyId = e.Target.Body.Value }).ToList() : null,
                Milestones = sector.MilestonesReached.Count > 0 ? new SortedDictionary<string, long>(sector.MilestonesReached.ToDictionary(m => m.Key, m => m.Value), StringComparer.Ordinal) : null,
                MilestonesStarted = sector.MilestonesStarted,
                LargeDesigns = sector.LargeDesigns,
                Shots = sector.Shots.Count > 0
                    ? sector.Shots.Select(s => new ShotDto { Position = Vec(s.Position), Velocity = Vec(s.Velocity), ExpireTick = s.ExpireTick, Damage = s.Damage, FactionId = s.FactionId, IsMissile = s.IsMissile, TargetId = s.TargetId.Value, SpeedMps = s.SpeedMps }).ToList()
                    : null,
                NextSwarmId = sector.NextSwarmId > 1 ? sector.NextSwarmId : 0,
                Swarms = sector.Swarms.Count > 0
                    ? sector.Swarms.Select(s => new SwarmDto
                    {
                        Id = s.Id.Value, Name = s.Name, Design = DesignToDto(s.Design), Count = s.Count, SourceBodyId = s.SourceBodyId.Value,
                        MaterialId = s.MaterialId, DestinationId = s.DestinationId.Value, ReturnFraction = s.ReturnFraction,
                        InventoryKg = s.Inventory.Kg.Count > 0 ? new SortedDictionary<string, long>(s.Inventory.Kg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal) : null,
                        StoredEnergyKJ = s.StoredEnergyKJ, MineRemainderKg = s.MineRemainderKg, DeliverRemainderKg = s.DeliverRemainderKg,
                        DeliveredKg = s.DeliveredKg, GrowYardId = s.GrowYardId.Value, GrowRemaining = s.GrowRemaining,
                    }).ToList()
                    : null,
                EjectedKg = sector.EjectedKg.Count > 0
                    ? new SortedDictionary<string, long>(sector.EjectedKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
                    : null,
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
                    OriginalMassKg = b.OriginalMassKg != b.MassKg ? b.OriginalMassKg : 0,
                    StarClass = b.StarClass,
                    Craters = b.Craters.Count > 0 ? b.Craters.Select(c => new CraterDto { BearingRad = c.BearingRad, MassKg = c.MassKg }).ToList() : null,
                    DensityKgPerM3 = b.DensityKgPerM3,
                    IsMineable = b.IsMineable,
                    DepositsKg = b.DepositsKg.Count > 0
                        ? new SortedDictionary<string, long>(b.DepositsKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
                        : null,
                    Orbit = b.Orbit is { } o ? OrbitToDto(o) : null,
                    Surveyed = b.Surveyed,
                    HasLife = b.HasLife,
                    FloraHazard = b.FloraHazard,
                    LifeFound = b.LifeFound,
                    LifeSampled = b.LifeSampled,
                    HasAnimals = b.HasAnimals,
                    AnimalName = b.AnimalName.Length > 0 ? b.AnimalName : null,
                    AnimalTemper = b.AnimalTemper,
                    Sites = b.Sites.Count > 0
                        ? b.Sites.Select(s => new SiteDto
                        {
                            Surveyed = s.Surveyed,
                            Flora = s.Flora,
                            Fauna = s.Fauna,
                            DepositsKg = s.DepositsKg.Count > 0 ? new SortedDictionary<string, long>(s.DepositsKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal) : null,
                        }).ToList()
                        : null,
                }).ToList(),
                Crafts = sector.Crafts.Select(c => new CraftDto
                {
                    Id = c.Id.Value,
                    Name = c.Name,
                    Design = DesignToDto(c.Design),
                    Position = Vec(c.Position),
                    Velocity = Vec(c.Velocity),
                    HeadingRad = c.HeadingRad,
                    Controls = new ControlsDto { Throttle = c.Controls.Throttle, Turn = c.Controls.Turn, Brake = c.Controls.Brake, Strafe = c.Controls.Strafe },
                    TargetBodyId = c.TargetBodyId.Value,
                    TargetCraftId = c.TargetCraftId.Value,
                    DockedToCraftId = c.DockedToCraftId.Value,
                    DockPort = c.DockPortIndex,
                    LandedOnBodyId = c.LandedOnBodyId.Value,
                    LandedOffset = c.IsLanded ? Vec(c.LandedOffset) : null,
                    Anchored = c.IsAnchored,
                    LegacyUnlimitedStorage = c.LegacyUnlimitedStorage,
                    StoredEnergyKJ = c.StoredEnergyKJ,
                    GeneratorBankKJ = c.GeneratorBankKJ.Count > 0 ? new SortedDictionary<string, double>(c.GeneratorBankKJ, StringComparer.Ordinal) : null,
                    ConverterProgressKJ = c.ConverterProgressKJ.Count > 0 ? new SortedDictionary<string, double>(c.ConverterProgressKJ, StringComparer.Ordinal) : null,
                    LegacyFreePower = c.LegacyFreePower,
                    EjectionRemainderKg = c.EjectionRemainderKg,
                    Cruising = c.Cruising,
                    PartHealth = c.PartHealth.Count > 0
                        ? c.PartHealth.OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
                            .Select(kv => new HealthDto { X = kv.Key.X, Y = kv.Key.Y, Health = kv.Value }).ToList()
                        : null,
                    PartsLost = c.PartsLost,
                    IntactDesign = c.IntactDesign is { } intact ? DesignToDto(intact) : null,
                    RepairProgressKg = c.RepairProgressKg,
                    ServiceAtId = c.ServiceAt.Value,
                    YardOrder = c.YardOrder is { } yo
                        ? new YardOrderDto { Kind = yo.Kind, YardId = yo.YardId.Value, Target = yo.Target is { } t ? DesignToDto(t) : null }
                        : null,
                    YardProgressKg = c.YardProgressKg,
                    GateLinkId = c.GateLinkId.Value,
                    GateLostStarId = c.GateLostStarId.Value,
                    GateReadyTick = c.GateReadyTick,
                    JumpRequestTick = c.JumpRequestTick,
                    Jumps = c.Jumps,
                    FactionId = c.FactionId,
                    IsWreck = c.IsWreck,
                    IsPatrol = c.IsPatrol,
                    PatrolStop = c.PatrolStop,
                    IsTradeStation = c.IsTradeStation,
                    PenKg = c.PenKg,
                    FlyToPhase = c.FlyToPhase,
                    GateClosed = c.GateClosed,
                    ContainmentFailS = c.ContainmentFailS,
                    QuantumChargeKJ = c.QuantumChargeKJ,
                    QuantumRequested = c.QuantumRequested,
                    QuantumReadyTick = c.QuantumReadyTick,
                    PenAnimal = c.PenAnimal.Length > 0 ? c.PenAnimal : null,
                    PenTemper = c.PenTemper,
                    Culling = c.Culling,
                    RanchRemainderKg = c.RanchRemainderKg,
                    CullRemainderKg = c.CullRemainderKg,
                    HoldFire = c.HoldFire,
                    SalvageProgressKg = c.SalvageProgressKg,
                    ShieldCharge = c.ShieldCharge,
                    ShieldHitTick = c.ShieldHitTick >= 0 ? c.ShieldHitTick : null,
                    WeaponReady = c.WeaponReadyTicks.Any(w => w.Value > sector.Tick)
                        ? c.WeaponReadyTicks.Where(w => w.Value > sector.Tick).OrderBy(w => w.Key.Y).ThenBy(w => w.Key.X)
                            .Select(w => new WeaponReadyDto { X = w.Key.X, Y = w.Key.Y, Tick = w.Value }).ToList()
                        : null,
                    LabRemainderKg = c.LabRemainderKg,
                    Crew = c.Crew,
                    DockClaimStationId = c.DockClaimStation.Value,
                    Transit = c.Transit is { } leg
                        ? new TransitDto
                        {
                            StartTick = leg.StartTick, ArriveTick = leg.ArriveTick, Start = Vec(leg.Start),
                            TargetBodyId = leg.TargetBody.Value, TargetCraftId = leg.TargetCraft.Value, AngleRad = leg.AngleRad, RadiusM = leg.RadiusM,
                        }
                        : null,
                    DockClaimPort = c.DockClaimPort,
                    FoodRemainderKg = c.FoodRemainderKg,
                    WaterRemainderKg = c.WaterRemainderKg,
                    FarmRemainderKg = c.FarmRemainderKg,
                    CrewGrowthS = c.CrewGrowthProgressS,
                    LegacyNoCrew = c.LegacyNoCrew,
                    CruiseFuelRemainderKg = c.CruiseFuelRemainderKg,
                    PropellantDebtKg = c.PropellantDebtKg.Count > 0 ? new SortedDictionary<string, double>(c.PropellantDebtKg, StringComparer.Ordinal) : null,
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
                        TradeJob tj => new JobDto
                        {
                            Kind = tj.Kind, DestinationId = tj.HomeId.Value, PickupId = tj.TraderId.Value, MaterialId = tj.GetMaterial, GiveMaterial = tj.GiveMaterial,
                            TradeState = tj.State, TradeResumeState = tj.ResumeState, Active = tj.Active, WaitReason = tj.WaitReason,
                            CompletedCycles = tj.CompletedCycles, DeliveredKg = tj.DeliveredKg, RetryAtTick = tj.RetryAtTick,
                        },
                        DefendJob dj => new JobDto
                        {
                            Kind = dj.Kind, PickupId = dj.PostId.Value, SourceBodyId = dj.StarId.Value, DestinationId = dj.RearmAt.Value, DefendState = dj.State,
                            PatrolIndex = dj.PatrolIndex, Active = dj.Active, WaitReason = dj.WaitReason, CompletedCycles = dj.CompletedCycles, RetryAtTick = dj.RetryAtTick,
                        },
                        SalvageJob sg => new JobDto
                        {
                            Kind = sg.Kind, PickupId = sg.WreckId.Value, DestinationId = sg.DestinationId.Value, SalvageState = sg.State, Active = sg.Active,
                            WaitReason = sg.WaitReason, CompletedCycles = sg.CompletedCycles, DeliveredKg = sg.DeliveredKg, RetryAtTick = sg.RetryAtTick,
                        },
                        SurveyJob sv => new JobDto
                        {
                            Kind = sv.Kind, SourceBodyId = sv.TargetBodyId.Value, SurveyState = sv.State, Active = sv.Active,
                            WaitReason = sv.WaitReason, CompletedCycles = sv.CompletedCycles, RetryAtTick = sv.RetryAtTick,
                        },
                        MiningJob j => new JobDto
                        {
                            Kind = j.Kind, SourceBodyId = j.SourceBodyId.Value, MaterialId = j.MaterialId, DestinationId = j.DestinationId.Value,
                            ReturnFraction = j.ReturnFraction, Active = j.Active, State = j.State, ResumeState = j.ResumeState, SpotBearingRad = j.SpotBearingRad,
                            WaitReason = j.WaitReason, CompletedCycles = j.CompletedCycles, DeliveredKg = j.DeliveredKg, RetryAtTick = j.RetryAtTick,
                        },
                        HaulJob h => new JobDto
                        {
                            Kind = h.Kind, PickupId = h.PickupId.Value, MaterialId = h.MaterialId, DestinationId = h.DestinationId.Value,
                            MinPickupKg = h.MinPickupKg, KeepStockedKg = h.KeepStockedKg, Active = h.Active,
                            HaulState = h.State, HaulResumeState = h.ResumeState,
                            WaitReason = h.WaitReason, CompletedCycles = h.CompletedCycles, DeliveredKg = h.DeliveredKg, RetryAtTick = h.RetryAtTick,
                        },
                        ServeRequestsJob s => new JobDto
                        {
                            Kind = s.Kind, MinPickupKg = s.MinLoadKg, Active = s.Active,
                            HaulState = s.State, HaulResumeState = s.ResumeState,
                            PickupId = s.Contract?.PickupId.Value ?? 0, DestinationId = s.Contract?.DestinationId.Value ?? 0,
                            MaterialId = s.Contract?.MaterialId, ContractAmountKg = s.Contract?.AmountKg ?? 0,
                            WaitReason = s.WaitReason, CompletedCycles = s.CompletedCycles, DeliveredKg = s.DeliveredKg, RetryAtTick = s.RetryAtTick,
                        },
                        _ => null,
                    },
                    RequestsKg = c.Requests.Count > 0
                        ? new SortedDictionary<string, long>(c.Requests.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
                        : null,
                    Kits = c.Kits.Count > 0
                        ? c.Kits.Select(k => new KitDto
                        {
                            Id = k.Id.Value, Name = k.Name, Design = DesignToDto(k.Design),
                            OriginalCraftId = k.OriginalCraftId.Value,
                            RequestsKg = k.Requests.Count > 0 ? new SortedDictionary<string, long>(k.Requests.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal) : null,
                            MaterialsKg = new SortedDictionary<string, long>(k.MaterialsKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
                        }).ToList()
                        : null,
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
                            SwarmId = o.SwarmId.Value,
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
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) throw new FormatException("Save file is empty.");
            if (root["game"]?.GetValue<string>() is var game && game != GameId) throw new FormatException($"Not a {GameId} save (game '{game}').");
            var version = root["schemaVersion"]?.GetValue<int>() ?? 0;
            if (version < OldestReadableSchemaVersion || version > SchemaVersion)
                throw new FormatException($"Save schemaVersion {version}; this build reads {OldestReadableSchemaVersion} to {SchemaVersion}.");
            SaveMigrations.Migrate(root, catalog); // older versions become the current format here
            file = root.Deserialize<SaveFile>(Options);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new FormatException($"Save file is not valid JSON: {e.Message}", e);
        }

        if (file is null) throw new FormatException("Save file is empty.");
        var dto = file.Sector ?? throw new FormatException("Save has no sector.");

        try
        {
            var sector = new SectorState(dto.Seed, dto.Tick) { Recipes = catalog.Recipes, Parts = catalog };
            if (dto.Tuning is { } t)
            {
                sector.Tuning.DrillRateMultiplier = t.DrillRateMultiplier;
                sector.Tuning.RefiningRateMultiplier = t.RefiningRateMultiplier;
                sector.Tuning.FabricationRateMultiplier = t.FabricationRateMultiplier;
            }
            AddBodiesParentsFirst(sector, dto.Bodies ?? new List<BodyDto>());

            foreach (var c in dto.Crafts ?? new List<CraftDto>())
            {
                var design = DesignFromDto(c.Design, $"Craft {c.Id}");
                // Every part must exist. Roles aren't checked: damage can break off a cockpit or the
                // last thruster (#34), and such a wreck is still a valid craft in a save.
                if (design.Cells.Count == 0) throw new FormatException($"Craft {c.Id} has no parts.");
                foreach (var cell in design.Cells)
                    if (!catalog.TryGet(cell.PartId, out _))
                        throw new FormatException($"Craft {c.Id} design '{design.Id}' is invalid: unknown part '{cell.PartId}' at ({cell.Pos.X},{cell.Pos.Y}).");

                var craft = new CraftState(new CraftId(c.Id), c.Name ?? "", design, design.ComputeStats(catalog),
                    ToVec(c.Position, "position"), c.HeadingRad)
                {
                    Velocity = ToVec(c.Velocity, "velocity"),
                    Controls = c.Controls is { } k ? new ShipControls(k.Throttle, k.Turn, k.Brake, k.Strafe) : ShipControls.Idle,
                };
                sector.AddCraft(craft);
                sector.SetTarget(craft.Id, new BodyId(c.TargetBodyId));
                if (c.LandedOnBodyId != 0)
                    sector.RestoreLanding(craft.Id, new BodyId(c.LandedOnBodyId), ToVec(c.LandedOffset, "landedOffset"));
                sector.RestoreAnchored(craft.Id, c.Anchored);
                craft.LegacyUnlimitedStorage = c.LegacyUnlimitedStorage;
                craft.LegacyFreePower = c.LegacyFreePower;
                if (c.StoredEnergyKJ < 0 || c.StoredEnergyKJ > craft.Stats.EnergyCapacityKJ + 1e-6)
                    throw new FormatException($"Craft {c.Id} holds {c.StoredEnergyKJ} kJ but its batteries take {craft.Stats.EnergyCapacityKJ} kJ.");
                craft.StoredEnergyKJ = c.StoredEnergyKJ;
                foreach (var (part, kj) in c.GeneratorBankKJ ?? new SortedDictionary<string, double>()) // #75
                {
                    if (!(kj >= 0) || !double.IsFinite(kj)) throw new FormatException($"Craft {c.Id} has an invalid generator store.");
                    if (kj > 0) craft.GeneratorBankKJ[part] = kj;
                }
                foreach (var (part, kj) in c.ConverterProgressKJ ?? new SortedDictionary<string, double>()) // #78
                {
                    if (!(kj >= 0) || !double.IsFinite(kj)) throw new FormatException($"Craft {c.Id} has invalid converter progress.");
                    if (kj > 0) craft.ConverterProgressKJ[part] = kj;
                }
                if (c.EjectionRemainderKg is < 0 or >= 1) throw new FormatException($"Craft {c.Id} has an ejection remainder of {c.EjectionRemainderKg} kg.");
                craft.EjectionRemainderKg = c.EjectionRemainderKg;
                if (c.CruiseFuelRemainderKg is < 0 or >= 1) throw new FormatException($"Craft {c.Id} has a cruise fuel remainder of {c.CruiseFuelRemainderKg} kg.");
                craft.Cruising = c.Cruising;
                sector.RestoreCrew(craft.Id, c.Crew, c.FoodRemainderKg, c.WaterRemainderKg, c.FarmRemainderKg, c.CrewGrowthS, c.LegacyNoCrew);
                sector.RestoreHealth(craft.Id,
                    (c.PartHealth ?? new List<HealthDto>()).ToDictionary(h => new GridPos(h.X, h.Y), h => h.Health), c.PartsLost);
                craft.FactionId = c.FactionId; // #84
                craft.IsWreck = c.IsWreck;
                if (c.PatrolStop < 0) throw new FormatException($"Craft {c.Id} has an invalid patrol.");
                craft.IsPatrol = c.IsPatrol;
                craft.PatrolStop = c.PatrolStop;
                craft.IsTradeStation = c.IsTradeStation;
                if (c.PenKg < 0 || !(c.RanchRemainderKg >= 0 && c.RanchRemainderKg < 1) || !(c.CullRemainderKg >= 0 && c.CullRemainderKg < 1))
                    throw new FormatException($"Craft {c.Id} has invalid animals.");
                craft.PenKg = c.PenKg;
                if (!(c.ContainmentFailS >= 0) || !(c.QuantumChargeKJ >= 0)) throw new FormatException($"Craft {c.Id} has an invalid quantum drive or containment.");
                craft.GateClosed = c.GateClosed;
                craft.ContainmentFailS = c.ContainmentFailS;
                craft.QuantumChargeKJ = c.QuantumChargeKJ;
                craft.QuantumRequested = c.QuantumRequested;
                craft.QuantumReadyTick = c.QuantumReadyTick;
                craft.PenAnimal = c.PenAnimal ?? "";
                craft.PenTemper = c.PenTemper;
                craft.Culling = c.Culling;
                craft.RanchRemainderKg = c.RanchRemainderKg;
                craft.CullRemainderKg = c.CullRemainderKg;
                craft.HoldFire = c.HoldFire;
                if (!(c.SalvageProgressKg >= 0)) throw new FormatException($"Craft {c.Id} has invalid salvage progress.");
                craft.SalvageProgressKg = c.SalvageProgressKg;
                if (!(c.ShieldCharge >= 0)) throw new FormatException($"Craft {c.Id} has an invalid shield charge.");
                craft.ShieldCharge = c.ShieldCharge;
                if (c.ShieldHitTick is { } hitTick) craft.ShieldHitTick = hitTick;
                foreach (var w in c.WeaponReady ?? new List<WeaponReadyDto>()) craft.WeaponReadyTicks[new GridPos(w.X, w.Y)] = w.Tick;
                craft.CruiseFuelRemainderKg = c.CruiseFuelRemainderKg;
                foreach (var (m, kg) in c.PropellantDebtKg ?? new SortedDictionary<string, double>()) // #80
                {
                    if (kg is < 0 or >= 1) throw new FormatException($"Craft {c.Id} has a propellant remainder of {kg} kg.");
                    if (kg > 0) craft.PropellantDebtKg[m] = kg;
                }
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
                    var orderErrors = orderDesign.Validate(catalog, orderDesign.SuggestedRole(catalog), requireConnected: false);
                    if (orderErrors.Count > 0)
                        throw new FormatException($"Order {o.OrderId} design '{orderDesign.Id}' is invalid: {string.Join("; ", orderErrors)}");
                    var materials = o.MaterialsKg ?? new SortedDictionary<string, long>();
                    if (materials.Any(kv => !catalog.Materials.Contains(kv.Key) || kv.Value < 0))
                        throw new FormatException($"Order {o.OrderId} holds unknown or negative materials.");
                    sector.RestoreOrder(craft.Id, new BuildOrder(new OrderId(o.OrderId), o.ShipName ?? "", orderDesign,
                        orderDesign.ComputeStats(catalog), materials, o.ProgressKg, isKit: orderDesign.SuggestedRole(catalog) == DesignRole.Station) { SwarmId = new SwarmId(o.SwarmId) });
                }
                foreach (var kitDto in c.Kits ?? new List<KitDto>())
                {
                    var kitDesign = DesignFromDto(kitDto.Design, $"Kit {kitDto.Id}");
                    var kitErrors = kitDesign.Validate(catalog, DesignRole.Station, requireConnected: false);
                    if (kitErrors.Count > 0) throw new FormatException($"Kit {kitDto.Id} design '{kitDesign.Id}' is invalid: {string.Join("; ", kitErrors)}");
                    var materials = kitDto.MaterialsKg ?? new SortedDictionary<string, long>();
                    if (materials.Any(kv => !catalog.Materials.Contains(kv.Key)))
                        throw new FormatException($"Kit {kitDto.Id} holds unknown materials.");
                    var kitRequests = kitDto.RequestsKg ?? new SortedDictionary<string, long>();
                    if (kitRequests.Any(kv => !catalog.Materials.Contains(kv.Key) || kv.Value <= 0))
                        throw new FormatException($"Kit {kitDto.Id} has invalid requests.");
                    sector.RestoreKit(craft.Id, new ConstructionKit(new OrderId(kitDto.Id), kitDto.Name ?? "", kitDesign, kitDesign.ComputeStats(catalog), materials)
                    {
                        OriginalCraftId = new CraftId(kitDto.OriginalCraftId), // a retired id: it doesn't exist now
                        Requests = kitRequests,
                    });
                }
                foreach (var (material, kg) in c.RequestsKg ?? new SortedDictionary<string, long>())
                {
                    if (!catalog.Materials.Contains(material)) throw new FormatException($"Craft {c.Id} requests unknown material '{material}'.");
                    sector.RestoreRequest(craft.Id, material, kg);
                }
            }
            if (dto.NextOrderId > 0) sector.RestoreNextOrderId(dto.NextOrderId);
            if (dto.NextCraftId > 0) sector.RestoreNextCraftId(dto.NextCraftId);
            if (dto.NextBodyId > 0) sector.RestoreNextBodyId(dto.NextBodyId);
            if (dto.DiscoveredKg < 0) throw new FormatException("Negative discovered mass.");
            sector.RestoreDiscovered(dto.DiscoveredKg);
            if (!(dto.ResearchPoints >= 0)) throw new FormatException("Negative research points.");
            sector.RestoreResearch(dto.ResearchPoints, dto.UnlockedTech ?? new List<string>(), dto.ResearchAllUnlocked);
            // Civilizations, laws and the event log (#36), all optional.
            foreach (var f in dto.Factions ?? new List<FactionDto>())
            {
                if (sector.Bodies.All(b => b.Id.Value != f.HomeStarId)) throw new FormatException($"Civilization {f.Id} claims a missing system.");
                if (!double.IsFinite(f.Attitude) || !(f.Grievance >= 0) || !(f.Goodwill >= 0)) throw new FormatException($"Civilization {f.Id} has invalid attitude.");
                sector.RestoreFaction(f.Id, f.Name ?? $"Civilization {f.Id}", f.Culture ?? "", new BodyId(f.HomeStarId), f.Baseline, f.Attitude, f.Grievance, f.NextEventTick, f.Warned, new CraftId(f.RaidGateId), f.RaidEndTick,
                    f.NextPatrolTick, f.PatrolShipsBuilt, f.Goodwill, f.TruceUntilTick, f.NextStationTick, f.Invading);
            }
            sector.RestoreLaws(dto.Laws ?? new List<string>());
            sector.RestoreEvents((dto.Events ?? new List<EventDto>()).Select(e => new SectorEvent(e.Tick, e.Text ?? "",
                e.Kind >= 0 && e.Kind <= (int)EventKind.Message ? (EventKind)e.Kind : EventKind.Other, new TargetRef(new CraftId(e.CraftId), new BodyId(e.BodyId)))));
            foreach (var (material, kg) in dto.EjectedKg ?? new SortedDictionary<string, long>())
            {
                if (!catalog.Materials.Contains(material)) throw new FormatException($"Unknown ejected material '{material}'.");
                sector.RestoreEjected(material, kg);
            }

            foreach (var s in dto.Swarms ?? new List<SwarmDto>()) // #30; the source or destination may be gone since: it waits with that reason
            {
                var design = DesignFromDto(s.Design, $"Swarm {s.Id}");
                var swarmErrors = design.Validate(catalog, DesignRole.Ship, requireConnected: false);
                if (swarmErrors.Count > 0) throw new FormatException($"Swarm {s.Id} design is invalid: {string.Join("; ", swarmErrors)}");
                if (!catalog.Materials.Contains(s.MaterialId ?? "")) throw new FormatException($"Swarm {s.Id} mines unknown material '{s.MaterialId}'.");
                if (s.MineRemainderKg is < 0 or >= 1 || s.DeliverRemainderKg is < 0 or >= 1 || s.GrowRemaining < 0 || s.DeliveredKg < 0)
                    throw new FormatException($"Swarm {s.Id} has invalid progress.");
                var swarm = new SwarmState(new SwarmId(s.Id), s.Name ?? $"Swarm {s.Id}", design, design.ComputeStats(catalog), s.Count,
                    new BodyId(s.SourceBodyId), s.MaterialId!, new CraftId(s.DestinationId), s.ReturnFraction)
                {
                    StoredEnergyKJ = s.StoredEnergyKJ, MineRemainderKg = s.MineRemainderKg, DeliverRemainderKg = s.DeliverRemainderKg,
                    DeliveredKg = s.DeliveredKg, GrowYardId = new CraftId(s.GrowYardId), GrowRemaining = s.GrowRemaining,
                };
                if (!(s.StoredEnergyKJ >= 0) || s.StoredEnergyKJ > swarm.Count * swarm.Stats.EnergyCapacityKJ * (1 + 1e-9) + 1e-6)
                    throw new FormatException($"Swarm {s.Id} holds {s.StoredEnergyKJ} kJ.");
                foreach (var (material, kg) in s.InventoryKg ?? new SortedDictionary<string, long>())
                {
                    if (!catalog.Materials.Contains(material) || kg <= 0) throw new FormatException($"Swarm {s.Id} holds {kg} kg of '{material}'.");
                    swarm.Inventory.Add(material, kg);
                }
                sector.RestoreSwarm(swarm);
            }
            sector.RestoreNextSwarmId(dto.NextSwarmId);
            foreach (var (id, tick) in dto.Milestones ?? new SortedDictionary<string, long>()) // #38
            {
                if (tick < 0) throw new FormatException($"Milestone {id} has an invalid time.");
                sector.RestoreMilestone(id, tick);
            }
            sector.MilestonesStarted = dto.MilestonesStarted;
            sector.LargeDesigns = dto.LargeDesigns;
            foreach (var s in dto.Shots ?? new List<ShotDto>())
            {
                if (!(s.Damage >= 0)) throw new FormatException("A shot has invalid damage.");
                sector.RestoreShot(new Shot { Position = ToVec(s.Position, "shot position"), Velocity = ToVec(s.Velocity, "shot velocity"), ExpireTick = s.ExpireTick, Damage = s.Damage, FactionId = s.FactionId, IsMissile = s.IsMissile, TargetId = new CraftId(s.TargetId), SpeedMps = s.SpeedMps });
            }

            // Links between crafts, after every craft exists.
            foreach (var c in dto.Crafts ?? new List<CraftDto>())
            {
                var flyTo = c.FlyToPhase;
                if (flyTo is < 0 or > 2) throw new FormatException($"Craft {c.Id} has an invalid fly-to phase.");
                if (c.TargetCraftId != 0) sector.SetTargetCraft(new CraftId(c.Id), new CraftId(c.TargetCraftId)); sector.GetCraft(new CraftId(c.Id)).FlyToPhase = flyTo;
                if (c.DockedToCraftId != 0) sector.RestoreDock(new CraftId(c.Id), new CraftId(c.DockedToCraftId), c.DockPort);
                if (c.DockClaimStationId != 0) sector.RestoreDockClaim(new CraftId(c.Id), new CraftId(c.DockClaimStationId), c.DockClaimPort);
                if (c.Transit is { } leg)
                    sector.RestoreTransit(new CraftId(c.Id), new TransitLeg(leg.StartTick, leg.ArriveTick, ToVec(leg.Start, "transit.start"),
                        new BodyId(leg.TargetBodyId), new CraftId(leg.TargetCraftId), leg.AngleRad, leg.RadiusM));
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
                ShipDesign? intactDesign = null;
                if (c.IntactDesign is not null)
                {
                    intactDesign = DesignFromDto(c.IntactDesign, $"Craft {c.Id} intact design");
                    var intactErrors = intactDesign.Validate(catalog, intactDesign.SuggestedRole(catalog), requireConnected: false);
                    if (intactErrors.Count > 0) throw new FormatException($"Craft {c.Id} intact design is invalid: {string.Join("; ", intactErrors)}");
                }
                if (c.RepairProgressKg < 0) throw new FormatException($"Craft {c.Id} has negative repair progress.");
                sector.RestoreRepair(restored.Id, intactDesign, c.RepairProgressKg, new CraftId(c.ServiceAtId));
                if (c.GateReadyTick < 0 || c.JumpRequestTick < 0 || c.Jumps < 0) throw new FormatException($"Craft {c.Id} has invalid warp gate data.");
                if (c.LabRemainderKg is < 0 or >= 1) throw new FormatException($"Craft {c.Id} has a lab remainder of {c.LabRemainderKg} kg.");
                restored.LabRemainderKg = c.LabRemainderKg;
                sector.RestoreGate(restored.Id, new CraftId(c.GateLinkId), c.GateReadyTick, c.JumpRequestTick, c.Jumps);
                if (c.GateLostStarId != 0) sector.RestoreGateLost(restored.Id, new BodyId(c.GateLostStarId));
                if (c.YardOrder is { } yod)
                {
                    ShipDesign? target = null;
                    if (yod.Target is not null)
                    {
                        target = DesignFromDto(yod.Target, $"Craft {c.Id} refit target");
                        var targetErrors = target.Validate(catalog, target.SuggestedRole(catalog), requireConnected: false);
                        if (targetErrors.Count > 0) throw new FormatException($"Craft {c.Id} refit target is invalid: {string.Join("; ", targetErrors)}");
                    }
                    if (c.YardProgressKg < 0) throw new FormatException($"Craft {c.Id} has negative yard progress.");
                    sector.RestoreYardOrder(restored.Id, new YardOrder(yod.Kind, new CraftId(yod.YardId), target), c.YardProgressKg);
                }
                if (c.Job is { Kind: "trade" } trade)
                {
                    if (!catalog.Materials.Contains(trade.MaterialId ?? "") || !catalog.Materials.Contains(trade.GiveMaterial ?? ""))
                        throw new FormatException($"Craft {c.Id} trades unknown materials.");
                    // Either station may be gone since: the job waits with that reason.
                    sector.RestoreJob(restored.Id, new TradeJob(new CraftId(trade.DestinationId), trade.GiveMaterial!, new CraftId(trade.PickupId), trade.MaterialId!)
                    {
                        Active = trade.Active, State = trade.TradeState, ResumeState = trade.TradeResumeState, WaitReason = trade.WaitReason,
                        CompletedCycles = trade.CompletedCycles, DeliveredKg = trade.DeliveredKg, RetryAtTick = trade.RetryAtTick,
                    });
                }
                else if (c.Job is { Kind: "guard" or "escort" or "patrol" } defend)
                {
                    // What it protects may be gone since: the job waits with that reason.
                    var order = defend.Kind == "patrol" ? new DefendJob(new BodyId(defend.SourceBodyId))
                        : new DefendJob(defend.Kind == "guard" ? DefendMode.Guard : DefendMode.Escort, new CraftId(defend.PickupId));
                    if (defend.PatrolIndex < 0) throw new FormatException($"Craft {c.Id} has an invalid patrol.");
                    order.Active = defend.Active;
                    order.State = defend.DefendState;
                    order.PatrolIndex = defend.PatrolIndex;
                    order.RearmAt = new CraftId(defend.DestinationId);
                    order.WaitReason = defend.WaitReason;
                    order.CompletedCycles = defend.CompletedCycles;
                    order.RetryAtTick = defend.RetryAtTick;
                    sector.RestoreJob(restored.Id, order);
                }
                else if (c.Job is { Kind: "salvage" } salvage)
                {
                    // The wreck or the destination may be gone since: the job picks another, or waits with that reason.
                    sector.RestoreJob(restored.Id, new SalvageJob(new CraftId(salvage.DestinationId))
                    {
                        Active = salvage.Active, State = salvage.SalvageState, WreckId = new CraftId(salvage.PickupId), WaitReason = salvage.WaitReason,
                        CompletedCycles = salvage.CompletedCycles, DeliveredKg = salvage.DeliveredKg, RetryAtTick = salvage.RetryAtTick,
                    });
                }
                else if (c.Job is { Kind: "survey" } sj)
                {
                    // The target may have been mined away since (#27): the scout picks another.
                    sector.RestoreJob(restored.Id, new SurveyJob
                    {
                        Active = sj.Active, State = sj.SurveyState, TargetBodyId = new BodyId(sj.SourceBodyId), WaitReason = sj.WaitReason,
                        CompletedCycles = sj.CompletedCycles, RetryAtTick = sj.RetryAtTick,
                    });
                }
                else if (c.Job is { Kind: "requests" } r)
                {
                    HaulContract? contract = null;
                    if (r.ContractAmountKg > 0)
                    {
                        if (!catalog.Materials.Contains(r.MaterialId ?? "")) throw new FormatException($"Craft {c.Id} job uses unknown material '{r.MaterialId}'.");
                        // The stations may have been dismantled since (#57): the job drops the trip when it sees that.
                        contract = new HaulContract(new CraftId(r.PickupId), new CraftId(r.DestinationId), r.MaterialId!, r.ContractAmountKg).Validated();
                    }
                    sector.RestoreJob(restored.Id, new ServeRequestsJob(r.MinPickupKg)
                    {
                        Active = r.Active, State = r.HaulState, ResumeState = r.HaulResumeState, WaitReason = r.WaitReason, Contract = contract,
                        CompletedCycles = r.CompletedCycles, DeliveredKg = r.DeliveredKg, RetryAtTick = r.RetryAtTick,
                    });
                }
                else if (c.Job is { } j)
                {
                    if (!catalog.Materials.Contains(j.MaterialId ?? "")) throw new FormatException($"Craft {c.Id} job uses unknown material '{j.MaterialId}'.");
                    // Stations a job names may have been dismantled since (#57): the job waits with that reason.
                    if (j.Kind == "freight")
                    {
                        sector.RestoreJob(restored.Id, new HaulJob(new CraftId(j.PickupId), j.MaterialId!, new CraftId(j.DestinationId), j.MinPickupKg, j.KeepStockedKg)
                        {
                            Active = j.Active, State = j.HaulState, ResumeState = j.HaulResumeState, WaitReason = j.WaitReason,
                            CompletedCycles = j.CompletedCycles, DeliveredKg = j.DeliveredKg, RetryAtTick = j.RetryAtTick,
                        });
                    }
                    else
                    {
                        // The source may have been mined away entirely (#27): the job waits with that reason.
                        sector.RestoreJob(restored.Id, new MiningJob(new BodyId(j.SourceBodyId), j.MaterialId!, new CraftId(j.DestinationId), j.ReturnFraction)
                        {
                            Active = j.Active, State = j.State, ResumeState = j.ResumeState, WaitReason = j.WaitReason,
                            CompletedCycles = j.CompletedCycles, DeliveredKg = j.DeliveredKg, RetryAtTick = j.RetryAtTick,
                            SpotBearingRad = j.SpotBearingRad,
                        });
                    }
                }
            }

            var player = new CraftId(file.PlayerCraftId);
            if (!player.IsNone) sector.GetCraft(player); // must exist
            sector.PilotCraftId = player;
            return new LoadedGame(sector, player, file.Header);
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
                sector.AddBody(new CelestialBodyState(new BodyId(b.Id), b.Name ?? "", b.Kind, b.MassKg, b.DensityKgPerM3, b.IsMineable, orbit,
                    b.DepositsKg, b.OriginalMassKg > 0 ? b.OriginalMassKg : null));
                sector.GetBody(new BodyId(b.Id)).StarClass = b.StarClass;
                if (b.Craters is { } craters)
                {
                    if (craters.Any(c => c.MassKg <= 0 || !double.IsFinite(c.BearingRad))) throw new FormatException($"Body {b.Id} has an invalid crater.");
                    sector.RestoreCraters(new BodyId(b.Id), craters.Select(c => new Crater(c.BearingRad, c.MassKg)));
                }
                // Sites (#70): saved ones as they were; older saves get the fixed layout from their deposits, surveyed as the body was.
                if (b.Sites is { } sites)
                {
                    try
                    {
                        sector.RestoreSites(new BodyId(b.Id), sites.Select(s => (s.Surveyed, (IReadOnlyDictionary<string, long>)(s.DepositsKg ?? new SortedDictionary<string, long>()))).ToList());
                    }
                    catch (ArgumentException e) { throw new FormatException(e.Message); }
                    var living = sector.GetBody(new BodyId(b.Id));
                    living.HasLife = b.HasLife;
                    living.FloraHazard = b.FloraHazard;
                    living.LifeFound = b.LifeFound;
                    living.LifeSampled = b.LifeSampled;
                    living.HasAnimals = b.HasAnimals;
                    living.AnimalName = b.AnimalName ?? "";
                    living.AnimalTemper = b.AnimalTemper;
                    for (var i = 0; i < sites.Count; i++)
                    {
                        if (!(sites[i].Flora >= 0 && sites[i].Flora <= 1)) throw new FormatException($"Body {b.Id} has invalid plant cover.");
                        if (!(sites[i].Fauna >= 0 && sites[i].Fauna <= 1)) throw new FormatException($"Body {b.Id} has invalid animals.");
                        living.SiteList[i].Flora = sites[i].Flora;
                        living.SiteList[i].Fauna = sites[i].Fauna;
                    }
                }
                else if (b.Surveyed) sector.MarkSurveyed(new BodyId(b.Id));
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

    // --- File shape (schema 10). Change only together with SchemaVersion and docs/08. ---

    private sealed class SaveFile
    {
        public int SchemaVersion { get; set; }
        public string? Game { get; set; }

        /// <summary>#60 (optional, before the sector so a save list can read it alone).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SaveHeader? Header { get; set; }

        public uint PlayerCraftId { get; set; }
        public SectorDto? Sector { get; set; }
    }

    private sealed class SectorDto
    {
        public ulong Seed { get; set; }
        public long Tick { get; set; }
        public long NextOrderId { get; set; }

        /// <summary>v17: next craft id (ids of dismantled craft are never reused). 0 in older files: derived from the craft.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint NextCraftId { get; set; }

        /// <summary>#27 (still v17, optional): next body id (bodies mined away entirely are gone; their ids are never reused).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint NextBodyId { get; set; }

        /// <summary>v18: mineable mass of the star systems generated since the start (keeps the mass ledger balanced).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long DiscoveredKg { get; set; }

        /// <summary>v19: research points to spend, techs researched, and (older saves) everything open.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double ResearchPoints { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? UnlockedTech { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ResearchAllUnlocked { get; set; }

        /// <summary>v19 (later addition, optional, #36): civilizations met, laws in force, and the event log.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<FactionDto>? Factions { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Laws { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<EventDto>? Events { get; set; }

        /// <summary>v29: milestones reached (#38).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, long>? Milestones { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool MilestonesStarted { get; set; }

        /// <summary>v32: large-ship building is open (#111).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LargeDesigns { get; set; }

        /// <summary>v21: shots in flight (#85).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ShotDto>? Shots { get; set; }

        /// <summary>v20: swarms (#30) and the next swarm id.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SwarmDto>? Swarms { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint NextSwarmId { get; set; }

        public SortedDictionary<string, long>? EjectedKg { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] // omitted while every multiplier is 1
        public TuningDto? Tuning { get; set; }
        public List<BodyDto>? Bodies { get; set; }
        public List<CraftDto>? Crafts { get; set; }
    }

    private sealed class FactionDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Culture { get; set; }
        public uint HomeStarId { get; set; }
        public double Baseline { get; set; }
        public double Attitude { get; set; }
        public double Grievance { get; set; }
        public long NextEventTick { get; set; }
        public bool Warned { get; set; }

        /// <summary>v21: the gate it's raiding.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint RaidGateId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long RaidEndTick { get; set; }

        /// <summary>v24: its standing patrol (#89).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long NextPatrolTick { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int PatrolShipsBuilt { get; set; }

        /// <summary>v25: trade and diplomacy (#89).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double Goodwill { get; set; }

        /// <summary>v29: a raid meaning to go through the gate (#95).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Invading { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long TruceUntilTick { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long NextStationTick { get; set; }
    }

    private sealed class EventDto
    {
        public long Tick { get; set; }
        public string? Text { get; set; }

        /// <summary>v31: what it's about (#100).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Kind { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint CraftId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint BodyId { get; set; }
    }

    private sealed class BodyDto
    {
        public uint Id { get; set; }
        public string? Name { get; set; }
        public BodyKind Kind { get; set; }
        public double MassKg { get; set; }

        /// <summary>#27 (still v17, optional): mass when the sector was made, if mining has changed it. 0: the same as massKg.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double OriginalMassKg { get; set; }

        /// <summary>v19: a star's type (#25); omitted for yellow.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public StarClass StarClass { get; set; }

        /// <summary>v19 (later addition, optional, #28): pits dug by mining.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CraterDto>? Craters { get; set; }

        public double DensityKgPerM3 { get; set; }
        public bool IsMineable { get; set; }
        public OrbitDto? Orbit { get; set; }
        public SortedDictionary<string, long>? DepositsKg { get; set; }
        public bool Surveyed { get; set; }

        /// <summary>v19 (later addition, optional, #70): the rim's sites in order. Older saves: made from the deposits.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SiteDto>? Sites { get; set; }

        /// <summary>v26: native life (#91).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HasLife { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public FloraHazard FloraHazard { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LifeFound { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LifeSampled { get; set; }

        /// <summary>v27: native animals (#93).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HasAnimals { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AnimalName { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public AnimalTemper AnimalTemper { get; set; }
    }

    private sealed class SiteDto
    {
        public bool Surveyed { get; set; }

        /// <summary>v26: cover of native plants (#92).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double Flora { get; set; }

        /// <summary>v27: native animals (#93).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double Fauna { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, long>? DepositsKg { get; set; }
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

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] // only on stations from pre-v9 saves
        public bool LegacyUnlimitedStorage { get; set; }

        public double StoredEnergyKJ { get; set; }

        /// <summary>v19 (later addition, optional, #75): energy left from generator batches already burned, by part id.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, double>? GeneratorBankKJ { get; set; }

        /// <summary>v19 (later addition, optional, #78): energy put into each converter's current batch, by part id.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, double>? ConverterProgressKJ { get; set; }

        /// <summary>v19 (later addition, optional, #80): propellant burned but not yet a whole kg, by material.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, double>? PropellantDebtKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] // only on craft from pre-v10 saves
        public bool LegacyFreePower { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double EjectionRemainderKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Cruising { get; set; }

        public List<HealthDto>? PartHealth { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int PartsLost { get; set; }

        /// <summary>v18: for a warp gate, the gate it sends craft to (0 until its first jump).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint GateLinkId { get; set; }

        /// <summary>v19 (later addition, optional, #36): the system whose far gate a raid destroyed.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint GateLostStarId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long GateReadyTick { get; set; }

        /// <summary>v18: the tick this craft asked to jump (its place in the gate's queue); 0 when not asking.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long JumpRequestTick { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Jumps { get; set; }

        /// <summary>v21: combat (#84).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int FactionId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsWreck { get; set; }

        /// <summary>v24: a civilization's standing patrol (#89).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsPatrol { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int PatrolStop { get; set; }

        /// <summary>v25: a civilization's trade station (#89).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsTradeStation { get; set; }

        /// <summary>v27: animals (#93).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long PenKg { get; set; }

        /// <summary>v28: antimatter and the quantum drive (#81, #82).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double ContainmentFailS { get; set; }

        /// <summary>v29: a closed warp gate (#95).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool GateClosed { get; set; }

        /// <summary>v30: flying itself to its target (#108).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int FlyToPhase { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double QuantumChargeKJ { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool QuantumRequested { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long QuantumReadyTick { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PenAnimal { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public AnimalTemper PenTemper { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Culling { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double RanchRemainderKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CullRemainderKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HoldFire { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double SalvageProgressKg { get; set; }

        /// <summary>v23: shields (#90).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double ShieldCharge { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ShieldHitTick { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<WeaponReadyDto>? WeaponReady { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double LabRemainderKg { get; set; }

        /// <summary>v17: a refit, dismantle or pack order a shipyard is working on.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public YardOrderDto? YardOrder { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double YardProgressKg { get; set; }

        /// <summary>v16: the design before parts broke off (only while some are missing).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DesignDto? IntactDesign { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double RepairProgressKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint ServiceAtId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Crew { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint DockClaimStationId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TransitDto? Transit { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int DockClaimPort { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double FoodRemainderKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double WaterRemainderKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double FarmRemainderKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CrewGrowthS { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LegacyNoCrew { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CruiseFuelRemainderKg { get; set; }
        public double MiningRemainderKg { get; set; }
        public BatchDto? RefiningBatch { get; set; }
        public JobDto? Job { get; set; }
        public OrbitDto? RailOrbit { get; set; }
        public SortedDictionary<string, long>? InventoryKg { get; set; }
        public List<OrderDto>? BuildQueue { get; set; }
        public List<KitDto>? Kits { get; set; }
        public SortedDictionary<string, long>? RequestsKg { get; set; }
    }

    private sealed class CraterDto
    {
        public double BearingRad { get; set; }
        public long MassKg { get; set; }
    }

    private sealed class YardOrderDto
    {
        public YardWorkKind Kind { get; set; }
        public uint YardId { get; set; }
        public DesignDto? Target { get; set; }
    }

    private sealed class TransitDto
    {
        public long StartTick { get; set; }
        public long ArriveTick { get; set; }
        public VecDto? Start { get; set; }
        public uint TargetBodyId { get; set; }
        public uint TargetCraftId { get; set; }
        public double AngleRad { get; set; }
        public double RadiusM { get; set; }
    }

    private sealed class HealthDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Health { get; set; }
    }

    private sealed class KitDto
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public DesignDto? Design { get; set; }
        public SortedDictionary<string, long>? MaterialsKg { get; set; }

        /// <summary>#24 (still v17, optional): the station this kit was packed from, which it comes back as.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint OriginalCraftId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, long>? RequestsKg { get; set; }
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
        /// <summary>"mining" (v6 default), "freight" (v7), "requests" (v11, contract in pickup/destination/material/contractAmountKg) or "survey" (v16, target in sourceBodyId).</summary>
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

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long ContractAmountKg { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public SurveyJobState SurveyState { get; set; }

        /// <summary>v25: trade routes (#89); the trade station is in <see cref="PickupId"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GiveMaterial { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public TradeJobState TradeState { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public TradeJobState TradeResumeState { get; set; }

        /// <summary>v24: guard, escort and patrol jobs (#88).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public DefendState DefendState { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int PatrolIndex { get; set; }

        /// <summary>v22: salvage jobs (#86); the wreck is in <see cref="PickupId"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public SalvageJobState SalvageState { get; set; }

        /// <summary>Mining: this trip's landing spot bearing (v14+, #55); null when not picked yet.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SpotBearingRad { get; set; }
    }

    private sealed class ShotDto
    {
        public VecDto? Position { get; set; }
        public VecDto? Velocity { get; set; }
        public long ExpireTick { get; set; }
        public double Damage { get; set; }
        public int FactionId { get; set; }

        /// <summary>v23: missiles (#87).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsMissile { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint TargetId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double SpeedMps { get; set; }
    }

    private sealed class WeaponReadyDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public long Tick { get; set; }
    }

    private sealed class SwarmDto
    {
        public uint Id { get; set; }
        public string? Name { get; set; }
        public DesignDto? Design { get; set; }
        public int Count { get; set; }
        public uint SourceBodyId { get; set; }
        public string? MaterialId { get; set; }
        public uint DestinationId { get; set; }
        public double ReturnFraction { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, long>? InventoryKg { get; set; }
        public double StoredEnergyKJ { get; set; }
        public double MineRemainderKg { get; set; }
        public double DeliverRemainderKg { get; set; }
        public long DeliveredKg { get; set; }
        public uint GrowYardId { get; set; }
        public int GrowRemaining { get; set; }
    }

    private sealed class OrderDto
    {
        public long OrderId { get; set; }
        public string? ShipName { get; set; }
        public DesignDto? Design { get; set; }
        public SortedDictionary<string, long>? MaterialsKg { get; set; }
        public double ProgressKg { get; set; }

        /// <summary>v20: the swarm this ship joins (#30).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint SwarmId { get; set; }
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

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double Strafe { get; set; }
    }
}
