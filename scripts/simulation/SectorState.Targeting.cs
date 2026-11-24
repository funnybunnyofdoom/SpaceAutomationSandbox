using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>What the target key steps through (#107).</summary>
public enum TargetKind { Stations, Ships, Bodies, Enemies, Wrecks, Others }

/// <summary>Something that can be targeted: a craft or a body.</summary>
public readonly record struct TargetRef(CraftId Craft, BodyId Body)
{
    public static readonly TargetRef None = new(CraftId.None, BodyId.None);
    public bool IsNone => Craft.IsNone && Body.IsNone;
    public static TargetRef Of(CraftState craft) => new(craft.Id, BodyId.None);
    public static TargetRef Of(CelestialBodyState body) => new(CraftId.None, body.Id);
}

/// <summary>The targets of a kind: nearest first, at most the limit asked for, and how many there are in all.</summary>
public sealed record TargetList(IReadOnlyList<TargetRef> Targets, int Total);

/// <summary>
/// Choosing targets (#107): what's of a kind in the same star system as a craft, nearest first (by surface), ties by id.
/// Flying to a target (#108): the ship turns to its target and cruises there by itself; any flight control takes over.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>A craft dropped out of cruise this far from a craft it was flying to.</summary>
    public const double FlyToStopM = 3_000;

    /// <summary>Nearer than this, flying there by hand is quicker.</summary>
    public const double FlyToMinM = 6_000;

    /// <summary>The kind a craft is listed under for the player.</summary>
    public TargetKind KindOf(CraftState craft) =>
        craft.IsWreck ? TargetKind.Wrecks
        : IsPlayers(craft) ? craft.Stats.TotalThrustN > 0 ? TargetKind.Ships : TargetKind.Stations
        : IsEnemy(craft) ? TargetKind.Enemies : TargetKind.Others;

    public TargetList TargetsOf(CraftState from, TargetKind kind, int limit)
    {
        var star = StarAt(from.Position)?.Id;
        var found = new List<(double Distance, uint Id, TargetRef Target)>();
        if (kind == TargetKind.Bodies)
        {
            foreach (var body in _bodies)
                if (body.Id == star || StarOf(body.Id)?.Id == star)
                    found.Add((GetPosition(body.Id).DistanceTo(from.Position) - body.RadiusM, body.Id.Value, TargetRef.Of(body)));
        }
        else
        {
            foreach (var craft in _crafts)
                if (craft.Id != from.Id && KindOf(craft) == kind && StarAt(craft.Position)?.Id == star)
                    found.Add((craft.Position.DistanceTo(from.Position) - craft.CollisionRadiusM, craft.Id.Value, TargetRef.Of(craft)));
        }
        return new(found.OrderBy(f => f.Distance).ThenBy(f => f.Id).Take(limit).Select(f => f.Target).ToList(), found.Count);
    }

    /// <summary>The next (or previous) target in a list, skipping what no longer exists; None when nothing is left.</summary>
    public TargetRef NextTarget(IReadOnlyList<TargetRef> list, TargetRef current, int step)
    {
        var at = -1;
        for (var i = 0; i < list.Count; i++) if (list[i] == current) at = i;
        if (at < 0) at = step > 0 ? -1 : list.Count;
        for (var n = 0; n < list.Count; n++)
        {
            at = ((at + step) % list.Count + list.Count) % list.Count;
            if (Exists(list[at])) return list[at];
        }
        return TargetRef.None;
    }

    public bool Exists(TargetRef target) => !target.Craft.IsNone ? _craftsById.ContainsKey(target.Craft) : _byId.ContainsKey(target.Body);

    public void SetTarget(CraftId craftId, TargetRef target)
    {
        if (!target.Craft.IsNone) { SetTargetCraft(craftId, target.Craft); return; }
        SetTargetCraft(craftId, CraftId.None);
        SetTarget(craftId, target.Body);
    }

    // --- Flying to it ---

    /// <summary>Why a craft can't fly itself to a target now, or null.</summary>
    public string? FlyToBlocker(CraftState craft, TargetRef target)
    {
        if (target.IsNone || !Exists(target)) return "Choose a target first.";
        if (craft.IsAutomated) return "Its job is flying it: stop the job first.";
        var (goal, _, radius, name) = FlyToGoal(craft, target);
        if (StarAt(goal)?.Id != StarAt(craft.Position)?.Id) return $"{name} is in another system: jump through a warp gate.";
        if (goal.DistanceTo(craft.Position) - radius < FlyToMinM) return $"{name} is close: fly there by hand.";
        if (!craft.Cruising && CruiseBlocker(craft) is { } blocked) return blocked;
        var positions = AllBodyPositions(TimeS);
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            // The goal itself, and the body a craft is close to, are where the cruise should end.
            if (body.Id == target.Body || positions[i].DistanceTo(goal) - body.RadiusM < CruiseDropoutM + 1_000) continue;
            if (!SegmentClears(craft.Position, goal, positions[i], body.RadiusM + CruiseDropoutM)) return $"{body.Name} is in the way.";
        }
        return null;
    }

    /// <summary>Turns to the target and cruises there.</summary>
    public ActionResult FlyTo(CraftId craftId, TargetRef target)
    {
        var craft = GetCraft(craftId);
        if (FlyToBlocker(craft, target) is { } reason) return new(false, reason);
        SetTarget(craftId, target);
        craft.FlyToPhase = craft.Cruising ? 2 : 1;
        return new(true, $"Flying to {FlyToGoal(craft, target).Name}. Any flight control stops it.");
    }

    private (Vector2D Position, Vector2D Velocity, double Radius, string Name) FlyToGoal(CraftState craft, TargetRef target)
    {
        if (!target.Craft.IsNone)
        {
            var other = GetCraft(target.Craft);
            return (other.Position, BrakeReferenceForCraft(craft, other, TimeS), other.CollisionRadiusM, other.Name);
        }
        var body = GetBody(target.Body);
        return (GetPosition(body.Id), GetVelocityAt(body.Id, TimeS), body.RadiusM, body.Name);
    }

    private readonly List<(CraftState Craft, ShipControls Given)> _flownTo = new();

    /// <summary>Before anything moves: craft flying to their target get its controls for this tick (given back after, see <see cref="EndFlyTo"/>).</summary>
    private void StepFlyTo()
    {
        foreach (var craft in _crafts)
        {
            if (craft.FlyToPhase == 0) continue;
            var target = new TargetRef(craft.TargetCraftId, craft.TargetBodyId);
            if (craft.Controls.Clamped() != ShipControls.Idle || target.IsNone || !Exists(target) || craft.IsAutomated || craft.IsWreck
                || craft.IsDocked || craft.IsLanded || craft.IsOnRails || craft.FlyToPhase == 2 && !craft.Cruising)
            {
                craft.FlyToPhase = 0; // taken over, arrived, or nothing to fly to
                continue;
            }
            var (goal, velocity, radius, name) = FlyToGoal(craft, target);
            var toGoal = goal - craft.Position;
            var distance = toGoal.Length;
            if (!(distance > 0)) { craft.FlyToPhase = 0; continue; }
            var direction = toGoal * (1 / distance);
            ShipControls controls;
            if (craft.Cruising)
            {
                if (distance - radius < FlyToStopM + CruiseSpeedMps * FixedStepClock.TickSeconds)
                {
                    StopCruise(craft, velocity, $"arrived near {name}");
                    craft.FlyToPhase = 0;
                    continue;
                }
                controls = new ShipControls(0, Autopilot.TurnToward(craft, direction), false);
            }
            else
            {
                if (distance - radius < FlyToMinM / 2) { craft.FlyToPhase = 0; continue; }
                var lined = craft.Forward.X * direction.X + craft.Forward.Y * direction.Y >= Math.Cos(2 * Math.PI / 180);
                if (lined)
                {
                    if (FlyToBlocker(craft, target) is { } reason)
                    {
                        craft.FlyToPhase = 0;
                        craft.CruiseStopReason = reason.TrimEnd('.');
                        continue;
                    }
                    craft.Cruising = true;
                    craft.CruiseStopReason = null;
                    craft.FlyToPhase = 2;
                }
                controls = new ShipControls(0, Autopilot.TurnToward(craft, direction), !lined);
            }
            _flownTo.Add((craft, craft.Controls));
            craft.Controls = controls;
        }
    }

    private void EndFlyTo()
    {
        foreach (var (craft, given) in _flownTo) craft.Controls = given;
        _flownTo.Clear();
    }
}
