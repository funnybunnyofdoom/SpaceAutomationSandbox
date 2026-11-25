using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>What a check of a program found about a step: whether it'd be done now, and what's in the way.</summary>
public sealed record StepCheck(int StepId, bool Reached, string Note, bool Wrong);

/// <summary>
/// Running programs (#120). Each ship works through its program a step at a time; steps that take no time (tests, messages, passing
/// over) are done at once, at most <see cref="ProgramOpsPerTick"/> a tick. A step that can't be done says why, and after its own time
/// limit goes on as it says to. Docking, flying, mining and warp gates are as for the other jobs.
/// </summary>
public sealed partial class SectorState
{
    public const int ProgramOpsPerTick = 80;

    /// <summary>Between tries, when a whole round had nothing to do, and between one ship's messages.</summary>
    public const double ProgramIdleS = 5, ProgramMessageS = 60;

    /// <summary>A wait for something looks this often.</summary>
    public const double ProgramLookS = 0.5;

    private readonly SortedDictionary<int, JobProgram> _programs = new();

    public IReadOnlyCollection<JobProgram> Programs => _programs.Values;

    public JobProgram? ProgramById(int id) => _programs.GetValueOrDefault(id);

    /// <summary>Ships running a program.</summary>
    public int ProgramUsers(int id)
    {
        var count = 0;
        foreach (var craft in _crafts) if (craft.Job is ProgramJob { Active: true } job && job.ProgramId == id) count++;
        return count;
    }

