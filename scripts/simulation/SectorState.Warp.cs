using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Star systems and warp gates (#54, #22, #23).
/// <list type="bullet">
/// <item><b>Systems:</b> the home system plus systems opened by warp. Each has its own star, placed so
/// far away (<see cref="SectorGenerator.SystemSpacingM"/>) that warp is the only way there. A body
/// belongs to the system of the star at the top of its orbit chain; a craft to the system of the
/// nearest star. Everything else (motion, gravity, distant simulation, saves) works as before.</item>
/// <item><b>Gates:</b> a station with a warp gate part. Ships dock at it and ask to jump (the player
/// with N; automated jobs on their own). The gate sends one craft every <see cref="GateCooldownS"/>
/// seconds, paying <see cref="JumpEnergyKJPerTonne"/> per tonne of craft (cargo included) from its
/// own power; the rest queue in the order they asked ("queued at X, 2 ahead"). The craft arrives
/// docked at the linked gate (or parked beside it if its ports are full).</item>
/// <item><b>Opening a system:</b> a gate's first jump has nowhere to go yet: it generates the next star
/// system and builds the far gate there, the same design as itself, from materials in its own stock
/// (its whole bill), so mass stays exact. The far gate starts with no crew (robots in the design work).</item>
/// <item><b>Routing:</b> an automated job whose next target (station or body) is in another system flies
/// to the first gate on the shortest chain of gates there (relays included), jumps, and carries on.
/// No route: it waits and says so. (Gates only link systems, so there are no in-system shortcuts yet.)</item>
/// </list>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>A gate sends one craft this often.</summary>
    public const double GateCooldownS = 10;

    /// <summary>Energy a jump costs the sending gate, per tonne of craft (2 MJ/t).</summary>
    public const double JumpEnergyKJPerTonne = 2_000;

    private int GateCooldownTicks => (int)Math.Round(GateCooldownS * FixedStepClock.TicksPerSecond);

    // --- Systems ---

    /// <summary>The stars, one per system, in id order (the home star first).</summary>
    public IReadOnlyList<CelestialBodyState> Stars => _stars;

    private readonly List<CelestialBodyState> _stars = new();

    /// <summary>The star of the system a body is in (top of its orbit chain), or null without a star.</summary>
    public CelestialBodyState? StarOf(BodyId bodyId)
    {
        if (!_byId.TryGetValue(bodyId, out var body)) return null;
        for (var guard = 0; guard < 64; guard++)
        {
            if (body.Kind == BodyKind.Star) return body;
            if (body.Orbit is not { } orbit || !_byId.TryGetValue(orbit.Parent, out body)) return null;
        }
        return null;
    }

    /// <summary>The star of the system a point is in: the nearest star.</summary>
    public CelestialBodyState? StarAt(Vector2D point)
    {
        CelestialBodyState? best = null;
        var bestDistance = double.MaxValue;
        foreach (var body in _stars)
        {
            var d = GetPosition(body.Id).DistanceTo(point);
            if (d < bestDistance) { best = body; bestDistance = d; }
        }
        return best;
    }

    /// <summary>Is the craft a warp gate station?</summary>
    public static bool IsGate(CraftState craft) => craft.Shape.WarpGateCount > 0 && craft.Shape.DockingPorts.Count > 0;

    // --- Asking to jump ---

    /// <summary>The player (or anyone) asks to jump through the gate the craft is docked at; asking again cancels.</summary>
    public ActionResult RequestJump(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (!craft.IsDocked || !_craftsById.TryGetValue(craft.DockedToCraftId, out var gate) || !IsGate(gate))
            return new(false, $"{craft.Name} must be docked at a warp gate to jump.");
        if (craft.JumpRequestTick > 0)
        {
            craft.JumpRequestTick = 0;
            return new(true, $"{craft.Name}: jump cancelled.");
        }
        craft.JumpRequestTick = Math.Max(1, Tick);
        return new(true, gate.GateLinkId.IsNone || !_craftsById.ContainsKey(gate.GateLinkId)
            ? $"{craft.Name}: queued at {gate.Name}. Its first jump opens a new star system."
            : $"{craft.Name}: queued at {gate.Name} for {_craftsById[gate.GateLinkId].Name}.");
    }

    // --- Gates at work ---

    /// <summary>Each tick, after movement: every gate sends the first craft in its queue if it can.</summary>
    private void StepWarpGates()
    {
        foreach (var craft in _crafts)
        {
            craft.GateStatus = null;
            // Only craft docked at a gate can be in its queue.
            if (craft.JumpRequestTick > 0 && (!craft.IsDocked || !_craftsById.TryGetValue(craft.DockedToCraftId, out var at) || !IsGate(at)))
                craft.JumpRequestTick = 0;
        }

        List<(CraftState Craft, CraftState To)>? jumps = null;
        // A snapshot: opening a system adds the far gate to the craft list.
        foreach (var gate in _crafts.Where(IsGate).ToList())
        {
            var queue = _crafts.Where(c => c.JumpRequestTick > 0 && c.DockedToCraftId == gate.Id)
                .OrderBy(c => c.JumpRequestTick).ThenBy(c => c.Id.Value).ToList();
            if (queue.Count == 0) continue;
            for (var i = 1; i < queue.Count; i++) queue[i].GateStatus = $"queued at {gate.Name} ({i} ahead)";
            var next = queue[0];

            var wait = gate.GateReadyTick - Tick;
            if (wait > 0) { next.GateStatus = $"next to jump at {gate.Name} (in {wait * FixedStepClock.TickSeconds:0} s)"; continue; }
            if (gate.GateClosed && gate.Stats.GateLockCount == 0) gate.GateClosed = false; // its lock was lost
            if (GateClosedReason(gate) is { } shut) { next.GateStatus = $"waiting: {shut}"; continue; } // #95
            if (!Operational(gate)) { next.GateStatus = $"waiting: {gate.Name} isn't working ({gate.CrewShortReason})"; continue; }

            var energy = next.MassKg / 1000 * JumpEnergyKJPerTonne;
            if (!gate.LegacyFreePower && !EnsurePower(gate, energy)) // generators help (#75)
            {
                next.GateStatus = $"waiting: {gate.Name} is charging (a jump takes {energy / 1000:0} MJ, it has {PowerPoolKJ(gate) / 1000:0})";
                continue;
            }
            if (gate.GateLinkId.IsNone || !_craftsById.TryGetValue(gate.GateLinkId, out var far))
            {
                // A far gate destroyed by a raid (#36) is sent through again to the same system; otherwise a new system opens.
                var problem = !gate.GateLostStarId.IsNone ? RebuildFarGate(gate) : OpenSystem(gate);
                if (problem is { } reason) { next.GateStatus = $"waiting: {reason}"; continue; }
                far = _craftsById[gate.GateLinkId];
            }
            TryUsePower(gate, energy);
            gate.GateReadyTick = Tick + GateCooldownTicks;
            (jumps ??= new()).Add((next, far));
        }
        if (jumps is null) return;
        foreach (var (craft, to) in jumps) Jump(craft, to);
    }

    /// <summary>Moves a craft (with everything aboard) to the far gate: docked there if a port is free.</summary>
    private void Jump(CraftState craft, CraftState to)
    {
        craft.DockedToCraftId = CraftId.None;
        craft.JumpRequestTick = 0;
        craft.Transit = null;
        craft.Dormant = false;
        ReleaseClaim(craft);
        PlaceNextTo(to, craft, TimeS);
        craft.Jumps++;
        craft.GateStatus = $"arrived at {to.Name}";
    }

    /// <summary>
    /// A gate's first jump: generates the next star system and builds the far gate there from the
    /// gate's own stock. Returns why it can't, or null when the link is made.
    /// </summary>
    private string? OpenSystem(CraftState gate)
    {
        if (Parts is null) return "no part catalog";
        var bill = gate.Design.ComputeBillOfMaterials(Parts).ToWholeKg();
        var shortfall = gate.Inventory.Shortfall(bill);
        if (shortfall.Count > 0)
            return $"the first jump opens a new star system and builds {gate.Name}'s twin there: it needs "
                   + string.Join(", ", shortfall.Select(kv => $"{kv.Value / 1000.0:0.0} t {kv.Key}")) + " more in its stock";
        gate.Inventory.TryRemove(bill);

        var index = _stars.Count; // the home star is 0, so the next system is number _stars.Count
        var before = ComputeMassLedger().DepositsKg;
        var (star, planet) = SectorGenerator.GenerateSystem(this, index);
        DiscoveredKg += ComputeMassLedger().DepositsKg - before; // new world: the ledger stays balanced
        MaybeMeetCivilization(star, index); // a civilization may claim it (#36)
        var far = new CraftState(AllocateCraftId(), $"{star.Name} gate", gate.Design, gate.Design.ComputeStats(Parts), GetPosition(planet.Id), 0);
        AddCraft(far);
        var radius = planet.RadiusM + far.CollisionRadiusM + 700; // low, like the home yard: the planet is in view on arrival
        PutOnRails(far.Id, new CircularOrbit(planet.Id, radius, CircularOrbitPeriodS(planet, radius, DeployOrbitPeriodS), 0));
        far.GateLinkId = gate.Id;
        gate.GateLinkId = far.Id;
        return null;
    }

    // --- Routing (#23) ---

    /// <summary>Gates and where they lead, built per tick for routing (in craft id order).</summary>
    private readonly List<(CraftState Gate, BodyId From, BodyId To)> _gateLinks = new();

    private void BuildGateLinks()
    {
        _gateLinks.Clear();
        foreach (var gate in _crafts)
        {
            if (!IsGate(gate) || gate.GateLinkId.IsNone || GateClosedReason(gate) is not null || !_craftsById.TryGetValue(gate.GateLinkId, out var far)) continue;
            if (StarAt(gate.Position) is not { } from || StarAt(far.Position) is not { } to || from.Id == to.Id) continue;
            _gateLinks.Add((gate, from.Id, to.Id));
        }
    }

    /// <summary>
    /// The gate to head for to reach a target in another system (first hop of the fewest jumps,
    /// gates in id order), null when the target is in this system. <paramref name="noRoute"/> is set
    /// when it's elsewhere and unreachable.
    /// </summary>
    private CraftState? GateHop(CraftState craft, Vector2D target, out bool noRoute)
    {
        noRoute = false;
        if (StarAt(craft.Position) is not { } here || StarAt(target) is not { } there || here.Id == there.Id) return null;
        // Breadth-first over systems; remember the first gate taken out of ours.
        var firstHop = new Dictionary<BodyId, CraftState> { [here.Id] = null! };
        var frontier = new Queue<BodyId>();
        frontier.Enqueue(here.Id);
        while (frontier.Count > 0)
        {
            var system = frontier.Dequeue();
            foreach (var (gate, from, to) in _gateLinks)
            {
                if (from != system || firstHop.ContainsKey(to)) continue;
                firstHop[to] = system == here.Id ? gate : firstHop[system];
                if (to == there.Id) return firstHop[to];
                frontier.Enqueue(to);
            }
        }
        noRoute = true;
        return null;
    }

    /// <summary>Where the job's current leg goes, as a point (station or body centre), or null.</summary>
    private Vector2D? JobTargetPoint(CraftState craft)
    {
        if (TravelTargetForJob(craft) is not { } t) return null;
        if (!t.Craft.IsNone) return _craftsById.TryGetValue(t.Craft, out var c) ? c.Position : null;
        return _byId.ContainsKey(t.Body) ? GetPosition(t.Body) : null;
    }

    /// <summary>
    /// For an automated job whose target is in another system: flies to the gate, docks, queues and
    /// jumps. True when it handled the craft this tick.
    /// </summary>
    private bool StepGateRoute(CraftState craft, ShipJob job)
    {
        if (_stars.Count < 2) return false; // one system: nothing to route
        if (JobTargetPoint(craft) is not { } target) return false;
        var hop = GateHop(craft, target, out var noRoute);
        if (hop is null)
        {
            if (!noRoute) return false;
            job.ServiceStatus = $"waiting: no warp gate route to the {StarAt(target)?.Name} system";
            craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded);
            return true;
        }
        switch (FlyToDock(craft, hop))
        {
            case DockApproach.Docked:
                craft.Controls = ShipControls.Idle;
                if (craft.JumpRequestTick == 0) craft.JumpRequestTick = Math.Max(1, Tick);
                job.ServiceStatus = craft.GateStatus ?? $"at {hop.Name}, jumping to {(_craftsById.TryGetValue(hop.GateLinkId, out var far) ? far.Name : "a new system")}";
                break;
            case DockApproach.NoFreePort:
                craft.Controls = new ShipControls(0, 0, true);
                job.ServiceStatus = $"queued for a berth at {hop.Name}";
                break;
            default:
                job.ServiceStatus = $"flying to {hop.Name} (warp to the {StarAt(target)?.Name} system)";
                break;
        }
        return true;
    }

    /// <summary>Mineable mass of the star systems generated so far (saved; see <see cref="MassLedger"/>).</summary>
    public long DiscoveredKg { get; private set; }

    public void RestoreDiscovered(long kg)
    {
        if (kg < 0) throw new ArgumentOutOfRangeException(nameof(kg));
        DiscoveredKg = kg;
    }

    /// <summary>Restores saved gate state.</summary>
    /// <summary>Restores a gate whose far end was destroyed (#36).</summary>
    public void RestoreGateLost(CraftId craftId, BodyId starId) => GetCraft(craftId).GateLostStarId = starId;

    public void RestoreGate(CraftId craftId, CraftId linkId, long gateReadyTick, long jumpRequestTick, int jumps)
    {
        var craft = GetCraft(craftId);
        if (gateReadyTick < 0 || jumpRequestTick < 0 || jumps < 0) throw new ArgumentOutOfRangeException(nameof(gateReadyTick));
        craft.GateLinkId = linkId; // may be a gate that's gone (dismantled): then the next jump opens a new system
        craft.GateReadyTick = gateReadyTick;
        craft.JumpRequestTick = jumpRequestTick;
        craft.Jumps = jumps;
    }
}
