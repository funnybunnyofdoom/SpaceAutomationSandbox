using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>A megastructure being built or built (#126): its kind, where it is, its hub station, and how far it's come.</summary>
public sealed class MegaProject
{
    public MegaProject(int id, string kindId, string name, BodyId bodyId, CraftId hubId) => (Id, KindId, Name, BodyId, HubId) = (id, kindId, name, bodyId, hubId);

    public int Id { get; }
    public string KindId { get; }
    public string Name { get; internal set; }
    public BodyId BodyId { get; }
    public CraftId HubId { get; }

    /// <summary>Stages finished.</summary>
    public int Stages { get; internal set; }

    /// <summary>The next stage's materials have been taken in, and it's being worked on.</summary>
    public bool Working { get; internal set; }
    public long WorkTicks { get; internal set; }
    public bool Paused { get; internal set; }

    /// <summary>Its upkeep wasn't there: it does nothing until it is.</summary>
    public bool Neglected { get; internal set; }

    /// <summary>Materials built into it, kg (part of the mass ledger).</summary>
    public long BuiltKg { get; internal set; }
}

/// <summary>
/// Megastructures (#126; the creator's choices: an orbital ring, a space elevator, a solar swarm and an orbital shipyard dock; special projects
/// with stages; each finished stage gives its share; each needs research; civilizations may object; upkeep). Each is founded at a hub station
/// on rails, which posts requests for the next stage's materials, so ordinary freight and serve-requests freighters bring them. When they're
/// all there the stage takes them in (they become the structure: mass kept exactly) and works for a while on the hub's power. Upkeep is used
/// up every hour from the hub; without it the structure stops working, never falls apart.
/// </summary>
public sealed partial class SectorState
{
    public const double MegaWorkKW = 300;
    private const long UpkeepTicks = 3600 * FixedStepClock.TicksPerSecond;

    private readonly List<MegaProject> _megas = new();

    public IReadOnlyList<MegaProject> Megas => _megas;

    private MegaCatalog MegaKinds => Parts?.Megastructures ?? MegaCatalog.Empty;

    public MegaKind? KindOf(MegaProject project) => MegaKinds.Find(project.KindId);

    internal void RestoreMega(MegaProject project) => _megas.Add(project);

    /// <summary>The share of its effect it gives now: finished stages over all, or nothing while neglected or without its hub.</summary>
    public double Share(MegaProject project) =>
        project.Neglected || KindOf(project) is not { } kind || !_craftsById.ContainsKey(project.HubId) ? 0 : (double)project.Stages / kind.Stages.Count;

    public MegaProject? MegaAt(CraftId hub)
    {
        foreach (var project in _megas) if (project.HubId == hub) return project;
        return null;
    }

    /// <summary>The hub's station: docking ports, storage, a little power; a fabricator for a dock.</summary>
    public static ShipDesign HubDesign(bool builds) => new(builds ? "mega_dock_hub" : "mega_hub", builds ? "Dock hub" : "Megastructure hub", new[]
    {
        new DesignCell(new GridPos(0, 0), builds ? "fabricator" : "hull"), new DesignCell(new GridPos(0, -1), "docking_port"), new DesignCell(new GridPos(0, 1), "docking_port", 2),
        new DesignCell(new GridPos(-1, 0), "storage_silo"), new DesignCell(new GridPos(1, 0), "storage_silo"), new DesignCell(new GridPos(-2, 0), "docking_port", 3), new DesignCell(new GridPos(2, 0), "docking_port", 1),
        new DesignCell(new GridPos(-1, -1), "solar_panel"), new DesignCell(new GridPos(1, -1), "solar_panel"), new DesignCell(new GridPos(-1, 1), "battery"), new DesignCell(new GridPos(1, 1), "battery"),
    });

    // --- Founding ---

    /// <summary>Why one can't be founded at this body, or null.</summary>
    public string? FoundBlocker(MegaKind kind, CelestialBodyState body)
    {
        if (kind.Tech.Length > 0 && !IsTechUnlocked(kind.Tech)) return $"Research {(Tech.TryGet(kind.Tech, out var t) ? t.Name : kind.Tech)} first.";
        var fits = kind.Place switch
        {
            MegaPlace.Star => body.Kind == BodyKind.Star,
            MegaPlace.RockyPlanet => body.Kind is BodyKind.Planet or BodyKind.Moon && body.BulkMaterialId == CelestialBodyState.RockMaterial,
            _ => body.Kind is BodyKind.Planet or BodyKind.GasGiant or BodyKind.Moon,
        };
        if (!fits) return kind.Place switch { MegaPlace.Star => "It goes round a star.", MegaPlace.RockyPlanet => "It stands on a rocky planet or moon.", _ => "It goes round a planet or moon." };
        if (_megas.Any(m => m.KindId == kind.Id && m.BodyId == body.Id)) return $"{body.Name} already has one.";
        return null;
    }

    /// <summary>Founds one: its hub appears on rails above the body, built from outside the sector (it's the site office, not the structure).</summary>
    public ActionResult FoundMega(string kindId, BodyId bodyId)
    {
        if (Parts is null || MegaKinds.Find(kindId) is not { } kind || !_byId.TryGetValue(bodyId, out var body)) return new(false, "That can't be built.");
        if (FoundBlocker(kind, body) is { } why) return new(false, why);
        var design = HubDesign(kind.HubBuilds);
        var copies = _megas.Count(m => m.KindId == kind.Id);
        var name = $"{body.Name} {kind.Name.ToLowerInvariant()}";
        var hub = new CraftState(AllocateCraftId(), $"{kind.Name} hub{(copies > 0 ? $" {copies + 1}" : "")}", design, design.ComputeStats(Parts), GetPosition(body.Id), 0);
        AddCraft(hub);
        var radius = body.RadiusM + kind.AltitudeM;
        var phase = kind.Place == MegaPlace.RockyPlanet ? -Math.PI / 2 : 0;
        PutOnRails(hub.Id, new CircularOrbit(body.Id, radius, kind.Place == MegaPlace.RockyPlanet ? 1e12 : CircularOrbitPeriodS(body, radius, DeployOrbitPeriodS), phase)); // an elevator's hub stays over one spot
        DiscoveredKg += (long)Math.Round(hub.Stats.TotalMassKg);
        var project = new MegaProject(_megas.Count == 0 ? 1 : _megas.Max(m => m.Id) + 1, kind.Id, name, body.Id, hub.Id);
        _megas.Add(project);
        PostMegaRequests(project, kind, hub);
        AddEvent($"Founded: {name}. Its hub asks for the first stage's materials.", EventKind.Milestone, TargetRef.Of(hub));
        return new(true, $"{name} founded: bring {Describe(kind.Stages[0].MaterialsKg)} to {hub.Name}.");
    }

    public ActionResult PauseMega(int id, bool paused)
    {
        if (_megas.FirstOrDefault(m => m.Id == id) is not { } project) return new(false, "No such project.");
        project.Paused = paused;
        if (_craftsById.TryGetValue(project.HubId, out var hub) && KindOf(project) is { } kind) PostMegaRequests(project, kind, hub);
        return new(true, paused ? $"{project.Name}: paused. It keeps what it has built." : $"{project.Name}: building again.");
    }

    /// <summary>The hub asks for the next stage's materials (unless paused or done) and two hours of upkeep once anything is built.</summary>
    private void PostMegaRequests(MegaProject project, MegaKind kind, CraftState hub)
    {
        var want = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (!project.Paused && !project.Working && project.Stages < kind.Stages.Count)
            foreach (var (material, kg) in kind.Stages[project.Stages].MaterialsKg) want[material] = kg;
        if (project.Stages > 0 || project.Working)
            foreach (var (material, kg) in kind.UpkeepKgPerHour) want[material] = want.GetValueOrDefault(material) + 2 * kg;
        foreach (var material in hub.RequestsList.Keys.Where(m => !want.ContainsKey(m)).ToList()) hub.RequestsList.Remove(material);
        foreach (var (material, kg) in want) hub.RequestsList[material] = kg;
    }

    // --- Each tick ---

    private void StepMegas()
    {
        if (_megas.Count == 0) return;
        foreach (var project in _megas)
        {
            if (KindOf(project) is not { } kind || !_craftsById.TryGetValue(project.HubId, out var hub)) continue;
            hub.ExtraStorageKg = MegaStorageKg(hub);
            if (project.Stages > 0 && (Tick + project.Id * 997) % UpkeepTicks == 0) Upkeep(project, kind, hub);
            if (project.Paused || project.Stages >= kind.Stages.Count) continue;
            var stage = kind.Stages[project.Stages];
            if (!project.Working)
            {
                if (Tick % FixedStepClock.TicksPerSecond != 0 || hub.Inventory.Shortfall(stage.MaterialsKg).Count > 0) continue;
                hub.Inventory.TryRemove(stage.MaterialsKg);
                project.BuiltKg += stage.MaterialsKg.Values.Sum(); // they're the structure now
                project.Working = true;
                project.WorkTicks = 0;
                PostMegaRequests(project, kind, hub);
                continue;
            }
            if (!Operational(hub) || !UseMachinePower(hub, MegaWorkKW * FixedStepClock.TickSeconds)) continue;
            if (++project.WorkTicks < (long)(stage.WorkMinutes * 60 * FixedStepClock.TicksPerSecond)) continue;
            project.Working = false;
            project.WorkTicks = 0;
            project.Stages++;
            Make("mega_stage", hub.Position); // #129
            var done = project.Stages == kind.Stages.Count;
            AddEvent(done ? $"{project.Name} is finished." : $"{project.Name}: {stage.Name} done ({project.Stages} of {kind.Stages.Count}).", EventKind.Milestone, TargetRef.Of(hub));
            if (done) ReachMilestone("megastructure");
            Object(project, kind, hub);
            PostMegaRequests(project, kind, hub);
        }
        if (Tick % FixedStepClock.TicksPerSecond == 0) Lift();
    }

    /// <summary>A civilization whose system it's in takes offence; conservationists anywhere you've met them, a little.</summary>
    private void Object(MegaProject project, MegaKind kind, CraftState hub)
    {
        if (kind.Grievance <= 0) return;
        var owner = ClaimantAt(hub.Position);
        if (owner is not null)
        {
            owner.Grievance = Math.Min(100, owner.Grievance + kind.Grievance);
            AddEvent($"The {owner.Name} object to {project.Name} in their system.", EventKind.Civilization, new TargetRef(CraftId.None, owner.HomeStarId));
        }
        foreach (var faction in _factions.Where(f => f != owner && f.Culture.Id == "conservationist"))
            faction.Grievance = Math.Min(100, faction.Grievance + kind.Grievance / 4);
    }

    private void Upkeep(MegaProject project, MegaKind kind, CraftState hub)
    {
        if (kind.UpkeepKgPerHour.Count == 0) return;
        var had = hub.Inventory.TryRemove(kind.UpkeepKgPerHour);
        if (had) foreach (var (material, kg) in kind.UpkeepKgPerHour) _ejectedKg[material] = _ejectedKg.GetValueOrDefault(material) + kg; // used up
        if (had == !project.Neglected) return;
        project.Neglected = !had;
        AddEvent(had ? $"{project.Name} works again." : $"{project.Name} has stopped working: its hub lacks {Describe(kind.UpkeepKgPerHour)} for upkeep.", had ? EventKind.Fleet : EventKind.Danger, TargetRef.Of(hub));
    }

    // --- Effects ---

    /// <summary>Extra storage a hub has from its structure.</summary>
    internal long MegaStorageKg(CraftState hub) =>
        _megas.Count > 0 && MegaAt(hub.Id) is { } project && KindOf(project) is { } kind ? (long)(kind.Effect.StorageKg * Share(project)) : 0;

    /// <summary>Extra building rate at a hub.</summary>
    public double MegaFabricationKgPerS(CraftState yard) =>
        _megas.Count > 0 && MegaAt(yard.Id) is { } project && KindOf(project) is { } kind ? kind.Effect.FabricationKgPerS * Share(project) : 0;

    /// <summary>A yard's building rate, kg a second, with its structure's share (#126).</summary>
    public double FabricationRate(CraftState yard) => yard.Stats.FabricationRateKgPerS + MegaFabricationKgPerS(yard);

    /// <summary>Power from structures this tick, kW, for a craft: its own ring, or a share of a swarm in its system.</summary>
    public double MegaPowerKW(CraftState craft)
    {
        if (_megas.Count == 0 || !IsPlayers(craft) || craft.IsWreck) return 0;
        var kw = 0.0;
        foreach (var project in _megas)
        {
            if (KindOf(project) is not { Effect.PowerKW: > 0 } kind || Share(project) is var share && share <= 0) continue;
            if (!kind.Effect.PowerToSystem) { if (project.HubId == craft.Id) kw += kind.Effect.PowerKW * share; continue; }
            if (Movable(craft) || StarOf(project.BodyId) is not { } star && GetBody(project.BodyId) is not { Kind: BodyKind.Star }) continue;
            var system = GetBody(project.BodyId).Kind == BodyKind.Star ? project.BodyId : StarOf(project.BodyId)!.Id;
            if (StarAt(craft.Position)?.Id != system) continue;
            var stations = _crafts.Count(c => IsPlayers(c) && !c.IsWreck && !Movable(c) && StarAt(c.Position)?.Id == system);
            kw += kind.Effect.PowerKW * share / Math.Max(1, stations);
        }
        return kw;
    }

    /// <summary>Once a second: elevators move goods between the hub and your stations landed on their planet.</summary>
    private void Lift()
    {
        foreach (var project in _megas)
        {
            if (KindOf(project) is not { Effect.LiftKgPerMinute: > 0 } kind || Share(project) is var share && share <= 0 || !_craftsById.TryGetValue(project.HubId, out var hub)) continue;
            var budget = (long)(kind.Effect.LiftKgPerMinute * share / 60);
            foreach (var station in _crafts.Where(c => IsPlayers(c) && !c.IsWreck && c.IsLanded && c.LandedOnBodyId == project.BodyId && c.Id != hub.Id).ToList())
            {
                // Down: what the station asks for that the hub has. Up: what it holds beyond what it asks to keep, but not its own supplies.
                foreach (var (material, want) in station.RequestsList.ToList())
                {
                    var down = Math.Min(budget, Math.Min(hub.Inventory[material] - hub.RequestsList.GetValueOrDefault(material), want - station.Inventory[material]));
                    if (down > 0 && Move(hub, station, material, down)) budget -= down;
                }
                foreach (var (material, kg) in station.Inventory.Kg.ToList())
                {
                    if (budget <= 0) break;
                    if (material is FuelMaterial or FoodMaterial or WaterMaterial || station.RequestsList.ContainsKey(material)) continue;
                    var up = Math.Min(budget, kg);
                    if (up > 0 && Move(station, hub, material, up)) budget -= up;
                }
                if (budget <= 0) break;
            }
        }

        static bool Move(CraftState from, CraftState to, string material, long kg)
        {
            kg = Math.Min(kg, to.FreeCargoKg);
            if (kg <= 0 || !from.Inventory.TryRemove(new Dictionary<string, long> { [material] = kg })) return false;
            to.Inventory.Add(material, kg);
            return true;
        }
    }

    /// <summary>Structures' built mass, kg, for the ledger.</summary>
    private long MegaBuiltKg()
    {
        long kg = 0;
        foreach (var project in _megas) kg += project.BuiltKg;
        return kg;
    }
}
