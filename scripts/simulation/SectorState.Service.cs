using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Repairs and servicing (#3), and automated scouting (#4).
/// <list type="bullet">
/// <item><b>Repairs:</b> a shipyard repairs itself and the craft docked at it, one part at a time,
/// with its fabricator (same rate and power as building; repairs come before the build queue). A
/// damaged part costs its share of the part's materials (health 40% → 60% of the bill, whole kg,
/// rounded up), taken from the yard's stock when the work is done; the worn material is discarded
/// (counted as consumed in <see cref="EjectedKg"/>). A broken-off part is rebuilt at its full bill
/// (the materials become the part again). Damaged parts first, then missing ones in blueprint
/// order. No wear from use yet: only crash damage needs repair.</item>
/// <item><b>Servicing:</b> any automated job takes its craft to the nearest shipyard when a part is
/// below <see cref="ServiceHealthThreshold"/> or broken off, stays docked until fully repaired (or,
/// if the yard lacks materials, until nothing serious is left), then carries on. While the yard lacks
/// materials for its next repair it waits off the port, on the holding ring, so deliveries can dock.
/// Not while landed, mining or climbing off. The job's status says what it is doing and why it's waiting.</item>
/// <item><b>Survey jobs</b> (<see cref="SurveyJob"/>): automated scouts survey bodies by the same rule
/// as the player.</item>
/// </list>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>A part below this health sends an automated craft for repairs.</summary>
    public const double ServiceHealthThreshold = 0.5;

    /// <summary>Has damaged or missing parts.</summary>
    public static bool NeedsRepair(CraftState craft) => craft.HealthList.Count > 0 || craft.IntactDesign is not null;

    /// <summary>Damaged badly enough (or missing parts) that an automated job goes for repairs.</summary>
    public static bool NeedsService(CraftState craft)
    {
        if (craft.IntactDesign is not null) return true;
        foreach (var h in craft.HealthList.Values) if (h < ServiceHealthThreshold) return true;
        return false;
    }

    /// <summary>Parts broken off and not yet rebuilt.</summary>
    public static int MissingParts(CraftState craft) =>
        craft.IntactDesign is { } intact ? intact.Cells.Count - craft.Design.Cells.Count : 0;

    /// <summary>The next repair: the first damaged part (row order), else the first missing part (blueprint order).</summary>
    private static (DesignCell Cell, bool Missing)? NextRepair(CraftState craft)
    {
        DesignCell? damaged = null;
        foreach (var pos in craft.HealthList.Keys)
        {
            if (craft.Design.IndexOf(pos) is var at && at < 0) continue;
            var cell = craft.Design.CellArray[at];
            if (damaged is not { } d || cell.Pos.Y < d.Pos.Y || (cell.Pos.Y == d.Pos.Y && cell.Pos.X < d.Pos.X)) damaged = cell;
        }
        if (damaged is { } found) return (found, false);
        if (craft.IntactDesign is not { } intact) return null;
        foreach (var cell in intact.CellArray) if (craft.Design.IndexOf(cell.Pos) < 0) return (cell, true);
        return null;
    }

    /// <summary>Materials the repair costs (whole kg).</summary>
    private SortedDictionary<string, long> RepairCost(CraftState craft, DesignCell cell, bool missing)
    {
        var bill = Parts!.Get(cell.PartId).Bill.ToWholeKg();
        if (missing) return bill;
        var worn = 1 - craft.HealthList[cell.Pos];
        var cost = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (material, kg) in bill)
        {
            var share = (long)Math.Ceiling(kg * worn - 1e-9);
            if (share > 0) cost[material] = share;
        }
        return cost;
    }

    /// <summary>What a repair at this yard still lacks, for the next part (empty when it can go ahead).</summary>
    private SortedDictionary<string, long> RepairShortfall(CraftState yard, CraftState craft) =>
        Parts is not null && NextRepair(craft) is { } next ? yard.Inventory.Shortfall(RepairCost(craft, next.Cell, next.Missing)) : new();

    private static string Describe(IReadOnlyDictionary<string, long> kg) => string.Join(", ", kg.Select(kv => $"{kv.Value} kg {kv.Key}"));

    /// <summary>One tick of repairs at every shipyard: itself first, then the craft docked at it (by id).</summary>
    private readonly HashSet<CraftId> _repairingYards = new();

    private void StepRepairs()
    {
        _repairingYards.Clear();
        foreach (var craft in _crafts) { craft.RepairStatus = null; craft.YardStatus = null; }
        if (Parts is null) return;
        List<CraftState>? removed = null;
        foreach (var yard in _crafts)
        {
            if (!yard.IsShipyard) continue;
            CraftState? patient = NeedsRepair(yard) ? yard : null;
            if (patient is null)
                foreach (var c in _crafts)
                    if (c.DockedToCraftId == yard.Id && NeedsRepair(c)) { patient = c; break; }
            if (patient is null || NextRepair(patient) is not { } next)
            {
                // No repairs: refits, dismantling and packing (#56, #57).
                if (StepYardWork(yard, removed ??= new())) _repairingYards.Add(yard.Id);
                continue;
            }
            _repairingYards.Add(yard.Id); // the fabricator is busy: no building this tick

            var part = Parts.Get(next.Cell.PartId);
            var cost = RepairCost(patient, next.Cell, next.Missing);
            var work = Math.Max(1, cost.Values.Sum());
            var verb = next.Missing ? "rebuilding" : "repairing";
            if (!Operational(yard)) { patient.RepairStatus = $"repairs at {yard.Name} stopped: {yard.CrewShortReason}"; continue; }
            if (patient.RepairProgressKg < work)
            {
                if (!UseMachinePower(yard, yard.Stats.FabricatorPowerKW * FixedStepClock.TickSeconds))
                {
                    patient.RepairStatus = $"repairs at {yard.Name} stopped: no power";
                    continue;
                }
                patient.RepairProgressKg = Math.Min(work,
                    patient.RepairProgressKg + yard.Stats.FabricationRateKgPerS * Tuning.FabricationRateMultiplier * Trait(yard, _building) * FixedStepClock.TickSeconds);
                patient.RepairStatus = $"{verb} {part.Name} at {yard.Name} ({patient.RepairProgressKg / work * 100:0}%)";
                if (patient.RepairProgressKg < work) continue;
            }
            var shortfall = yard.Inventory.Shortfall(cost);
            if (shortfall.Count > 0)
            {
                patient.RepairStatus = $"waiting: {verb} {part.Name} needs {Describe(shortfall)} more at {yard.Name}";
                continue;
            }
            if (!yard.Inventory.TryRemove(cost)) throw new InvalidOperationException("Inventory changed during a repair.");
            patient.RepairProgressKg = 0;
            if (next.Missing)
            {
                Rebuild(patient, next.Cell);
            }
            else
            {
                foreach (var (material, kg) in cost) _ejectedKg[material] = (_ejectedKg.TryGetValue(material, out var had) ? had : 0) + kg;
                patient.HealthList.Remove(next.Cell.Pos);
                patient.Stats = patient.Design.ComputeStats(Parts, patient.HealthList);
            }
            patient.RepairStatus = $"{verb} {part.Name} at {yard.Name}: done";
        }
        // Craft dismantled or packed this tick leave after the loop (the list mustn't change while iterated).
        if (removed is not null) foreach (var craft in removed) RemoveCraft(craft);
    }

    /// <summary>Puts a broken-off part back (its materials were just taken from the yard).</summary>
    private void Rebuild(CraftState craft, DesignCell cell)
    {
        var intact = craft.IntactDesign!;
        var had = craft.Design;
        var complete = had.CellArray.Length + (had.IndexOf(cell.Pos) < 0 ? 1 : 0) == intact.CellArray.Length;
        var design = complete ? intact : intact.Keep(c => c.Pos == cell.Pos || had.IndexOf(c.Pos) >= 0);
        if (complete) craft.IntactDesign = null;
        ReplaceDesign(craft, design);
    }

    /// <summary>Restores saved repair state: the blueprint before parts broke off, and progress on the current repair.</summary>
    public void RestoreRepair(CraftId craftId, ShipDesign? intactDesign, double progressKg, CraftId serviceAt)
    {
        var craft = GetCraft(craftId);
        if (intactDesign is not null)
        {
            var cells = intactDesign.Cells.ToDictionary(c => c.Pos);
            foreach (var cell in craft.Design.Cells)
                if (!cells.TryGetValue(cell.Pos, out var whole) || whole.PartId != cell.PartId)
                    throw new ArgumentException($"{craftId}'s intact design doesn't contain its part at ({cell.Pos.X},{cell.Pos.Y}).");
            if (intactDesign.Cells.Count == craft.Design.Cells.Count) intactDesign = null; // nothing missing
        }
        if (!(progressKg >= 0)) throw new ArgumentOutOfRangeException(nameof(progressKg));
        if (!serviceAt.IsNone) GetCraft(serviceAt);
        craft.IntactDesign = intactDesign;
        craft.RepairProgressKg = progressKg;
        craft.ServiceAt = serviceAt;
    }

    private CraftState? NearestShipyard(CraftState craft)
    {
        CraftState? best = null;
        var bestDistance = double.MaxValue;
        foreach (var c in _crafts)
        {
            if (c.Id == craft.Id || !c.IsShipyard || c.IsWreck || (c.Stats.TotalThrustN > 0 && !c.IsOnRails && !c.IsLanded) || // not a ship that may fly off (#83)
                c.Stats.DockingPorts.Count == 0) continue;
            var d = c.Position.DistanceTo(craft.Position);
            if (d < bestDistance) { best = c; bestDistance = d; }
        }
        return best;
    }

    /// <summary>
    /// Jobs mid-landing, mining or climbing off finish that first (a miner breaks off on its way out). Breaking off
    /// mid-climb would cut across other miners, and resume the climb at full throttle beside the yard afterwards.
    /// </summary>
    private static bool CanBreakForService(ShipJob job) => job switch
    {
        MiningJob m => (m.State == MiningJobState.Waiting ? m.ResumeState : m.State) is not (MiningJobState.Landing or MiningJobState.Mining or MiningJobState.Lifting),
        _ => true,
    };

    /// <summary>
    /// Servicing for automated craft: returns true when it flies the craft this tick (going to a
    /// shipyard, or docked there for repairs). Decided from saved state only.
    /// </summary>
    private bool StepService(CraftState craft, ShipJob job)
    {
        job.ServiceStatus = null;
        if (craft.ServiceAt.IsNone)
        {
            if (!NeedsService(craft) || craft.IsLanded || !CanBreakForService(job)) return false;
            if (NearestShipyard(craft) is not { } nearest) return false; // nowhere to go: keep working
            craft.ServiceAt = nearest.Id;
        }
        if (!_craftsById.TryGetValue(craft.ServiceAt, out var yard) || !yard.IsShipyard || yard.Stats.DockingPorts.Count == 0)
        {
            craft.ServiceAt = CraftId.None;
            return false;
        }
        // Done when fully repaired; or when only light damage is left and the yard lacks the materials for it.
        if (!NeedsRepair(craft) || (!NeedsService(craft) && RepairShortfall(yard, craft).Count > 0))
        {
            craft.ServiceAt = CraftId.None;
            if (craft.DockedToCraftId == yard.Id) TryUndock(craft.Id);
            return false;
        }

        if (StarAt(yard.Position)?.Id != StarAt(craft.Position)?.Id) return StepGateRoute(craft, job) || false; // yard in another system (#23)
        var damaged = craft.HealthList.Count;
        var missing = MissingParts(craft);
        var what = string.Join(", ", new[] { damaged > 0 ? $"{damaged} damaged" : "", missing > 0 ? $"{missing} broken off" : "" }.Where(s => s.Length > 0));
        // A repair the yard lacks materials for doesn't hold (or claim) a port: whatever brings the materials needs
        // one. The ship waits on the holding ring until the yard has them. One that can't fly stays docked.
        var shortfall = RepairShortfall(yard, craft);
        var blocked = shortfall.Count > 0 && craft.Stats.TotalThrustN > 0;
        if (blocked && craft.DockedToCraftId == yard.Id) TryUndock(craft.Id);
        var approach = FlyToDock(craft, yard, claimBerth: !blocked);
        if (shortfall.Count > 0)
        {
            if (approach == DockApproach.Docked) craft.Controls = ShipControls.Idle;
            else if (approach == DockApproach.NoFreePort) craft.Controls = new ShipControls(0, 0, true);
            job.ServiceStatus = craft.Position.DistanceTo(yard.Position) - yard.CollisionRadiusM > ClaimRangeM
                ? $"flying to {yard.Name} for repairs ({what})"
                : $"waiting: repairs at {yard.Name} need {Describe(shortfall)} more";
            return true;
        }
        switch (approach)
        {
            case DockApproach.Docked:
                craft.Controls = ShipControls.Idle;
                job.ServiceStatus = $"in for repairs at {yard.Name} ({what})";
                break;
            case DockApproach.NoFreePort:
                craft.Controls = new ShipControls(0, 0, true);
                job.ServiceStatus = $"queued for repairs at {yard.Name}";
                break;
            default:
                job.ServiceStatus = $"flying to {yard.Name} for repairs ({what})";
                break;
        }
        return true;
    }

    // --- Survey jobs (#4) -----------------------------------------------------------------------

    /// <summary>Gives a craft a survey job.</summary>
    public ActionResult StartJob(CraftId craftId, SurveyJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        job.Active = true;
        job.WaitReason = null;
        job.State = SurveyJobState.Flying;
        job.TargetBodyId = BodyId.None;
        craft.Job = job;
        return new(true, $"{craft.Name}: surveying the unsurveyed bodies, nearest first.");
    }

    /// <summary>The nearest unsurveyed body (not the star), ties by id; null when all are surveyed.</summary>
    private CelestialBodyState? NearestUnsurveyed(Vector2D from)
    {
        CelestialBodyState? best = null;
        var bestDistance = double.MaxValue;
        foreach (var body in _bodies)
        {
            if (body.Surveyed || body.Kind == BodyKind.Star) continue;
            var d = GetPosition(body.Id).DistanceTo(from) - body.RadiusM;
            if (d < bestDistance) { best = body; bestDistance = d; }
        }
        return best;
    }

    /// <summary>Where a scout heads: half the survey range above the body, on the side it comes from.</summary>
    private double SurveyRadius(CelestialBodyState body, CraftState craft) => body.RadiusM + craft.CollisionRadiusM + SurveyRangeM / 2;

    private void StepSurveyJob(CraftState craft, SurveyJob job)
    {
        if (job.State == SurveyJobState.Waiting)
        {
            if (Tick < job.RetryAtTick) { craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded); return; }
            job.State = SurveyJobState.Flying;
            job.WaitReason = null;
        }
        if (!job.TargetBodyId.IsNone && _byId.TryGetValue(job.TargetBodyId, out var done) && done.Surveyed)
            job.TargetBodyId = BodyId.None; // surveyed (by this scout, or anyone else meanwhile): on to the next
        if (job.TargetBodyId.IsNone || !_byId.ContainsKey(job.TargetBodyId))
        {
            if (NearestUnsurveyed(craft.Position) is not { } next)
            {
                job.State = SurveyJobState.Waiting;
                job.WaitReason = "every body in the system is surveyed";
                job.RetryAtTick = Tick + (long)(JobStationRetryS * FixedStepClock.TicksPerSecond);
                job.TargetBodyId = BodyId.None;
                craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded);
                return;
            }
            job.TargetBodyId = next.Id;
        }
        var target = _byId[job.TargetBodyId];
        job.TargetName = target.Name;

        if (craft.IsDocked) { TryUndock(craft.Id); craft.Controls = ShipControls.Idle; return; }
        if (craft.IsLanded)
        {
            if (craft.IsAnchored) TryRelease(craft.Id);
            var outward = craft.Position - GetPosition(craft.LandedOnBodyId);
            craft.Controls = Autopilot.ThrustAlong(craft, outward * (1 / Math.Max(outward.Length, 1e-9)));
            return;
        }
        SetTarget(craft.Id, target.Id);
        var centre = GetPosition(target.Id);
        var bearing = craft.Position - centre;
        var goal = centre + bearing * (SurveyRadius(target, craft) / Math.Max(bearing.Length, 1e-9));
        craft.Controls = NavigateTo(craft, goal, GetVelocityAt(target.Id, TimeS), BodyId.None, JobCruiseSpeedMps, 0);
    }
}