    /// <summary>Adds a program (id 0), or changes one for every ship that runs it; each keeps its place where its step is still there.</summary>
    public ActionResult SaveProgram(int id, string name, IReadOnlyList<ProgramStep> steps, out int savedAs)
    {
        savedAs = id;
        name = CleanName(name);
        if (Parts is null) return new(false, "Part data isn't loaded.");
        if (JobProgram.Problem(name, steps, Parts.Materials) is { } why) return new(false, why);
        if (id != 0 && !_programs.ContainsKey(id)) return new(false, "That program is gone.");
        if (_programs.Values.Any(p => p.Id != id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return new(false, $"Another program is called {name}.");
        if (id == 0) id = _programs.Count == 0 ? 1 : _programs.Keys.Last() + 1;
        var before = _programs.GetValueOrDefault(id);
        var program = _programs[id] = new JobProgram(id, name, steps);
        savedAs = id;
        var ships = 0;
        foreach (var craft in _crafts)
        {
            if (craft.Job is not ProgramJob job || job.ProgramId != id) continue;
            ships++;
            if (job.StepId == 0) continue;
            var now = ProgramEdit.Find(steps, job.StepId);
            if (now is null) GoTo(job, 0);
            else if (before is null || !SameStep(ProgramEdit.Find(before.Steps, job.StepId), now)) GoTo(job, job.StepId); // it's another step now: begin it again
        }
        return new(true, $"{name} saved{(ships > 0 ? $" for {ships} ship{(ships == 1 ? "" : "s")}" : "")}.");
    }

    private static bool SameStep(ProgramStep? a, ProgramStep b) =>
        a is not null && System.Text.Json.JsonSerializer.Serialize(a, JobProgram.JsonOptions) == System.Text.Json.JsonSerializer.Serialize(b, JobProgram.JsonOptions);

    public ActionResult DeleteProgram(int id)
    {
        if (!_programs.TryGetValue(id, out var program)) return new(false, "That program is gone.");
        if (ProgramUsers(id) is > 0 and var users) return new(false, $"{users} ship{(users == 1 ? " runs" : "s run")} {program.Name}: stop {(users == 1 ? "it" : "them")} first.");
        _programs.Remove(id);
        return new(true, $"{program.Name} deleted.");
    }

    internal void RestoreProgram(JobProgram program) => _programs[program.Id] = program;

    public ActionResult StartJob(CraftId craftId, ProgramJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        if (!_programs.TryGetValue(job.ProgramId, out var program)) return new(false, "That program is gone.");
        job.Active = true;
        job.WaitReason = null;
        GoTo(job, 0);
        craft.Job = job;
        return new(true, $"{craft.Name}: running {program.Name}.");
    }

    private static void GoTo(ProgramJob job, int stepId)
    {
        job.StepId = stepId;
        job.Begun = false;
        job.BlockedSince = 0;
        job.RetryAtTick = 0;
        job.Holding = false;
        job.RepairAsked = false;
        job.Mining = null;
        job.WaitReason = null;
    }

    // --- Tests ---

    /// <summary>Does a condition hold for a ship now? Stations that are gone count as having nothing.</summary>
    public bool Holds(ProgramCondition condition, CraftState craft, double stepSeconds)
    {
        foreach (var test in condition.Tests)
            if (Holds(test, craft, stepSeconds) == condition.Any) return condition.Any;
        return !condition.Any;
    }

    private bool Holds(ProgramTest test, CraftState craft, double stepSeconds) => Measure(test, craft, stepSeconds) >= test.Value - 1e-9 != test.Below;

    /// <summary>What a test looks at, now, in its own unit.</summary>
    public double Measure(ProgramTest test, CraftState craft, double stepSeconds)
    {
        var station = test.StationId != 0 && _craftsById.TryGetValue(new CraftId(test.StationId), out var there) && !there.IsWreck && IsPlayers(there) ? there : null;
        return test.Kind switch
        {
            TestKind.CargoPercent => craft.CargoCapacityKg > 0 ? 100.0 * craft.CargoKg / craft.CargoCapacityKg : 0,
            TestKind.CargoTonnes => craft.Inventory[test.Material] / 1000.0,
            TestKind.PowerPercent => craft.Stats.EnergyCapacityKJ > 0 ? 100 * craft.StoredEnergyKJ / craft.Stats.EnergyCapacityKJ : 100,
            TestKind.FuelTonnes => craft.Inventory[FuelMaterial] / 1000.0,
            TestKind.HealthPercent => HealthPercent(craft),
            TestKind.StepMinutes => stepSeconds / 60,
            TestKind.StationStockTonnes => (station?.Inventory[test.Material] ?? 0) / 1000.0,
            TestKind.StationFreePorts => station is null ? 0 : FreePorts(station).Count,
            _ => 0,
        };
    }

    /// <summary>Parts whole and working, of those the craft was built with.</summary>
    public static double HealthPercent(CraftState craft)
    {
        var all = craft.Design.Cells.Count + craft.PartsLost;
        var lost = (double)craft.PartsLost;
        foreach (var health in craft.PartHealth.Values) lost += 1 - health;
        return all > 0 ? 100 * (1 - lost / all) : 100;
    }

    // --- Running ---

    private enum StepResult { Done, Busy, Blocked }

    private void StepProgram(CraftState craft, ProgramJob job)
    {
        if (!_programs.TryGetValue(job.ProgramId, out var program))
        {
            job.WaitReason = "its program is gone";
            job.Holding = true;
            craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded);
            return;
        }
        if (Tick < job.RetryAtTick)
        {
            craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded);
            return;
        }
        var at = job.StepId == 0 ? 0 : Math.Max(0, program.PlaceOf(job.StepId));
        var rounds = 0;
        for (var n = 0; n < ProgramOpsPerTick; n++)
        {
            if (at >= program.Ops.Count)
            {
                job.CompletedCycles++;
                at = 0;
                if (++rounds > 1 || n + 1 >= ProgramOpsPerTick) break;
                continue;
            }
            var op = program.Ops[at];
            if (op.Step is not { } step) { at = op.Target; continue; }
            if (step.Kind == StepKind.If)
            {
                GoTo(job, step.Id);
                at = Holds(step.Until!, craft, 0) ? at + 1 : op.Target;
                continue;
            }
            if (!job.Begun || job.StepId != step.Id)
            {
                GoTo(job, step.Id);
                if (step.OnlyIf is { } only && !Holds(only, craft, 0)) { at++; continue; }
                job.Begun = true;
                job.BegunTick = Tick;
            }
            job.Holding = false;
            var result = Do(craft, job, step, out var words);
            var prefix = $"step {program.NumberOf(step.Id)} of {program.Count}: ";
            if (result == StepResult.Done)
            {
                job.BlockedSince = 0;
                job.WaitReason = null;
                job.Begun = false;
                at++;
                continue;
            }
            if (result == StepResult.Busy)
            {
                job.BlockedSince = 0;
                job.WaitReason = null;
                job.Doing = prefix + words;
                return;
            }
            if (job.BlockedSince == 0) job.BlockedSince = Math.Max(1, Tick);
            job.WaitReason = prefix + words;
            job.Holding |= !craft.IsDocked && !craft.IsLanded;
            if (step.GiveUpSeconds <= 0 || step.Fallback == StepFallback.KeepTrying || (Tick - job.BlockedSince) * FixedStepClock.TickSeconds < step.GiveUpSeconds) return;
            switch (step.Fallback)
            {
                case StepFallback.NextStep: GoTo(job, step.Id); at++; continue;
                case StepFallback.FirstStep: GoTo(job, 0); at = 0; rounds++; if (rounds > 1) break; continue;
                default:
                    StopJob(craft.Id);
                    AddEvent($"{craft.Name} stopped its program {program.Name}: {words}.", EventKind.Fleet, TargetRef.Of(craft));
                    return;
            }
            break;
        }
        // Round and round with nothing to do: look again in a while.
        GoTo(job, 0);
        job.RetryAtTick = Tick + (long)(ProgramIdleS * FixedStepClock.TicksPerSecond);
        job.WaitReason = "nothing to do in the whole program just now";
        job.Holding = true;
        craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded);
    }

    private double StepSeconds(ProgramJob job) => (Tick - job.BegunTick) * FixedStepClock.TickSeconds;

    /// <summary>The station a step is at: named, or wherever the ship is docked. Null with the reason.</summary>
    private CraftState? StepStation(CraftState craft, ProgramStep step, out string why)
    {
        why = "";
        if (step.StationId == 0)
        {
            if (craft.IsDocked && _craftsById.TryGetValue(craft.DockedToCraftId, out var here)) return here;
            why = "no station is chosen, and it isn't docked";
            return null;
        }
        if (!_craftsById.TryGetValue(new CraftId(step.StationId), out var station) || station.IsWreck) { why = StationGone("station"); return null; }
        if (station.Id == craft.Id) { why = "the station chosen is the ship itself"; return null; }
        if (!IsPlayers(station)) { why = $"{station.Name} isn't yours"; return null; }
        if (station.Stats.DockingPorts.Count == 0) { why = $"{station.Name} has no docking port"; return null; }
        return station;
    }

    /// <summary>Gets docked at the step's station. Null when it is; else how it's getting on.</summary>
    private StepResult? GetDocked(CraftState craft, CraftState station, out string words)
    {
        switch (FlyToDock(craft, station))
        {
            case DockApproach.Docked: words = ""; return null;
            case DockApproach.NoFreePort:
                craft.Controls = new ShipControls(0, 0, true);
                words = $"queued for a berth at {station.Name}";
                return StepResult.Blocked;
            default: words = $"flying to {station.Name}"; return StepResult.Busy;
        }
    }

    private StepResult Do(CraftState craft, ProgramJob job, ProgramStep step, out string words)
    {
        words = "";
        switch (step.Kind)
        {
            case StepKind.Message:
                if (step.Text.Length > 0 && (job.LastMessageTick < 0 || (Tick - job.LastMessageTick) * FixedStepClock.TickSeconds >= ProgramMessageS))
                {
                    job.LastMessageTick = Tick;
                    AddEvent($"{craft.Name}: {step.Text}", EventKind.Fleet, TargetRef.Of(craft));
                }
                return StepResult.Done;

            case StepKind.FlyTo when step.BodyId != 0:
                return FlyNear(craft, step, out words);

            case StepKind.FlyTo:
            {
                if (StepStation(craft, step, out words) is not { } station) return Hold(craft);
                if (craft.DockedToCraftId == station.Id) return StepResult.Done;
                var approach = FlyToDock(craft, station, claimBerth: false);
                words = $"flying to {station.Name}";
                return approach == DockApproach.Underway ? StepResult.Busy : StepResult.Done; // on the holding ring
            }

            case StepKind.Dock:
            {
                if (StepStation(craft, step, out words) is not { } station) return Hold(craft);
                return GetDocked(craft, station, out words) ?? StepResult.Done;
            }

            case StepKind.Load or StepKind.Refuel:
            {
                if (StepStation(craft, step, out words) is not { } station) return Hold(craft);
                var material = step.Kind == StepKind.Refuel ? FuelMaterial : step.Material;
                if (material.Length == 0) { words = "no material is chosen"; return Hold(craft); }
                var want = step.Tonnes > 0 ? (long)Math.Round(step.Tonnes * 1000) : long.MaxValue;
                if (craft.Inventory[material] >= want || craft.FreeCargoKg <= 0 && (want == long.MaxValue || !step.UntilDone)) { craft.Controls = Still(craft); return StepResult.Done; }
                if (craft.Stats.CargoCapacityKg <= 0) { words = $"{craft.Name} has no cargo bay"; return Hold(craft); }
                if (GetDocked(craft, station, out words) is { } going) return going;
                craft.Controls = ShipControls.Idle;
                var take = Math.Min(MaxTransferKg(station.Id, craft.Id, material), want - craft.Inventory[material]);
                if (take > 0) TryTransfer(station.Id, craft.Id, material, take);
                if (!step.UntilDone || craft.Inventory[material] >= want || want == long.MaxValue && craft.FreeCargoKg <= 0) return StepResult.Done;
                words = craft.FreeCargoKg <= 0 ? "the hold is full of other cargo" : $"{station.Name} has no more {material} ({craft.Inventory[material] / 1000.0:0.0}{(want == long.MaxValue ? "" : $" of {step.Tonnes:0.0}")} t aboard)";
                return Leave(craft, job); // don't hold the port (whatever brings it needs one)
            }

            case StepKind.Unload:
            {
                if (StepStation(craft, step, out words) is not { } station) return Hold(craft);
                var left = (long)Math.Round(step.Tonnes * 1000);
                var materials = step.Material.Length > 0 ? new List<string> { step.Material } : craft.Inventory.Kg.Keys.Where(m => m != FuelMaterial).ToList();
                if (materials.All(m => craft.Inventory[m] <= left)) { craft.Controls = Still(craft); return StepResult.Done; }
                if (GetDocked(craft, station, out words) is { } going) return going;
                craft.Controls = ShipControls.Idle;
                foreach (var material in materials)
                {
                    var give = Math.Min(MaxTransferKg(craft.Id, station.Id, material), craft.Inventory[material] - left);
                    if (give > 0 && TryTransfer(craft.Id, station.Id, material, give).Done) job.DeliveredKg += give;
                }
                if (!step.UntilDone || materials.All(m => craft.Inventory[m] <= left)) return StepResult.Done;
                words = $"{station.Name} is full";
                return Leave(craft, job);
            }

            case StepKind.Mine:
                return DoMine(craft, job, step, out words);

            case StepKind.Wait:
            {
                var seconds = StepSeconds(job);
                if (step.Seconds > 0 && seconds >= step.Seconds || step.Seconds <= 0 && step.Until is null || step.Until is { } until && Holds(until, craft, seconds)) return StepResult.Done;
                craft.Controls = Still(craft);
                job.Holding = !craft.IsDocked && !craft.IsLanded;
                job.RetryAtTick = Tick + (long)(ProgramLookS * FixedStepClock.TicksPerSecond);
                words = step.Seconds > 0 ? $"staying for {step.Seconds:0} s" : "staying until its condition holds";
                return StepResult.Busy;
            }

            case StepKind.Recharge:
            {
                if (craft.LegacyFreePower || craft.Stats.EnergyCapacityKJ <= 0 || craft.StoredEnergyKJ >= step.Percent / 100 * craft.Stats.EnergyCapacityKJ - 1e-6) return StepResult.Done;
                if (StepStation(craft, step, out words) is not { } station) return Hold(craft);
                if (GetDocked(craft, station, out words) is { } going)
                {
                    job.BegunTick = Tick; // the time is for charging, not for getting there
                    return going;
                }
                craft.Controls = ShipControls.Idle;
                if (step.Seconds > 0 && StepSeconds(job) >= step.Seconds) return StepResult.Done;
                words = $"recharging at {station.Name} ({100 * craft.StoredEnergyKJ / craft.Stats.EnergyCapacityKJ:0}% of {step.Percent:0}%)";
                return StepResult.Busy;
            }

            case StepKind.Repair:
            {
                if (!NeedsRepair(craft) || job.RepairAsked) return StepResult.Done; // mended, or as far as that yard could
                var yard = step.StationId != 0 ? _craftsById.GetValueOrDefault(new CraftId(step.StationId)) : NearestShipyard(craft);
                if (yard is not { IsShipyard: true } || !IsPlayers(yard) || yard.IsWreck || yard.Stats.DockingPorts.Count == 0) { words = step.StationId == 0 ? "no shipyard to go to" : "the shipyard chosen isn't there"; return Hold(craft); }
                job.RepairAsked = true;
                craft.ServiceAt = yard.Id; // from here as for any damaged ship (#3)
                words = $"going to {yard.Name} for repairs";
                return StepResult.Busy;
            }

            default:
                return StepResult.Done;
        }
    }

    private static ShipControls Still(CraftState craft) => craft.IsDocked || craft.IsLanded ? ShipControls.Idle : new ShipControls(0, 0, true);

    private static StepResult Hold(CraftState craft)
    {
        craft.Controls = Still(craft);
        return StepResult.Blocked;
    }

    private StepResult Leave(CraftState craft, ProgramJob job)
    {
        TryUndock(craft.Id);
        craft.Controls = new ShipControls(0, 0, true);
        job.RetryAtTick = Tick + (long)(JobStationRetryS * FixedStepClock.TicksPerSecond);
        return StepResult.Blocked;
    }

    /// <summary>Flies to a body, near enough to survey it.</summary>
    private StepResult FlyNear(CraftState craft, ProgramStep step, out string words)
    {
        if (!_byId.TryGetValue(new BodyId(step.BodyId), out var body)) { words = "the body chosen is gone"; return Hold(craft); }
        words = $"flying to {body.Name}";
        var centre = GetPosition(body.Id);
        var gap = craft.Position.DistanceTo(centre) - body.RadiusM;
        var slow = (craft.Velocity - GetVelocityAt(body.Id, TimeS)).Length < 20;
        if (craft.IsLanded && craft.LandedOnBodyId == body.Id) return StepResult.Done;
        if (!craft.IsDocked && !craft.IsLanded && gap < SurveyRadius(body, craft) - body.RadiusM + 100 && slow) { craft.Controls = Still(craft); return StepResult.Done; }
        if (craft.IsDocked) { TryUndock(craft.Id); craft.Controls = ShipControls.Idle; return StepResult.Busy; }
        if (craft.IsLanded)
        {
            if (craft.IsAnchored) TryRelease(craft.Id);
            var outward = craft.Position - GetPosition(craft.LandedOnBodyId);
            craft.Controls = Autopilot.ThrustAlong(craft, outward * (1 / Math.Max(outward.Length, 1e-9)));
            return StepResult.Busy;
        }
        SetTarget(craft.Id, body.Id);
        var bearing = craft.Position - centre;
        craft.Controls = NavigateTo(craft, centre + bearing * (SurveyRadius(body, craft) / Math.Max(bearing.Length, 1e-9)), GetVelocityAt(body.Id, TimeS), BodyId.None, JobCruiseSpeedMps, 0);
        return StepResult.Busy;
    }

    private StepResult DoMine(CraftState craft, ProgramJob job, ProgramStep step, out string words)
    {
        words = "";
        if (job.Mining is null)
        {
            var share = Math.Clamp(step.Percent / 100, 0.01, 1);
            if (craft.Stats.CargoCapacityKg > 0 && (craft.FreeCargoKg <= 0 || craft.Inventory.TotalKg >= share * craft.CargoCapacityKg)) { craft.Controls = Still(craft); return StepResult.Done; }
            if (MineProblem(craft, step) is { } problem) { words = problem; return Hold(craft); }
            var source = new BodyId(step.BodyId);
            job.Mining = new MiningJob(source, step.Material, craft.Id, share) { State = craft.IsLanded && craft.LandedOnBodyId == source ? MiningJobState.Landing : MiningJobState.ToSource };
        }
        var mining = job.Mining;
        StepJob(craft, mining, allowResume: true);
        if (mining.State is MiningJobState.ToDestination or MiningJobState.Unloading)
        {
            job.Mining = null;
            craft.Controls = Still(craft);
            return StepResult.Done;
        }
        if (mining.State == MiningJobState.Waiting)
        {
            words = mining.WaitReason ?? "waiting to mine";
            return StepResult.Blocked;
        }
        words = $"{mining.Status} ({step.Material})";
        return StepResult.Busy;
    }

    private string? MineProblem(CraftState craft, ProgramStep step)
    {
        if (step.Material.Length == 0) return "no material is chosen";
        if (!_byId.TryGetValue(new BodyId(step.BodyId), out var source)) return step.BodyId == 0 ? "no body is chosen" : "the body chosen is gone";
        if (!source.IsMineable) return $"{source.Name} can't be mined";
        if (step.Material == BiomassMaterial ? craft.Stats.HarvestRateKgPerS <= 0 : craft.Stats.MiningRateKgPerS <= 0) return $"{craft.Name} has no {(step.Material == BiomassMaterial ? "harvester" : "drill")}";
        if (craft.Stats.CargoCapacityKg <= 0) return $"{craft.Name} has no cargo bay";
        if (craft.Stats.AnchorClampCount <= 0) return $"{craft.Name} has no anchor clamp (needed to mine)";
        if (!source.AnySiteSurveyed) return $"{source.Name} hasn't been surveyed";
        return LawForbidsMining(source);
    }

    /// <summary>The leg a program's step is on, if it's going somewhere.</summary>
    private (CraftState? Station, CelestialBodyState? Body) ProgramLeg(CraftState craft, ProgramJob job)
    {
        if (!job.Begun || job.Holding || job.WaitReason is not null || !_programs.TryGetValue(job.ProgramId, out var program) || program.PlaceOf(job.StepId) is not (>= 0 and var at) || program.Ops[at].Step is not { } step) return default;
        if (step.Kind == StepKind.FlyTo && step.BodyId != 0) return (null, _byId.GetValueOrDefault(new BodyId(step.BodyId)));
        if (step.Kind is not (StepKind.FlyTo or StepKind.Dock or StepKind.Load or StepKind.Unload or StepKind.Refuel or StepKind.Recharge) || step.StationId == 0) return default;
        return _craftsById.TryGetValue(new CraftId(step.StationId), out var station) && craft.DockedToCraftId != station.Id && !station.IsWreck && IsPlayers(station) && station.Id != craft.Id ? (station, null) : default;
    }

    // --- Checking ---

    /// <summary>A dry run: which steps a ship would go through as things are now, and what's wrong with any of them. Changes nothing.</summary>
    public IReadOnlyList<StepCheck> CheckProgram(CraftState craft, IReadOnlyList<ProgramStep> steps)
    {
        var found = new List<StepCheck>();
        Walk(steps, reached: true);
        return found;

        void Walk(IReadOnlyList<ProgramStep> list, bool reached)
        {
            foreach (var step in list)
            {
                var wrong = Wrong(craft, step);
                if (step.Kind == StepKind.If)
                {
                    var yes = step.Until is { } test && Holds(test, craft, 0);
                    found.Add(new(step.Id, reached, wrong ?? (yes ? "yes, now" : "no, now"), wrong is not null));
                    Walk(step.Then, reached && yes);
                    Walk(step.Otherwise, reached && !yes);
                    continue;
                }
                var passed = step.OnlyIf is { } only && !Holds(only, craft, 0);
                found.Add(new(step.Id, reached && !passed, wrong ?? (passed ? "passed over, now" : "would be done"), wrong is not null));
            }
        }
    }

    private string? Wrong(CraftState craft, ProgramStep step)
    {
        foreach (var test in (step.OnlyIf?.Tests ?? new()).Concat(step.Until?.Tests ?? new()))
        {
            if (test.Kind is TestKind.StationStockTonnes or TestKind.StationFreePorts && !_craftsById.ContainsKey(new CraftId(test.StationId))) return "a test names a station that isn't there";
            if (test.Kind is TestKind.StationStockTonnes or TestKind.CargoTonnes && test.Material.Length == 0) return "a test names no material";
        }
        switch (step.Kind)
        {
            case StepKind.Mine: return MineProblem(craft, step);
            case StepKind.FlyTo when step.BodyId != 0: return _byId.ContainsKey(new BodyId(step.BodyId)) ? null : "the body chosen is gone";
            case StepKind.FlyTo or StepKind.Dock or StepKind.Load or StepKind.Unload or StepKind.Refuel or StepKind.Recharge:
                if (step.StationId == 0) { if (step.Kind is StepKind.FlyTo or StepKind.Dock) return "no station is chosen"; }
                else if (StepStation(craft, step, out var why) is null) return why;
                if (step.Kind == StepKind.Load && step.Material.Length == 0) return "no material is chosen";
                return step.Kind is StepKind.Load or StepKind.Unload or StepKind.Refuel && craft.Stats.CargoCapacityKg <= 0 ? $"{craft.Name} has no cargo bay" : null;
            case StepKind.Wait: return step.Seconds <= 0 && step.Until is null ? "it waits for nothing" : null;
            case StepKind.Message: return step.Text.Length == 0 ? "it has no words" : null;
            default: return null;
        }
    }

    /// <summary>A step in words.</summary>
    public string Describe(ProgramStep step)
    {
        string Station() => step.StationId == 0 ? "where it's docked" : _craftsById.TryGetValue(new CraftId(step.StationId), out var s) ? s.Name : "(gone)";
        string Material() => step.Material.Length == 0 ? "..." : Word(step.Material);
        var text = step.Kind switch
        {
            StepKind.FlyTo => $"Fly to {(step.BodyId != 0 ? _byId.TryGetValue(new BodyId(step.BodyId), out var b) ? b.Name : "(gone)" : Station())}",
            StepKind.Dock => $"Dock at {Station()}",
            StepKind.Load => $"Load {Material()} at {Station()}, {(step.Tonnes > 0 ? $"up to {step.Tonnes:0.##} t aboard" : "as much as fits")}{(step.UntilDone ? ", until it's there" : "")}",
            StepKind.Unload => $"Unload {(step.Material.Length == 0 ? "everything but fuel" : Word(step.Material))} at {Station()}{(step.Tonnes > 0 ? $", keeping {step.Tonnes:0.##} t" : "")}{(step.UntilDone ? ", until it's gone" : "")}",
            StepKind.Mine => $"Mine {Material()} at {(_byId.TryGetValue(new BodyId(step.BodyId), out var source) ? source.Name : "...")} until {step.Percent:0}% full",
            StepKind.Wait => "Wait" + (step.Seconds > 0 ? $" {step.Seconds:0} s" : "") + (step.Until is { } u ? (step.Seconds > 0 ? " or" : "") + $" until {Describe(u)}" : ""),
            StepKind.Recharge => $"Recharge at {Station()} to {step.Percent:0}%{(step.Seconds > 0 ? $", {step.Seconds:0} s at most" : "")}",
            StepKind.Refuel => $"Refuel at {Station()}{(step.Tonnes > 0 ? $" to {step.Tonnes:0.##} t" : "")}",
            StepKind.Repair => $"Repair at {(step.StationId == 0 ? "the nearest shipyard" : Station())}",
            StepKind.Message => $"Say \"{step.Text}\"",
            _ => $"If {(step.Until is { } test ? Describe(test) : "...")}",
        };
        return text + (step.OnlyIf is { } only ? $"  [only if {Describe(only)}]" : "");
    }

    private string Word(string material) => Parts is { } p && p.Materials.Contains(material) ? p.Materials.Get(material).Name.ToLowerInvariant() : material;

    public string Describe(ProgramCondition condition) => string.Join(condition.Any ? " or " : " and ", condition.Tests.Select(Describe));

    public string Describe(ProgramTest t)
    {
        var station = t.StationId != 0 && _craftsById.TryGetValue(new CraftId(t.StationId), out var s) ? s.Name : "(no station)";
        var (what, unit) = t.Kind switch
        {
            TestKind.CargoPercent => ("the hold is", "% full"),
            TestKind.CargoTonnes => ($"{Word(t.Material)} aboard is", " t"),
            TestKind.PowerPercent => ("the battery is", "%"),
            TestKind.FuelTonnes => ("fuel is", " t"),
            TestKind.HealthPercent => ("health is", "%"),
            TestKind.StepMinutes => ("this step has taken", " min"),
            TestKind.StationStockTonnes => ($"{Word(t.Material)} at {station} is", " t"),
            _ => ($"free ports at {station} are", ""),
        };
        return $"{what} {(t.Below ? "below" : "at least")} {t.Value:0.##}{unit}";
    }
}
