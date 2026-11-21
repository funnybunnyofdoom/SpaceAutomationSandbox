using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Authoritative state of one sector: its seed, the current tick and its bodies.
/// No Godot types. Views read from this; they never own or mutate it directly.
/// </summary>
public sealed class SectorState
{
    private readonly List<CelestialBodyState> _bodies = new();
    private readonly Dictionary<BodyId, CelestialBodyState> _byId = new();
    private uint _nextBodyId = 1;
    private readonly List<CraftState> _crafts = new();
    private readonly Dictionary<CraftId, CraftState> _craftsById = new();
    private uint _nextCraftId = 1;

    public SectorState(ulong seed) => Seed = seed;

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

    /// <summary>Advances the sector by exactly one fixed tick.</summary>
    public void Step()
    {
        foreach (var craft in _crafts) FlightModel.Step(craft, FixedStepClock.TickSeconds);
        Tick++;
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
