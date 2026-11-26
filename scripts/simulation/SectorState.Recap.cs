using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>One line of the recap: an icon name, what to look at, whether it's urgent, and the craft or body it's about.</summary>
public sealed record RecapLine(string Icon, string Text, bool Urgent = false, TargetRef Target = default);

/// <summary>
/// Where you left off (#146): shown when a save is loaded, the things that want your attention, most urgent first. The game doesn't run
/// while it's closed, so this is the state the save was left in: craft in trouble or waiting and why, research you can buy, contracts on
/// offer, and the last dangers reported.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>The recap, at most <paramref name="max"/> lines; empty when nothing needs you.</summary>
    public IReadOnlyList<RecapLine> Recap(int max = 12)
    {
        var lines = new List<RecapLine>();
        var yours = _crafts.Where(c => IsPlayers(c) && !c.IsWreck).OrderBy(c => c.Id.Value).ToList();

        var defunct = DefunctCrafts().Count;
        if (defunct > 0) lines.Add(new("salvage", $"{defunct} defunct craft: Clean up in the Fleet panel marks them as wrecks for salvagers.", true));
        foreach (var c in yours.Where(c => c.CrewShortReason is not null).Take(3))
            lines.Add(new("crew", $"{c.Name}: {c.CrewShortReason}", true, TargetRef.Of(c)));
        foreach (var c in yours.Where(c => c.PowerShort).Take(3))
            lines.Add(new("power", $"{c.Name} is short of power.", true, TargetRef.Of(c)));
        var hurt = yours.Where(c => NeedsService(c) && !c.SeekRepair && c.ServiceAt.IsNone && !c.IsShipyard).ToList();
        foreach (var c in hurt.Take(3))
            lines.Add(new("danger", $"{c.Name} is badly damaged: Seek repair sends it to a shipyard.", true, TargetRef.Of(c)));
        if (hurt.Count > 3) lines.Add(new("danger", $"{hurt.Count - 3} more craft badly damaged.", true));

        var waiting = yours.Where(c => c.Job is { Active: true } job && job.Status.StartsWith("waiting")).ToList();
        foreach (var c in waiting.Take(4)) lines.Add(new("wait", $"{c.Name}: {c.Job!.Status}", false, TargetRef.Of(c)));
        if (waiting.Count > 4) lines.Add(new("wait", $"{waiting.Count - 4} more craft waiting (the Fleet panel's waiting filter lists them)."));

        if (!ResearchAllUnlocked)
        {
            var affordable = Tech.Techs.Where(t => ResearchBlocker(t.Id) is null).OrderBy(t => t.Cost).ToList();
            if (affordable.Count > 0)
                lines.Add(new("research", $"You can research {affordable[0].Name}" + (affordable.Count > 1 ? $" and {affordable.Count - 1} more" : "") + $" ({ResearchPoints:0} points)."));
        }
        var offers = _contracts.Count(c => c.State == ContractState.Offered);
        if (offers > 0) lines.Add(new("contract", offers == 1 ? "A contract is on offer." : $"{offers} contracts are on offer."));
        var accepted = _contracts.Count(c => c.State == ContractState.Accepted);
        if (accepted > 0) lines.Add(new("contract", accepted == 1 ? "You have a contract under way." : $"You have {accepted} contracts under way."));

        var recent = Tick - 10 * 60 * FixedStepClock.TicksPerSecond; // the last ten minutes of play
        foreach (var e in _events.Where(e => e.Kind == EventKind.Danger && e.Tick >= recent).Reverse().Take(3))
            lines.Add(new("warn", e.Text, false, e.Target));

        return lines.OrderBy(l => l.Urgent ? 0 : 1).Take(max).ToList();
    }
}
