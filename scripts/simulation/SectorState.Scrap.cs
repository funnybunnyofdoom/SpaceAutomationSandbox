using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Marking your own craft as a wreck (#135; the creator's choices: its crew go home, its cargo is part of the salvage, and once a craft is
/// defunct it's easy to mark and have it cleaned up). It becomes a wreck like any other: salvage arms and salvage jobs take it apart and
/// bring its materials home. Until salvage begins on it, it can be taken back.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Why a craft can't be marked as a wreck now, or null.</summary>
    public string? ScrapBlocker(CraftState craft)
    {
        if (!IsPlayers(craft)) return $"{craft.Name} isn't yours.";
        if (craft.IsWreck) return $"{craft.Name} is a wreck already.";
        if (craft.Id == PilotCraftId) return "You're flying it: fly another craft first.";
        if (!craft.LegacyNoCrew && craft.Crew > 0 && CrewRoomElsewhere(craft) < craft.Crew)
            return $"Its {craft.Crew} crew have nowhere to go: your other craft have room for {CrewRoomElsewhere(craft)}.";
        return null;
    }

    /// <summary>Free habitat room in your other working craft.</summary>
    private int CrewRoomElsewhere(CraftState craft) =>
        _crafts.Where(c => c.Id != craft.Id && IsPlayers(c) && !c.IsWreck && !c.LegacyNoCrew).Sum(c => Math.Max(0, c.Stats.CrewCapacity - c.Crew));

    /// <summary>Marks a craft of yours as a wreck: its crew go to your nearest craft with room, its job stops, what's docked to it lets go.</summary>
    public ActionResult ScrapCraft(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (ScrapBlocker(craft) is { } why) return new(false, why);
        var moved = new List<string>();
        if (!craft.LegacyNoCrew && craft.Crew > 0)
            foreach (var home in _crafts.Where(c => c.Id != craft.Id && IsPlayers(c) && !c.IsWreck && !c.LegacyNoCrew && c.Stats.CrewCapacity > c.Crew)
                         .OrderBy(c => StarAt(c.Position)?.Id == StarAt(craft.Position)?.Id ? 0 : 1).ThenBy(c => c.Position.DistanceTo(craft.Position)).ThenBy(c => c.Id.Value).ToList())
            {
                var go = Math.Min(craft.Crew, home.Stats.CrewCapacity - home.Crew);
                if (go <= 0) continue;
                home.Crew += go;
                craft.Crew -= go;
                moved.Add($"{go} to {home.Name}");
                if (craft.Crew == 0) break;
            }
        if (craft.Job is { Active: true } job) { job.Active = false; job.WaitReason = "stopped: marked as a wreck"; }
        craft.Job = null;
        craft.YardOrder = null;
        craft.YardProgressKg = 0;
        craft.RequestsList.Clear();
        MakeWreck(craft, IsGate(craft), destroyed: false);
        craft.Scrapped = true;
        AddEvent($"{craft.Name} is marked as a wreck" + (moved.Count > 0 ? $"; its crew went {string.Join(", ", moved)}" : "") + ". Salvagers will take it apart.", EventKind.Fleet, TargetRef.Of(craft));
        return new(true, $"{craft.Name} is a wreck now: salvage arms and salvage jobs will take it apart, its cargo too." + (moved.Count > 0 ? $" Crew moved: {string.Join(", ", moved)}." : ""));
    }

    /// <summary>Takes back a craft marked as a wreck, if salvage hasn't begun on it.</summary>
    public ActionResult UnscrapCraft(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (!craft.Scrapped || !craft.IsWreck) return new(false, $"{craft.Name} wasn't marked as a wreck.");
        craft.IsWreck = false;
        craft.Scrapped = false;
        return new(true, $"{craft.Name} is yours to use again (it has no crew aboard).");
    }
}
