using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>A big thing to reach in the sandbox (#38).</summary>
public sealed record Milestone(string Id, string Title, string Hint, Func<SectorState, bool> Reached);

/// <summary>
/// Milestones (#38; the creator's choice: an endless sandbox, nothing ends the game). Each is reached once, from the simulation's
/// state, and kept with the time it was reached.
/// </summary>
public static class Milestones
{
    private static long Ships(SectorState s) =>
        s.Crafts.Count(c => SectorState.IsPlayers(c) && !c.IsWreck && c.Stats.TotalThrustN > 0) + s.Swarms.Sum(w => (long)w.Count);

    private static bool Any(SectorState s, Func<CraftState, bool> what) => s.Crafts.Any(c => SectorState.IsPlayers(c) && !c.IsWreck && what(c));

    public static readonly IReadOnlyList<Milestone> All = new Milestone[]
    {
        new("automated", "First automated ship", "Give a ship a job.", s => Any(s, c => c.IsAutomated) || s.Swarms.Count > 0),
        new("ships_10", "Ten ships", "Build a fleet.", s => Ships(s) >= 10),
        new("researched", "First technology", "Run a research lab and unlock something.", s => s.UnlockedTech.Count > 0),
        new("new_star", "Another star", "Jump through a warp gate.", s => s.Stars.Count >= 2),
        new("stars_5", "Five star systems", "Open more gates.", s => s.Stars.Count >= 5),
        new("contact", "First contact", "Meet a civilization.", s => s.Factions.Count > 0),
        new("friend", "A friend among the stars", "Make a civilization friendly.", s => s.Factions.Any(f => f.Disposition == Disposition.Friendly)),
        new("trade", "Traders", "Earn goodwill by trade or gifts.", s => s.Factions.Any(f => f.Goodwill > 0)),
        new("victory", "First victory", "Defeat an enemy ship.", s => s.Crafts.Any(c => c.IsWreck && !SectorState.IsPlayers(c))),
        new("life", "Life!", "Find a living world.", s => s.Bodies.Any(b => b.LifeFound)),
        new("thriving", "A thriving world", "Find a living world that thrives, or help one to.", s => s.Bodies.Any(b => b.LifeFound && SectorState.HealthOf(b) == LifeHealth.Thriving)),
        new("swarm_1000", "A swarm of a thousand", "Grow one swarm to 1,000 ships.", s => s.Swarms.Any(w => w.Count >= 1000)),
        new("ships_1000", "A thousand ships", "Ships and swarms together.", s => Ships(s) >= 1000),
        new("giant_ship", "A giant ship", "Fly a ship of 200 parts.", s => Any(s, c => c.Stats.CellCount >= 200 && c.Stats.TotalThrustN > 0)),
        new("fusion", "A star in a bottle", "Build a fusion reactor.", s => Any(s, c => c.Stats.GeneratorList.Any(g => g.PartId == "fusion_reactor"))),
        new("antimatter", "Antimatter", "Hold some in containment.", s => Any(s, c => c.Inventory[SectorState.AntimatterMaterial] > 0)),
        new("quantum", "Quantum jump", "Jump across a system.", s => Any(s, c => c.QuantumReadyTick > 0)),
        new("dismantled", "A world dismantled", "Mine a body away completely.", _ => false), // noted when it happens
    };
}

public sealed partial class SectorState
{
    private readonly SortedDictionary<string, long> _milestones = new(StringComparer.Ordinal);

    /// <summary>Milestones reached, and the tick they were reached at (0: before the game kept track).</summary>
    public IReadOnlyDictionary<string, long> MilestonesReached => _milestones;

    /// <summary>False until the first look: what a loaded older game already has isn't announced.</summary>
    public bool MilestonesStarted { get; internal set; }

    public void RestoreMilestone(string id, long tick) => _milestones[id] = tick;

    internal void ReachMilestone(string id)
    {
        if (_milestones.ContainsKey(id)) return;
        _milestones[id] = MilestonesStarted ? Tick : 0;
        if (MilestonesStarted && Milestones.All.FirstOrDefault(m => m.Id == id) is { } milestone) AddEvent($"Milestone: {milestone.Title}.");
    }

    private void StepMilestones()
    {
        if (Tick % FixedStepClock.TicksPerSecond != 0) return;
        foreach (var milestone in Milestones.All)
            if (!_milestones.ContainsKey(milestone.Id) && milestone.Reached(this)) ReachMilestone(milestone.Id);
        MilestonesStarted = true;
    }
}
