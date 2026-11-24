using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Antimatter (#81) and the quantum drive (#82), with the creator's choices.
/// <list type="bullet">
/// <item><b>Antimatter</b> may only be held in containment, which needs constant power. When the field fails (no power, or more
/// than it holds), <see cref="ContainmentGraceS"/> later it all explodes and damages every craft around, however far the player is.</item>
/// <item><b>Quantum drive:</b> charges from the craft's power, then jumps to its target anywhere in the same star system, arriving matched
/// to it; then cools down. Automated craft charge when their batteries allow and jump long legs on their own.</item>
/// </list>
/// Mass stays exact: exploded antimatter counts as ejected.
/// </summary>
public sealed partial class SectorState
{
    public const string AntimatterMaterial = "antimatter";

    /// <summary>How long a failed containment field lasts before it lets go.</summary>
    public const double ContainmentGraceS = 15;

    /// <summary>Reach and damage (in ordinary parts' worth, at the centre) of an explosion, per square root of a kg.</summary>
    public const double BlastRadiusM = 150, BlastDamage = 3;

    /// <summary>Charge a quantum jump takes per tonne of craft, its shortest jump, and the time to cool down.</summary>
    public const double QuantumKJPerTonne = 2_000, QuantumMinM = 10_000, QuantumCooldownS = 120;

    /// <summary>How far from a body's surface, or a craft's hull, a jump ends.</summary>
    public const double QuantumBodyClearanceM = 3_000, QuantumCraftClearanceM = 600;

    /// <summary>Automated craft charge their quantum drive only above this share of battery.</summary>
    public const double QuantumSpareAbove = 0.5;

    // --- Antimatter ---

    /// <summary>Antimatter a craft can still take (kg); 0 without working containment.</summary>
    public static long AntimatterRoomKg(CraftState craft) =>
        craft.IsWreck ? 0 : Math.Max(0, (long)craft.Stats.AntimatterCapacityKg - craft.Inventory[AntimatterMaterial]);

    private void StepAntimatter()
    {
        List<CraftState>? failed = null;
        foreach (var craft in _crafts)
        {
            var held = craft.Inventory[AntimatterMaterial];
            if (held <= 0) { craft.ContainmentFailS = 0; continue; }
            var contained = !craft.IsWreck && held <= craft.Stats.AntimatterCapacityKg
                            && TryUsePower(craft, craft.Stats.ContainmentPowerKW * FixedStepClock.TickSeconds);
            if (contained) { craft.ContainmentFailS = 0; continue; }
            if (craft.ContainmentFailS <= 0)
                AddEvent($"DANGER: antimatter containment is failing on {craft.Name}! {ContainmentGraceS:0} s until it explodes"
                         + (craft.IsWreck || held > craft.Stats.AntimatterCapacityKg ? " (containment destroyed)." : " (no power)."), EventKind.Danger, TargetRef.Of(craft));
            craft.ContainmentFailS += FixedStepClock.TickSeconds;
            if (craft.ContainmentFailS >= ContainmentGraceS) (failed ??= new()).Add(craft);
        }
        if (failed is null) return;
        foreach (var craft in failed) if (_craftsById.ContainsKey(craft.Id)) Explode(craft);
    }

    private void Explode(CraftState source)
    {
        var kg = source.Inventory[AntimatterMaterial];
        if (kg <= 0 || Parts is null) return;
        source.Inventory.TryRemove(new Dictionary<string, long> { [AntimatterMaterial] = kg });
        Eject(AntimatterMaterial, kg);
        source.ContainmentFailS = 0;
        var radius = BlastRadiusM * Math.Sqrt(kg);
        AddEvent($"{kg} kg of antimatter exploded on {source.Name}.", EventKind.Danger, TargetRef.Of(source));
        _blasts.Add((Tick, source.Position, radius));
        var centre = source.Position;
        foreach (var craft in _crafts.ToList())
        {
            if (craft.Transit is not null || craft.Position.DistanceTo(centre) > radius + craft.CollisionRadiusM) continue;
            foreach (var (cell, at) in CellPositions(craft, craft.Position, craft.HeadingRad).ToList())
            {
                var distance = at.DistanceTo(centre);
                if (distance >= radius || craft.IsWreck) continue;
                var index = IndexOfCell(craft, cell.Pos); // parts break off as it goes
                if (index >= 0) HitCell(craft, index, BlastDamage * Math.Sqrt(kg) * (1 - distance / radius));
            }
        }
    }

    private static int IndexOfCell(CraftState craft, Construction.GridPos pos)
    {
        for (var i = 0; i < craft.Design.Cells.Count; i++) if (craft.Design.Cells[i].Pos == pos) return i;
        return -1;
    }

    private readonly List<(long Tick, Vector2D At, double RadiusM)> _blasts = new();

    /// <summary>Explosions of the last second, for drawing. Not saved.</summary>
    public IReadOnlyList<(long Tick, Vector2D At, double RadiusM)> Blasts => _blasts;

    // --- Quantum drive ---

    /// <summary>Charge this craft needs for a jump, kJ.</summary>
    public static double QuantumChargeNeededKJ(CraftState craft) => QuantumKJPerTonne * craft.MassKg / 1000;

    /// <summary>Why a craft can't jump now (charge aside), or null.</summary>
    public string? QuantumBlocker(CraftState craft)
    {
        if (craft.Stats.QuantumDriveKW <= 0) return $"{craft.Name} has no quantum drive.";
        if (craft.IsWreck || !Operational(craft)) return $"{craft.Name} isn't working.";
        if (craft.IsDocked) return "Undock first.";
        if (craft.IsLanded) return "Lift off first.";
        if (craft.Cruising) return "Drop out of cruise first.";
        var wait = craft.QuantumReadyTick - Tick;
        return wait > 0 ? $"The quantum drive is cooling down ({wait * FixedStepClock.TickSeconds:0} s)." : null;
    }

    /// <summary>The player asks for a jump to the craft's target: it charges up, then goes. Asking again cancels.</summary>
    public ActionResult RequestQuantumJump(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (craft.QuantumRequested)
        {
            craft.QuantumRequested = false;
            return new(true, $"{craft.Name}: quantum jump cancelled.");
        }
        if (QuantumBlocker(craft) is { } why) return new(false, why);
        if (QuantumGoal(craft) is not { } goal) return new(false, "Target a body or a craft to jump to.");
        if (StarAt(goal.Position) != StarAt(craft.Position)) return new(false, "Quantum jumps only reach within a star system: use a warp gate.");
        if (goal.Position.DistanceTo(craft.Position) < QuantumMinM) return new(false, $"Too close: quantum jumps go at least {QuantumMinM / 1000:0} km.");
        craft.QuantumRequested = true;
        return new(true, $"{craft.Name}: charging the quantum drive.");
    }

    /// <summary>Where a jump to the craft's target would end, and the velocity there.</summary>
    private (Vector2D Position, Vector2D Velocity)? QuantumGoal(CraftState craft)
    {
        if (!craft.TargetBodyId.IsNone && _byId.TryGetValue(craft.TargetBodyId, out var body))
        {
            var centre = GetPosition(body.Id);
            return (Beside(centre, body.RadiusM + craft.CollisionRadiusM + QuantumBodyClearanceM, craft.Position), GetVelocityAt(body.Id, TimeS));
        }
        if (craft.TargetCraftId.IsNone || !_craftsById.TryGetValue(craft.TargetCraftId, out var other)) return null;
        var spot = Beside(other.Position, other.CollisionRadiusM + craft.CollisionRadiusM + QuantumCraftClearanceM, craft.Position);
        return (ClearOfBodies(spot, craft), CraftVelocityAt(other, TimeS));
    }

    private static Vector2D Beside(Vector2D centre, double distance, Vector2D from)
    {
        var away = from - centre;
        return centre + (away.Length > 1 ? away * (distance / away.Length) : new Vector2D(distance, 0));
    }

    /// <summary>Moves a point straight up out of any body it would be in or too close to.</summary>
    private Vector2D ClearOfBodies(Vector2D point, CraftState craft)
    {
        var (body, gap) = NearestSurface(point, TimeS);
        const double clear = 400;
        if (gap >= clear + craft.CollisionRadiusM) return point;
        return Beside(GetPosition(body.Id), body.RadiusM + clear + craft.CollisionRadiusM, point);
    }

    private void QuantumJump(CraftState craft, Vector2D position, Vector2D velocity)
    {
        craft.QuantumChargeKJ = 0;
        craft.QuantumRequested = false;
        craft.QuantumReadyTick = Tick + (long)(QuantumCooldownS * FixedStepClock.TicksPerSecond);
        craft.RailOrbit = null;
        craft.Position = craft.PreviousPosition = position;
        craft.Velocity = velocity;
        ReleaseClaim(craft);
    }

    /// <summary>Charging, and jumps the player asked for.</summary>
    private void StepQuantum()
    {
        if (_blasts.Count > 0) _blasts.RemoveAll(b => b.Tick < Tick - FixedStepClock.TicksPerSecond);
        foreach (var craft in _crafts)
        {
            var s = craft.Stats;
            if (s.QuantumDriveKW <= 0 || craft.IsWreck) { craft.QuantumChargeKJ = 0; craft.QuantumRequested = false; continue; }
            var need = QuantumChargeNeededKJ(craft);
            var wants = craft.QuantumRequested;
            if (!wants && craft.IsAutomated && Tick >= craft.QuantumReadyTick)
            {
                var root = craft.PowerRoot ?? craft;
                wants = root.EnergyPoolKJ > QuantumSpareAbove * root.NetworkCapacityKJ;
            }
            if (wants && craft.QuantumChargeKJ < need && Operational(craft))
            {
                var kJ = Math.Min(s.QuantumDriveKW * FixedStepClock.TickSeconds, need - craft.QuantumChargeKJ);
                if (TryUsePower(craft, kJ)) craft.QuantumChargeKJ += kJ;
            }
            if (!craft.QuantumRequested || craft.QuantumChargeKJ + 1e-6 < need || QuantumBlocker(craft) is not null) continue;
            if (QuantumGoal(craft) is not { } goal || StarAt(goal.Position) != StarAt(craft.Position)) { craft.QuantumRequested = false; continue; }
            QuantumJump(craft, goal.Position, goal.Velocity);
            AddEvent($"{craft.Name} made a quantum jump.", EventKind.Fleet, TargetRef.Of(craft));
        }
    }

    private bool QuantumReady(CraftState craft) =>
        craft.Stats.QuantumDriveKW > 0 && craft.IsAutomated && craft.QuantumChargeKJ + 1e-6 >= QuantumChargeNeededKJ(craft) && QuantumBlocker(craft) is null;

    /// <summary>A craft far from the player, under way on a long leg, jumps as soon as its drive is charged. True if it did.</summary>
    private bool TryQuantumInTransit(CraftState craft, Vector2D goal, Vector2D goalVelocity)
    {
        if (goal.DistanceTo(craft.Position) < AutopilotCruiseMinDistanceM || !QuantumReady(craft) || StarAt(goal) != StarAt(craft.Position)) return false;
        craft.Transit = null;
        QuantumJump(craft, ClearOfBodies(goal, craft), goalVelocity);
        return true;
    }

    /// <summary>For job autopilots on a long leg: jumps to a little short of the goal if the drive is charged. True if it did.</summary>
    private bool TryQuantumLeg(CraftState craft, Vector2D goal, Vector2D goalVelocity)
    {
        var to = goal - craft.Position;
        if (to.Length < AutopilotCruiseMinDistanceM || !QuantumReady(craft) || StarAt(goal) != StarAt(craft.Position)) return false;
        var spot = goal - to * (AutopilotCruiseArriveM / 2 / to.Length);
        QuantumJump(craft, ClearOfBodies(spot, craft), goalVelocity);
        return true;
    }
}
