using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Complete dismantling of bodies (#27). Mineable planets, moons and asteroids can be mined past their
/// deposits: the bulk of the body comes out as rock (ice for icy bodies), and the body shrinks with
/// its mass (radius and gravity follow it). At <see cref="CelestialBodyState.RemnantFraction"/> of its
/// starting mass, with nothing left to mine, the remnant scatters (counted as debris in
/// <see cref="EjectedKg"/>) and the body is gone:
/// <list type="bullet">
/// <item>craft landed on it drift free with its last velocity; craft parked around it leave their
/// parking orbit; targets and abstract legs aimed at it are cleared;</item>
/// <item>jobs that used it wait with "the source body is missing";</item>
/// <item>a body that something still orbits (a planet with moons) keeps its remnant until they're gone.</item>
/// </list>
/// Body ids are never reused.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Bodies dismantled completely so far (names, for messages and diagnostics). In memory only.</summary>
    public IReadOnlyList<string> BodiesGone => _bodiesGone;

    private readonly List<string> _bodiesGone = new();

    /// <summary>Next body id to hand out (saved, so ids of bodies that are gone are never reused).</summary>
    public uint NextBodyId => _nextBodyId;

    public void RestoreNextBodyId(uint next)
    {
        if (next < 1) throw new ArgumentOutOfRangeException(nameof(next));
        _nextBodyId = Math.Max(_nextBodyId, next);
    }

    /// <summary>Why a mined-out body is still here, or null if it will go (or isn't mined out).</summary>
    public string? RemnantReason(CelestialBodyState body)
    {
        if (!body.MinedOut) return null;
        var child = _bodies.FirstOrDefault(b => b.Orbit?.Parent == body.Id);
        return child is null ? null : $"{body.Name} is mined out down to its core; it can't break up while {child.Name} orbits it";
    }

    /// <summary>After mining: bodies with nothing left to mine and nothing orbiting them are gone.</summary>
    private void StepBodiesGone()
    {
        List<CelestialBodyState>? gone = null;
        foreach (var body in _bodies)
            if (body.MinedOut && RemnantReason(body) is null) (gone ??= new()).Add(body);
        if (gone is null) return;
        foreach (var body in gone) RemoveBody(body);
    }

    private void RemoveBody(CelestialBodyState body)
    {
        var velocity = GetVelocityAt(body.Id, TimeS);
        // The remnant scatters as debris: whole kg into the ledger, as the bulk material.
        var remnant = body.WholeMassKg;
        if (remnant > 0) _ejectedKg[body.BulkMaterialId] = _ejectedKg.GetValueOrDefault(body.BulkMaterialId) + remnant;

        foreach (var craft in _crafts)
        {
            if (craft.LandedOnBodyId == body.Id)
            {
                craft.LandedOnBodyId = BodyId.None;
                craft.LandedOffset = Vector2D.Zero;
                craft.IsAnchored = false;
                craft.Velocity = velocity;
            }
            if (craft.RailOrbit?.Parent == body.Id)
            {
                craft.Velocity = RailVelocityAt(craft.RailOrbit, TimeS);
                craft.RailOrbit = null;
            }
            if (craft.TargetBodyId == body.Id) craft.TargetBodyId = BodyId.None;
            if (craft.Transit?.TargetBody == body.Id)
            {
                craft.Transit = null;
                craft.Velocity = velocity;
            }
        }
        _bodies.Remove(body);
        _byId.Remove(body.Id);
        ResetMotionCache();
        _bodiesGone.Add(body.Name);
    }
}
