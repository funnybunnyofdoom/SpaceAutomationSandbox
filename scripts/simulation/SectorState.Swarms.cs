using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Swarms (#30, chosen by the creator): the player turns a group of identical automated miners into one
/// <see cref="SwarmState"/>, simulated as a flow, so its cost doesn't grow with its size.
/// <list type="bullet">
/// <item><b>Throughput from the design:</b> one ship's trip (flight from thrust and the distance now,
/// drilling from its drills, stretched if its solar power can't keep up) times the number of ships.</item>
/// <item><b>Limits are real:</b> the destination's free docking ports, its storage, the surveyed deposits
/// and the laws. Holds fill up behind a blocked unloading, then mining stops; the reason shows.</item>
/// <item><b>Exact mass:</b> whole kg move from the body to the swarm's holds to the destination.</item>
/// <item><b>No losses</b>, and they fly at autopilot speed without the cruise drive. Ships split off again
/// beside the destination as ordinary craft with the same job.</item>
/// <item><b>Bulk growth:</b> a standing order at a shipyard builds one ship after another into the swarm.</item>
/// </list>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Fewest ships that make a swarm.</summary>
    public const int MinSwarmShips = 5;

    /// <summary>Most ships that split off at once (they become ordinary craft, which cost more to simulate).</summary>
    public const int MaxSwarmSplit = 50;

    /// <summary>Time per trip for landing and anchoring, and for lifting off (compared with real miners in SwarmTests).</summary>
    public const double SwarmLandS = 120, SwarmLiftS = 80;

    /// <summary>How long a ship takes up a docking port per trip (compared with real miners at a busy yard).</summary>
    public const double SwarmBerthS = 60;

    private readonly List<SwarmState> _swarms = new();
    private uint _nextSwarmId = 1;

    /// <summary>Swarms in id order.</summary>
    public IReadOnlyList<SwarmState> Swarms => _swarms;

    public uint NextSwarmId => _nextSwarmId;

    private SwarmState? FindSwarm(SwarmId id)
    {
        foreach (var swarm in _swarms) if (swarm.Id == id) return swarm;
        return null;
    }

    public SwarmState GetSwarm(SwarmId id) => FindSwarm(id) ?? throw new KeyNotFoundException($"No {id} in sector.");

    /// <summary>Restores a saved swarm exactly.</summary>
    public void RestoreSwarm(SwarmState swarm)
    {
        if (FindSwarm(swarm.Id) is not null) throw new InvalidOperationException($"Duplicate {swarm.Id}.");
        _swarms.Add(swarm);
        _swarms.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        _nextSwarmId = Math.Max(_nextSwarmId, swarm.Id.Value + 1);
    }

    public void RestoreNextSwarmId(uint next) => _nextSwarmId = Math.Max(_nextSwarmId, next);

    // --- Forming ---

    /// <summary>Why a craft can't be part of a swarm, or null if it can.</summary>
    public string? SwarmBlocker(CraftState craft)
    {
        var s = craft.Stats;
        if (!IsPlayers(craft) || craft.IsWreck) return "it isn't a working ship of yours";
        if (s.QuantumDriveKW > 0 || s.NanoRepairKgPerS > 0 || s.AntimatterCapacityKg > 0) return "its design is too special";
        if (s.ShieldCapacity > 0) return "its design has a shield";
        if (s.WeaponList.Count > 0) return "its design is armed";
        if (craft.Id == PilotCraftId) return "you are flying it";
        if (craft.Job is not MiningJob { Active: true }) return "it has no mining job";
        if (craft.Job is MiningJob { MaterialId: BiomassMaterial }) return "harvesters can't swarm";
        if (s.DockingPorts.Count > 0) return "its design has docking ports";
        if (s.CrewRequired > 0 || craft.Crew > 0) return "it has crew";
        if (s.DriveList.Count > 0 || s.GeneratorList.Count > 0 || s.ConverterList.Count > 0) return "its design burns or converts materials";
        if (s.PowerGenerationKW <= 0 || craft.LegacyFreePower || craft.LegacyNoCrew) return "its design has no solar power";
        if (s.FabricationRateKgPerS > 0 || s.RefiningRateKgPerS > 0 || s.ResearchRatePerS > 0 || s.WarpGateCount > 0 || s.EjectionRateKgPerS > 0)
            return "its design does more than mine";
        if (craft.PartHealth.Count > 0 || craft.PartsLost > 0 || craft.IntactDesign is not null) return "it is damaged";
        if (craft.Kits.Count > 0) return "it carries a construction kit";
        if (craft.PenKg > 0 || s.RanchRateKgPerS > 0 || s.RepellerCount > 0 || s.PenCapacityKg > 0) return "its design handles animals";
        if (craft.YardOrder is not null || !craft.ServiceAt.IsNone) return "a shipyard is working on it";
        if (craft.Requests.Count > 0 || craft.JumpRequestTick > 0) return "it is busy";
        return null;
    }

    private static bool SameDesign(ShipDesign a, ShipDesign b) =>
        ReferenceEquals(a, b) || (a.Cells.Count == b.Cells.Count && a.Cells.ToHashSet().SetEquals(b.Cells));

    private static bool SameJob(MiningJob a, MiningJob b) =>
        a.SourceBodyId == b.SourceBodyId && a.MaterialId == b.MaterialId && a.DestinationId == b.DestinationId && a.ReturnFraction == b.ReturnFraction;

    private SwarmState? MatchingSwarm(ShipDesign design, MiningJob job) => _swarms.FirstOrDefault(s =>
        s.SourceBodyId == job.SourceBodyId && s.MaterialId == job.MaterialId && s.DestinationId == job.DestinationId
        && s.ReturnFraction == job.ReturnFraction && SameDesign(s.Design, design));

    /// <summary>Groups of at least <see cref="MinSwarmShips"/> ships (fewer if a matching swarm exists) that could swarm now.</summary>
    public IReadOnlyList<SwarmCandidate> SwarmCandidates()
    {
        var groups = new List<(ShipDesign Design, MiningJob Job, List<CraftId> Ships)>();
        foreach (var craft in _crafts)
        {
            if (craft.Job is not MiningJob { Active: true } job || SwarmBlocker(craft) is not null) continue;
            var found = groups.FindIndex(g => SameJob(g.Job, job) && SameDesign(g.Design, craft.Design));
            if (found < 0)
            {
                found = groups.Count;
                groups.Add((craft.Design, job, new List<CraftId>()));
            }
            groups[found].Ships.Add(craft.Id);
        }
        return groups.Where(g => g.Ships.Count >= MinSwarmShips || MatchingSwarm(g.Design, g.Job) is not null)
            .Select(g => new SwarmCandidate(g.Design.Name, g.Job.SourceBodyId, g.Job.MaterialId, g.Job.DestinationId, g.Ships)).ToList();
    }

    /// <summary>
    /// Turns ships into a swarm (or adds them to the one with the same design and job). Their cargo and
    /// battery charge go with them; nothing changes if any of them can't.
    /// </summary>
    public ActionResult FormSwarm(IReadOnlyList<CraftId> shipIds)
    {
        var ships = shipIds.Distinct().OrderBy(i => i.Value).Where(_craftsById.ContainsKey).Select(GetCraft).ToList();
        if (ships.Count == 0) return new(false, "No ships to swarm.");
        foreach (var ship in ships)
            if (SwarmBlocker(ship) is { } why) return new(false, $"{ship.Name} can't swarm: {why}.");
        var design = ships[0].Design;
        var job = (MiningJob)ships[0].Job!;
        if (ships.Any(s => !SameDesign(s.Design, design) || !SameJob((MiningJob)s.Job!, job)))
            return new(false, "A swarm needs ships with the same design and the same job.");
        var swarm = MatchingSwarm(design, job);
        if (swarm is null && ships.Count < MinSwarmShips) return new(false, $"A swarm needs at least {MinSwarmShips} ships.");
        if (!_byId.TryGetValue(job.SourceBodyId, out var source) || !_craftsById.TryGetValue(job.DestinationId, out var destination))
            return new(false, "The job's source or destination is gone.");
        var star = StarOf(source.Id);
        if (StarAt(destination.Position) != star || ships.Any(s => StarAt(s.Position) != star))
            return new(false, "A swarm works in one star system: ships, source and destination must all be there.");

        if (swarm is null)
        {
            var id = new SwarmId(_nextSwarmId++);
            swarm = new SwarmState(id, $"Swarm {id.Value}", design, ships[0].Stats, ships.Count, job.SourceBodyId, job.MaterialId, job.DestinationId, job.ReturnFraction);
            _swarms.Add(swarm);
        }
        else swarm.Count += ships.Count;
        foreach (var ship in ships)
        {
            swarm.Inventory.Add(ship.Inventory.Kg);
            swarm.StoredEnergyKJ += ship.StoredEnergyKJ;
            RemoveCraft(ship);
        }
        return new(true, $"{swarm.Name}: {swarm.Count} ships mining {swarm.MaterialId} at {source.Name}.");
    }

    // --- Splitting ---

    /// <summary>
    /// Takes ships out of a swarm (at most <see cref="MaxSwarmSplit"/> at once): they appear beside the destination as
    /// ordinary craft with their share of the cargo and charge, running the same job. The last one out ends the swarm.
    /// </summary>
    public ActionResult SplitSwarm(SwarmId id, int count)
    {
        var swarm = GetSwarm(id);
        count = Math.Min(Math.Min(count, swarm.Count), MaxSwarmSplit);
        if (count < 1) return new(false, "No ships to split off.");
        if (!_craftsById.TryGetValue(swarm.DestinationId, out var destination)) return new(false, "Its destination is gone: give it a new job first.");
        var split = 0;
        for (; split < count; split++)
        {
            var craftId = AllocateCraftId();
            var craft = new CraftState(craftId, $"{swarm.Design.Name} #{craftId.Value}", swarm.Design, swarm.Stats, destination.Position, 0);
            AddCraft(craft);
            try { PlaceNextTo(destination, craft, TimeS); }
            catch (InvalidOperationException) { RemoveCraft(craft); break; } // no room beside the destination
            // An even share, rounded up (so what stays always fits the ships that stay), within this ship's hold.
            foreach (var (material, kg) in swarm.Inventory.Kg.ToList())
            {
                var share = Math.Min((kg + swarm.Count - 1) / swarm.Count, craft.FreeCargoKg);
                if (share <= 0) continue;
                swarm.Inventory.TryRemove(new Dictionary<string, long> { [material] = share });
                craft.Inventory.Add(material, share);
            }
            craft.StoredEnergyKJ = Math.Min(swarm.StoredEnergyKJ / swarm.Count, craft.Stats.EnergyCapacityKJ);
            swarm.StoredEnergyKJ = Math.Max(0, swarm.StoredEnergyKJ - craft.StoredEnergyKJ);
            craft.Job = new MiningJob(swarm.SourceBodyId, swarm.MaterialId, swarm.DestinationId, swarm.ReturnFraction)
            {
                State = craft.Inventory[swarm.MaterialId] > 0 ? MiningJobState.ToDestination : MiningJobState.ToSource, // unload first
            };
            swarm.Count--;
        }
        if (swarm.Count == 0)
        {
            if (swarm.Inventory.TotalKg > 0) throw new InvalidOperationException($"{swarm.Name} ended with cargo left."); // can't happen: shares round up
            _swarms.Remove(swarm);
        }
        return split == 0
            ? new(false, $"No room beside {destination.Name}.")
            : new(true, $"{split} ship{(split == 1 ? "" : "s")} left {swarm.Name}" + (swarm.Count == 0 ? ", which is now empty." : $"; {swarm.Count} remain."));
    }

    // --- Orders ---

    /// <summary>Gives a swarm another mining job in its system. What it already holds is delivered to the new destination.</summary>
    public ActionResult RetargetSwarm(SwarmId id, BodyId sourceId, string material, CraftId destinationId)
    {
        var swarm = GetSwarm(id);
        if (!_byId.TryGetValue(sourceId, out var source) || !source.IsMineable) return new(false, "The source must be a mineable body.");
        if (!_craftsById.TryGetValue(destinationId, out var destination) || destination.Stats.DockingPorts.Count == 0 || !IsPlayers(destination))
            return new(false, "The destination must have a docking port.");
        if (material == BiomassMaterial) return new(false, "Swarms can't harvest plants.");
        if (MineableSurveyed(source, material) <= 0) return new(false, $"Not possible: {NothingToMine(source, material)}.");
        var star = _byId.ContainsKey(swarm.SourceBodyId) ? StarOf(swarm.SourceBodyId)
            : _craftsById.TryGetValue(swarm.DestinationId, out var old) ? StarAt(old.Position) : StarOf(sourceId);
        if (StarOf(sourceId) != star || StarAt(destination.Position) != star) return new(false, "A swarm can't move to another star system.");
        swarm.SourceBodyId = sourceId;
        swarm.MaterialId = material;
        swarm.DestinationId = destinationId;
        swarm.MineRemainderKg = 0;
        return new(true, $"{swarm.Name}: now mining {material} at {source.Name} for {destination.Name}.");
    }

    /// <summary>
    /// Standing order: the yard builds <paramref name="count"/> more ships into the swarm, one after another, taking
    /// each ship's materials when they're there. Added to what's still to be ordered.
    /// </summary>
    public ActionResult GrowSwarm(SwarmId id, CraftId yardId, int count)
    {
        var swarm = GetSwarm(id);
        if (count < 1) return new(false, "Order at least one ship.");
        if (!_craftsById.TryGetValue(yardId, out var yard) || !yard.IsShipyard) return new(false, "Growing a swarm needs a shipyard.");
        if (_byId.ContainsKey(swarm.SourceBodyId) && StarAt(yard.Position) != StarOf(swarm.SourceBodyId))
            return new(false, "The shipyard must be in the swarm's star system.");
        if (LockedPartsReason(swarm.Design.Cells.Select(c => c.PartId)) is { } locked) return new(false, locked);
        swarm.GrowYardId = yardId;
        swarm.GrowRemaining = (int)Math.Min(1_000_000, (long)swarm.GrowRemaining + count);
        swarm.GrowWaitReason = null;
        return new(true, $"{swarm.Name}: {swarm.GrowRemaining} more ships to build at {yard.Name}.");
    }

    /// <summary>Stops ordering more ships. One already being built still joins.</summary>
    public void StopGrowing(SwarmId id)
    {
        var swarm = GetSwarm(id);
        swarm.GrowRemaining = 0;
        swarm.GrowWaitReason = null;
    }

    private void StepSwarmGrowth()
    {
        foreach (var swarm in _swarms)
        {
            if (swarm.GrowRemaining <= 0 || Parts is null) continue;
            if (!_craftsById.TryGetValue(swarm.GrowYardId, out var yard) || !yard.IsShipyard) { swarm.GrowWaitReason = "its shipyard is gone"; continue; }
            if (yard.BuildQueueList.Any(o => o.SwarmId == swarm.Id)) { swarm.GrowWaitReason = null; continue; } // one at a time: stock stays free for other orders
            if ((Tick + swarm.Id.Value) % FixedStepClock.TicksPerSecond != 0) continue; // once a second
            var result = OrderBuild(yard.Id, swarm.Design, Parts, $"{swarm.Name} ship");
            if (!result.Accepted) { swarm.GrowWaitReason = string.Join("; ", result.Errors); continue; }
            yard.BuildQueueList[^1].SwarmId = swarm.Id;
            swarm.GrowRemaining--;
            swarm.GrowWaitReason = null;
        }
    }

    /// <summary>A finished ship ordered for a swarm joins it (true), unless the swarm is gone.</summary>
    private bool JoinSwarm(BuildOrder order)
    {
        if (order.SwarmId.IsNone || FindSwarm(order.SwarmId) is not { } swarm) return false;
        swarm.Count++;
        swarm.StoredEnergyKJ += order.Stats.EnergyCapacityKJ; // new craft start fully charged
        return true;
    }

    // --- The flow ---

    /// <summary>One ship's trip now: from its design, the distance and the sunlight.</summary>
    private SwarmRate SwarmTrip(SwarmState swarm, CelestialBodyState source, CraftState destination)
    {
        var stats = swarm.Stats;
        var distance = Math.Max(0, GetPosition(source.Id).DistanceTo(destination.Position) - source.RadiusM);
        const double top = JobCruiseSpeedMps;
        double Leg(double massKg)
        {
            var accel = Math.Max(stats.TotalThrustN, 1) / massKg;
            return distance >= top * top / accel ? distance / top + top / accel : 2 * Math.Sqrt(distance / accel);
        }
        double ThrustS(double massKg)
        {
            var accel = Math.Max(stats.TotalThrustN, 1) / massKg;
            return 2 * Math.Min(top / accel, Math.Sqrt(distance / accel));
        }
        var load = swarm.LoadKg;
        var outS = Leg(stats.TotalMassKg) + SwarmLandS;
        var mineS = load / Math.Max(stats.MiningRateKgPerS * Tuning.DrillRateMultiplier * _mining, 1e-9);
        var backS = Leg(stats.TotalMassKg + load) + SwarmLiftS + SwarmBerthS;
        var cycle = outS + mineS + backS;
        // Energy a trip needs against what its panels make: batteries only smooth it out.
        var needKJ = stats.DrillPowerKW * _power * mineS + stats.ThrustPowerKW * (ThrustS(stats.TotalMassKg) + ThrustS(stats.TotalMassKg + load));
        var solarKW = stats.PowerGenerationKW * SolarFactorAt(destination.Position);
        var powerS = solarKW > 0 ? needKJ / solarKW : double.PositiveInfinity;
        return new SwarmRate(outS, mineS, backS, Math.Max(cycle, powerS), powerS > cycle);
    }

    /// <summary>Materials a swarm's ships carry for themselves: not delivered unless they're what it mines.</summary>
    private static bool IsSwarmSupply(string material) => material is FuelMaterial or "food" or "water";

    private static bool Deliverable(SwarmState swarm, string material) => material == swarm.MaterialId || !IsSwarmSupply(material);

    private void StepSwarms()
    {
        foreach (var swarm in _swarms)
        {
            swarm.LimitReason = null;
            swarm.LimitNote = null;
            if (!_craftsById.TryGetValue(swarm.DestinationId, out var destination))
            {
                swarm.LimitReason = StationGone("destination");
                swarm.DeliveryKgPerS = 0;
                continue;
            }
            var source = _byId.GetValueOrDefault(swarm.SourceBodyId);
            // Without its source it only unloads what it still holds.
            swarm.Rate = source is null ? default : SwarmTrip(swarm, source, destination);
            var sharing = 0; // swarms unloading here share its free ports evenly
            foreach (var other in _swarms)
                if (other.DestinationId == destination.Id && other.Inventory.TotalKg > 0) sharing++;
            var delivered = DeliverFromSwarm(swarm, destination, FreePorts(destination).Count, Math.Max(1, sharing));
            swarm.DeliveryKgPerS += (delivered * (double)FixedStepClock.TicksPerSecond - swarm.DeliveryKgPerS) / (60.0 * FixedStepClock.TicksPerSecond);
            if (source is null) swarm.LimitReason ??= "the source body is gone";
            else MineForSwarm(swarm, source);
            if (swarm.Rate.PowerLimited) swarm.LimitNote = "slowed by solar power";
        }
    }

    /// <summary>Ships on the way back unload: as fast as they arrive, the free ports turn them round, and there's room.</summary>
    private long DeliverFromSwarm(SwarmState swarm, CraftState destination, int freePorts, int sharing)
    {
        long held = 0;
        foreach (var (material, kg) in swarm.Inventory.Kg) if (Deliverable(swarm, material)) held += kg;
        if (held == 0) { swarm.DeliverRemainderKg = 0; return 0; }

        // What's in the holds is on its way back: it arrives over one return trip.
        var arriving = held * FixedStepClock.TickSeconds / Math.Max(SwarmBerthS, swarm.Rate.BackS);
        var berths = (double)freePorts / sharing * swarm.LoadKg / SwarmBerthS * FixedStepClock.TickSeconds;
        if (freePorts == 0) swarm.LimitReason = $"no free docking port at {destination.Name}";
        else if (berths < arriving) swarm.LimitReason = $"queued for {freePorts} docking port{(freePorts == 1 ? "" : "s")} at {destination.Name}";
        var due = swarm.DeliverRemainderKg + Math.Min(arriving, berths);
        var whole = Math.Min((long)Math.Floor(due), held);
        var room = destination.FreeCargoKg;
        if (room <= 0 || room < whole) swarm.LimitReason = $"{destination.Name} is full";
        var moving = Math.Min(whole, room);
        swarm.DeliverRemainderKg = moving == whole ? due - Math.Floor(due) : 0;
        if (moving == 0) return 0;
        var left = moving;
        foreach (var (material, kg) in swarm.Inventory.Kg.ToList())
        {
            if (left <= 0) break;
            if (!Deliverable(swarm, material)) continue;
            var take = Math.Min(kg, left);
            swarm.Inventory.TryRemove(new Dictionary<string, long> { [material] = take });
            destination.Inventory.Add(material, take);
            left -= take;
        }
        swarm.DeliveredKg += moving;
        return moving;
    }

    /// <summary>Ships at the source drill, until every hold is full or nothing's left that may be mined.</summary>
    private void MineForSwarm(SwarmState swarm, CelestialBodyState source)
    {
        if (MineableSurveyed(source, swarm.MaterialId) <= 0)
        {
            swarm.MineRemainderKg = 0;
            swarm.LimitReason = NothingToMine(source, swarm.MaterialId);
            return;
        }
        var room = Math.Min((long)(swarm.Count * swarm.LoadKg) - swarm.Inventory[swarm.MaterialId], swarm.CargoCapacityKg - swarm.Inventory.TotalKg);
        if (room <= 0) { swarm.MineRemainderKg = 0; return; } // every hold is full: the unloading says why
        var due = swarm.MineRemainderKg + swarm.Rate.KgPerS(swarm) * FixedStepClock.TickSeconds;
        var whole = Math.Min((long)Math.Floor(due), room);
        swarm.MineRemainderKg = due - Math.Floor(due);
        // They spread over the body's sites: each minute a different direction, then the nearest site with the ore.
        var near = FlightModel.WrapAngle((Tick / (60 * FixedStepClock.TicksPerSecond) + swarm.Id.Value) * 2.399963229728653 % (2 * Math.PI));
        for (var guard = 0; whole > 0 && guard <= SurfaceSite.MaxSites; guard++)
        {
            if (MiningSite(source, swarm.MaterialId, near, LawActive(Laws.ProtectLife)) is not { } site) break;
            var bearing = site.Clamp(near, 0.1 * site.WidthRad);
            var taken = source.Extract(swarm.MaterialId, whole, bearing, BulkMiningAllowed);
            if (taken == 0) break;
            swarm.Inventory.Add(swarm.MaterialId, taken);
            NoteMining(source, taken); // a grievance in a claimed system (#36)
            HarmLife(source, bearing, taken); // native life (#92)
            source.Dig(bearing, taken);
            whole -= taken;
        }
    }
}
