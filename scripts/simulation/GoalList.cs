using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>One goal of the new-player checklist (#65). <see cref="Hint"/> may name actions as
/// <c>[ActionName]</c> (an <c>InputActions</c> constant name); the interface swaps in the current key.</summary>
public sealed record GoalStep(string Id, string Title, string Hint);

/// <summary>
/// The optional goal checklist for new players (#65): the core loop in order. Each goal is checked
/// from the simulation state (never from button clicks) and stays done once met, even if the state
/// changes later. Only the ids of met goals are saved (in the save header).
/// </summary>
public static class GoalList
{
    public static readonly IReadOnlyList<GoalStep> Steps = new GoalStep[]
    {
        new("survey", "Survey an asteroid",
            "Pick a target with [TargetCycle] or [TargetNearest], or open the map with [Map]. Fly within 2 km of an asteroid ([Thrust] thrust, [TurnLeft] / [TurnRight] turn, [Brake] brake) and it is surveyed."),
        new("land", "Land on an asteroid",
            "Hold [Brake] to match its motion, then touch it slower than 6 m/s. [Anchor] anchors you so you don't drift."),
        new("mine", "Mine some ore",
            "Landed with a drill, your ship mines on its own. Watch the cargo grow in the Ship area; [Cargo] shows the hold."),
        new("refine", "Refine ore at a station",
            "Fly back and dock at the shipyard with [Dock]. In the cargo panel ([Cargo]) move the ore into the station; its refinery starts by itself."),
        new("design", "Design a ship",
            "Open the blueprint editor with [Editor]. Place parts on the grid (a control part, thrusters at the back) and save it with Ctrl+S."),
        new("build", "Build a second ship",
            "Open the shipyard with [Shipyard], pick your design and press Order. The yard needs the materials in stock."),
        new("automate", "Automate a ship",
            "Open the job panel with [Job] and start a mining or freight job. The ship then works on its own; [Fleet] shows all your ships."),
    };

    /// <summary>
    /// The goals met right now. <paramref name="savedDesigns"/> is how many designs the player has saved
    /// (the blueprint library lives outside the simulation).
    /// </summary>
    public static HashSet<string> MetNow(SectorState sector, CraftId playerId, int savedDesigns)
    {
        var met = new HashSet<string>();
        var player = playerId.IsNone ? null : sector.Crafts.FirstOrDefault(c => c.Id == playerId);
        var ores = sector.Bodies.SelectMany(b => b.DepositsKg.Keys).ToHashSet();
        ores.Add(CelestialBodyState.RockMaterial);
        ores.Add(CelestialBodyState.IceMaterial);

        if (sector.Bodies.Any(b => b.IsMineable && b.Surveyed)) met.Add("survey");
        if (player is { IsLanded: true } && sector.GetBody(player.LandedOnBodyId).IsMineable) met.Add("land");
        if (player != null && ores.Any(ore => player.Inventory[ore] > 0)) met.Add("mine");
        if (sector.Crafts.Any(c => c.RefiningBatch != null)) met.Add("refine");
        if (savedDesigns > 0) met.Add("design");
        if (sector.Crafts.Count(c => c.Stats.TotalThrustN > 0) >= 2) met.Add("build");
        if (sector.Crafts.Any(c => c.IsAutomated)) met.Add("automate");
        return met;
    }

    /// <summary>Adds the goals met now to <paramref name="done"/>; true when any was new.</summary>
    public static bool Update(ISet<string> done, SectorState sector, CraftId playerId, int savedDesigns)
    {
        var added = false;
        foreach (var id in MetNow(sector, playerId, savedDesigns)) added |= done.Add(id);
        return added;
    }

    /// <summary>The first goal not done yet, or null when all are.</summary>
    public static GoalStep? Current(IReadOnlySet<string> done) => Steps.FirstOrDefault(s => !done.Contains(s.Id));
}
