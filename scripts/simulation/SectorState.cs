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
    }

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
        foreach (var craft in _crafts)
        {
            var reference = craft.TargetBodyId.IsNone ? Vector2D.Zero : GetVelocityAt(craft.TargetBodyId, midTickS);
            var velocityBefore = craft.Velocity;
            FlightModel.Step(craft, FixedStepClock.TickSeconds, reference);

            if (craft.IsOnRails) UpdateOnRails(craft, craft.Velocity - velocityBefore, endS);
            else if (craft.IsLanded) UpdateLanded(craft, craft.Velocity - velocityBefore, endS);
            else ResolveContact(craft, endS);
        }

        // Shipyards advance after movement, so a finished craft is placed against the yard's new position.
        StepFabrication(endS);
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
            return;
        }

        craft.Velocity = bodyVel;
        craft.Position = bodyPos + craft.LandedOffset;
    }

    private void ResolveContact(CraftState craft, double endS)
    {
        foreach (var body in _bodies) // id order: deterministic if two bodies overlap
        {
            var bodyPos = GetPositionAt(body.Id, endS);
            var offset = craft.Position - bodyPos;
            var contactDistance = body.RadiusM + craft.CollisionRadiusM;
            var distance = offset.Length;
            if (distance >= contactDistance) continue;

            var normal = distance > 1e-9 ? offset * (1.0 / distance) : new Vector2D(0, -1);
            craft.LandedOnBodyId = body.Id;
            craft.LandedOffset = normal * contactDistance;
            craft.Position = bodyPos + craft.LandedOffset;
            craft.Velocity = GetVelocityAt(body.Id, endS);
            OnLanded(craft);
            return;
        }
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
        GetCraft(craftId).TargetBodyId = target;
    }

    /// <summary>Body velocity in the sector frame (sum along its parent chain).</summary>
    public Vector2D GetVelocityAt(BodyId id, double timeS)
    {
        var velocity = Vector2D.Zero;
        var body = GetBody(id);
        while (body.Orbit is { } orbit)
        {
            velocity += orbit.VelocityAt(timeS);
            body = GetBody(orbit.Parent);
        }
        return velocity;
    }

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
        foreach (var body in _bodies)
        {
            var distance = GetPosition(body.Id).DistanceTo(point) - body.RadiusM;
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
    public Vector2D GetPositionAt(BodyId id, double timeS)
    {
        var position = Vector2D.Zero;
        var body = GetBody(id);
        // Parents must already exist (enforced in AddBody), so this chain is finite.
        while (body.Orbit is { } orbit)
        {
            position += orbit.OffsetAt(timeS);
            body = GetBody(orbit.Parent);
        }
        return position;
    }
}
