using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Raids through warp gates (#95; the creator's choices). A civilization at <see cref="InvadeAttitude"/> or worse may send a raid
/// through your gate in its system instead of destroying it: the raiders spare the gate, and once they reach it while it works they jump to its
/// other end and attack there, for <see cref="InvasionS"/> at most. Never in the first hour. A <b>gate lock</b> part lets you close a gate: then
/// nothing goes through it, either way, and the raid attacks the gate as usual.
/// </summary>
public sealed partial class SectorState
{
    public const double InvadeAttitude = -70, InvadeChance = 0.5, InvasionS = 600, InvadeAfterS = 3_600, InvadeRangeM = 1_200;

    /// <summary>Opens or closes a gate with a working gate lock.</summary>
    public ActionResult SetGateClosed(CraftId gateId, bool closed)
    {
        var gate = GetCraft(gateId);
        if (!IsGate(gate) || gate.IsWreck) return new(false, $"{gate.Name} isn't a working warp gate.");
        if (gate.Stats.GateLockCount == 0) return new(false, $"{gate.Name} has no gate lock: add one in the ship editor and refit it.");
        gate.GateClosed = closed;
        return new(true, closed ? $"{gate.Name} is closed: nothing goes through." : $"{gate.Name} is open.");
    }

    /// <summary>Why nothing goes through a gate now because of a closed end, or null.</summary>
    private string? GateClosedReason(CraftState gate) =>
        gate.GateClosed ? $"{gate.Name} is closed"
        : _craftsById.GetValueOrDefault(gate.GateLinkId) is { GateClosed: true } far ? $"{far.Name} is closed" : null;

    private bool CanInvade(Faction faction, CraftState gate) =>
        faction.Attitude <= InvadeAttitude && TimeS >= InvadeAfterS && GateClosedReason(gate) is null
        && _craftsById.GetValueOrDefault(gate.GateLinkId) is { IsWreck: false } far && IsGate(far);

    /// <summary>Once a second for a raid that means to go through: do they reach the gate, and does it let them?</summary>
    private void StepInvasion(Faction faction, CraftState gate, List<CraftState> raiders)
    {
        if (!CanInvade(faction, gate))
        {
            faction.Invading = false; // closed, or no way through: an ordinary raid after all
            return;
        }
        if (!raiders.Any(r => r.Position.DistanceTo(gate.Position) < InvadeRangeM)) return;
        if (!Operational(gate)) return;
        var exit = _craftsById[gate.GateLinkId];
        var (body, _) = NearestBody(exit.Position);
        var outward = exit.Position - GetPosition(body.Id);
        var bearing = Math.Atan2(outward.Y, outward.X);
        var through = raiders.Where(r => r.Position.DistanceTo(gate.Position) < 3 * InvadeRangeM).ToList();
        for (var i = 0; i < through.Count; i++)
        {
            var raider = through[i];
            var angle = bearing + (i - (through.Count - 1) / 2.0) * 0.15;
            raider.Position = raider.PreviousPosition = exit.Position + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (exit.CollisionRadiusM + 1_500);
            raider.Velocity = exit.Velocity;
            raider.Cruising = false;
            SetTargetCraft(raider.Id, exit.Id);
        }
        // Those too far behind leave.
        foreach (var late in raiders.Except(through).ToList())
        {
            DiscoveredKg -= (long)Math.Round(late.Stats.TotalMassKg) + late.CargoKg;
            RemoveCraft(late);
        }
        faction.Invading = false;
        faction.RaidGateId = exit.Id;
        faction.RaidEndTick = Tick + (long)(InvasionS * FixedStepClock.TicksPerSecond);
        AddEvent($"DANGER: {through.Count} {faction.Name} raider{(through.Count == 1 ? " has" : "s have")} come through to {exit.Name} in the {StarAt(exit.Position)?.Name} system!", EventKind.Danger, TargetRef.Of(exit));
    }
}
