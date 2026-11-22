using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Craft-to-body relation. Surface distance is negative inside the body. Closing speed is
/// positive while approaching. Relative speed is the full speed difference (0 = matched).
/// </summary>
public readonly record struct RelativeMotion(double SurfaceDistanceM, double RelativeSpeedMps, double ClosingSpeedMps);

/// <summary>
/// Authoritative state of one sector: its seed, the current tick and its bodies.
/// No Godot types. Views read from this; they never own or mutate it directly.
/// </summary>
public sealed partial class SectorState
{
    private readonly List<CelestialBodyState> _bodies = new();
    private readonly Dictionary<BodyId, CelestialBodyState> _byId = new();
    private uint _nextBodyId = 1;
    private readonly List<CraftState> _crafts = new();
    private readonly Dictionary<CraftId, CraftState> _craftsById = new();
    private uint _nextCraftId = 1;

    /// <param name="tick">Non-zero only when restoring a save.</param>
    public SectorState(ulong seed, long tick = 0)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        Seed = seed;
        Tick = tick;
    }

    public ulong Seed { get; }

    /// <summary>Number of fixed ticks simulated so far (see <see cref="FixedStepClock"/>).</summary>
    public long Tick { get; private set; }

    public double TimeS => Tick * FixedStepClock.TickSeconds;

    /// <summary>Bodies in id order (deterministic iteration).</summary>
    public IReadOnlyList<CelestialBodyState> Bodies => _bodies;

    public BodyId AllocateBodyId() => new(_nextBodyId++);

    public void AddBody(CelestialBodyState body)
    {
        if (_byId.ContainsKey(body.Id)) throw new InvalidOperationException($"Duplicate {body.Id}.");
        if (body.Orbit is { } orbit && !_byId.ContainsKey(orbit.Parent))
            throw new InvalidOperationException($"{body.Id} orbits unknown parent {orbit.Parent}; add parents first.");
        if (body.Id.Value >= _nextBodyId) _nextBodyId = body.Id.Value + 1;

        _bodies.Add(body);
        _bodies.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        _byId.Add(body.Id, body);
        ResetMotionCache();
    }

    // --- Body motion cache (#53). A body's position is its parent chain's offsets at a time; with a
    // star at the centre most chains are 2–3 deep and every craft asks about every body each tick
    // (contact, autopilot, cruise). Values are cached per body index for the few times a tick uses.
    // Computed the same way every time, so results are deterministic and identical after a load.
    private sealed class MotionSlot
    {
        public double TimeS = double.NaN;
        public Vector2D[] Position = Array.Empty<Vector2D>();
        public Vector2D[] Velocity = Array.Empty<Vector2D>();
        public bool[] Done = Array.Empty<bool>();
    }

    private readonly MotionSlot[] _motionSlots = { new(), new(), new(), new() };
    private int _nextMotionSlot;
    private readonly Dictionary<BodyId, int> _bodyIndex = new();
    private int[] _parentIndex = Array.Empty<int>();

    private void ResetMotionCache()
    {
        _bodyIndex.Clear();
        for (var i = 0; i < _bodies.Count; i++) _bodyIndex[_bodies[i].Id] = i;
        _parentIndex = new int[_bodies.Count];
        for (var i = 0; i < _bodies.Count; i++)
            _parentIndex[i] = _bodies[i].Orbit is { } orbit ? _bodyIndex[orbit.Parent] : -1;
        foreach (var slot in _motionSlots)
        {
            slot.TimeS = double.NaN;
            slot.Position = new Vector2D[_bodies.Count];
            slot.Velocity = new Vector2D[_bodies.Count];
            slot.Done = new bool[_bodies.Count];
        }
    }

    private MotionSlot SlotAt(double timeS)
    {
        foreach (var slot in _motionSlots)
            if (slot.TimeS == timeS) return slot;
        var fresh = _motionSlots[_nextMotionSlot];
        _nextMotionSlot = (_nextMotionSlot + 1) % _motionSlots.Length;
        fresh.TimeS = timeS;
        Array.Clear(fresh.Done);
        return fresh;
    }

    private void Fill(MotionSlot slot, int index)
    {
        if (slot.Done[index]) return;
        var position = Vector2D.Zero;
        var velocity = Vector2D.Zero;
        if (_bodies[index].Orbit is { } orbit)
        {
            var parent = _parentIndex[index];
            Fill(slot, parent);
            position = slot.Position[parent] + orbit.OffsetAt(slot.TimeS);
            velocity = slot.Velocity[parent] + orbit.VelocityAt(slot.TimeS);
        }
        slot.Position[index] = position;
        slot.Velocity[index] = velocity;
        slot.Done[index] = true;
    }

    /// <summary>Position of the body at <paramref name="index"/> in <see cref="Bodies"/> (fast path for per-body loops).</summary>
    private Vector2D BodyPositionAt(int index, double timeS)
    {
        var slot = SlotAt(timeS);
        Fill(slot, index);
        return slot.Position[index];
    }

    private Vector2D BodyVelocityAt(int index, double timeS)
    {
        var slot = SlotAt(timeS);
        Fill(slot, index);
        return slot.Velocity[index];
    }

    private int IndexOf(BodyId id) => _bodyIndex.TryGetValue(id, out var i) ? i : throw new KeyNotFoundException($"No {id} in sector.");

    public CelestialBodyState GetBody(BodyId id) =>
        _byId.TryGetValue(id, out var body) ? body : throw new KeyNotFoundException($"No {id} in sector.");

    /// <summary>Crafts in id order (deterministic update and iteration).</summary>
    public IReadOnlyList<CraftState> Crafts => _crafts;

    public CraftId AllocateCraftId() => new(_nextCraftId++);

    public void AddCraft(CraftState craft)
    {
        if (_craftsById.ContainsKey(craft.Id)) throw new InvalidOperationException($"Duplicate {craft.Id}.");
        if (craft.Id.Value >= _nextCraftId) _nextCraftId = craft.Id.Value + 1;

        _crafts.Add(craft);
        _crafts.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        _craftsById.Add(craft.Id, craft);
    }

    public CraftState GetCraft(CraftId id) =>
        _craftsById.TryGetValue(id, out var craft) ? craft : throw new KeyNotFoundException($"No {id} in sector.");

    /// <summary>
    /// Advances the sector by exactly one fixed tick. A craft's brake matches its target body's
    /// velocity, sampled mid-tick so a held brake tracks a curving orbit closely.
    /// </summary>
    /// <remarks>
    /// PLACEHOLDER collision (docs/06): bodies are solid. A craft (as a circle of
    /// <see cref="CraftState.CollisionRadiusM"/>) that would end a tick inside a body is placed on
    /// its surface and lands: it then moves with the body, with no damage and no bounce. It lifts
    /// off when its own thrust this tick points away from the surface.
    /// </remarks>
    public void Step()
    {
        // Written as (tick + x) * dt, the same form as TimeS, so "end of this tick" is bit-identical
        // to "TimeS of the next tick" (matters for exact save/load equivalence).
        var midTickS = (Tick + 0.5) * FixedStepClock.TickSeconds;
        var endS = (Tick + 1) * FixedStepClock.TickSeconds;
        StepJobs(); // automated craft set their controls first, from this tick's state
        BeginPowerTick();
        List<(CraftState, Vector2D)>? docked = null;
        foreach (var craft in _crafts)
        {
            if (craft.Cruising) { StepCruise(craft, endS); continue; } // no thrust, no contact: it drops out first
            var reference = !craft.TargetCraftId.IsNone ? CraftVelocityAt(GetCraft(craft.TargetCraftId), midTickS)
                : !craft.TargetBodyId.IsNone ? GetVelocityAt(craft.TargetBodyId, midTickS)
                : Vector2D.Zero;
            var velocityBefore = craft.Velocity;
            var thrustPowered = TryUsePower(craft, ThrustEnergyKJ(craft, reference));
            FlightModel.Step(craft, FixedStepClock.TickSeconds, reference, thrustPowered);

            // Docked ships follow their station, which may move later in this loop: settled below.
            if (craft.IsDocked) (docked ??= new()).Add((craft, craft.Velocity - velocityBefore));
            else if (craft.IsOnRails) UpdateOnRails(craft, craft.Velocity - velocityBefore, endS);
            else if (craft.IsLanded) UpdateLanded(craft, craft.Velocity - velocityBefore, endS);
            else ResolveContact(craft, endS);
        }

        if (docked != null) UpdateDocked(docked);
        StepMining();
        StepRefining();
        StepSurvey();

        // Shipyards advance after movement, so a finished craft is placed against the yard's new position.
        StepFabrication(endS);
        StepEjection();
        EndPowerTick();
        Tick++;
    }

    private void UpdateLanded(CraftState craft, Vector2D ownDeltaV, double endS)
    {
        var bodyId = craft.LandedOnBodyId;
        var bodyPos = GetPositionAt(bodyId, endS);
        var bodyVel = GetVelocityAt(bodyId, endS);
        var outward = craft.LandedOffset * (1.0 / craft.LandedOffset.Length);

        // Only throttle can lift it. The body's motion never does, and neither does brake, whose
        // "match the reference velocity" change could otherwise point slightly outward. Anchored: never.
        if (!craft.IsAnchored && !craft.Controls.Clamped().Brake && ownDeltaV.X * outward.X + ownDeltaV.Y * outward.Y > 0)
        {
            craft.LandedOnBodyId = BodyId.None;
            craft.Velocity = bodyVel + ownDeltaV;
            craft.Position = bodyPos + craft.LandedOffset + ownDeltaV * FixedStepClock.TickSeconds;
            // Turning in the same tick can swing a cell into the ground: keep every cell outside.
            var surface = GetBody(bodyId).RadiusM;
            if (SurfaceGap(craft, craft.Position, craft.HeadingRad, bodyPos, surface) < 0)
                craft.Position = SettleOnSurface(craft, craft.Position, bodyPos, surface);
            return;
        }

        craft.Velocity = bodyVel;
        if (craft.IsAnchored) craft.HeadingRad = craft.PreviousHeadingRad; // anchored: locked, can't turn
        else if (craft.HeadingRad != craft.PreviousHeadingRad)
        {
            // Turning on the ground: re-settle so it rests on its (new) lowest cells, never inside.
            var body = GetBody(bodyId);
            var settled = SettleOnSurface(craft, bodyPos + craft.LandedOffset, bodyPos, body.RadiusM);
            craft.LandedOffset = settled - bodyPos;
        }
        craft.Position = bodyPos + craft.LandedOffset;
    }

    /// <summary>Restores a landed state from a save; validates the body and the offset.</summary>
    public void RestoreLanding(CraftId craftId, BodyId bodyId, Vector2D offset)
    {
        var craft = GetCraft(craftId);
        if (bodyId.IsNone) { craft.LandedOnBodyId = BodyId.None; return; }
        GetBody(bodyId);
        if (!(offset.Length > 0)) throw new ArgumentException("Landed offset must be non-zero.", nameof(offset));
        craft.LandedOnBodyId = bodyId;
        craft.LandedOffset = offset;
    }

    /// <summary>Sets what the craft's brake matches; <see cref="BodyId.None"/> means "stop relative to the sector".</summary>
    public void SetTarget(CraftId craftId, BodyId target)
    {
        if (!target.IsNone) GetBody(target); // throws for unknown ids
        var craft = GetCraft(craftId);
        craft.TargetBodyId = target;
        if (!target.IsNone) craft.TargetCraftId = CraftId.None;
    }

    /// <summary>Body velocity in the sector frame (sum along its parent chain).</summary>
    public Vector2D GetVelocityAt(BodyId id, double timeS) => BodyVelocityAt(IndexOf(id), timeS);

    /// <summary>Craft motion relative to a body, at the current tick. For HUDs now, and for autopilots and jobs later.</summary>
    public RelativeMotion GetRelativeMotion(CraftId craftId, BodyId bodyId)
    {
        var craft = GetCraft(craftId);
        var body = GetBody(bodyId);
        var offset = GetPosition(bodyId) - craft.Position;
        var distance = offset.Length;
        var relativeVelocity = craft.Velocity - GetVelocityAt(bodyId, TimeS);
        var closing = distance > 0 ? (relativeVelocity.X * offset.X + relativeVelocity.Y * offset.Y) / distance : 0;
        return new RelativeMotion(distance - body.RadiusM, relativeVelocity.Length, closing);
    }

    /// <summary>Body whose surface is closest to <paramref name="point"/>, with that surface distance (negative = inside).</summary>
    public (CelestialBodyState Body, double SurfaceDistanceM) NearestBody(Vector2D point)
    {
        CelestialBodyState? best = null;
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            var distance = BodyPositionAt(i, TimeS).DistanceTo(point) - body.RadiusM;
            if (distance < bestDistance) { best = body; bestDistance = distance; }
        }
        return best is null ? throw new InvalidOperationException("Sector has no bodies.") : (best, bestDistance);
    }

    /// <summary>Body centre at the current tick.</summary>
    public Vector2D GetPosition(BodyId id) => GetPositionAt(id, TimeS);

    /// <summary>
    /// Body centre at an arbitrary simulation time. The view passes a fractional time between
    /// ticks for smooth rendering; this never changes state.
    /// </summary>
    public Vector2D GetPositionAt(BodyId id, double timeS) => BodyPositionAt(IndexOf(id), timeS); // parents exist (AddBody), so the chain ends
}
