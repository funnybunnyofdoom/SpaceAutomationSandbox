using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Seek repair (#148): send a craft of yours to the nearest shipyard now, to be repaired whatever its damage, with or without a job.
/// It flies there, docks, waits (saying why) until every part is mended, undocks and carries on with its job if it has one. It uses
/// the same servicing as automated craft (#3), which on their own only go in once a part is badly hurt.
/// Cleaning up defunct craft in bulk (#144) is here too.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Why a craft can't be sent for repairs now, or null.</summary>
    public string? SeekRepairBlocker(CraftState craft)
    {
        if (!IsPlayers(craft)) return $"{craft.Name} isn't yours.";
        if (craft.IsWreck) return $"{craft.Name} is a wreck.";
        if (craft.Id == PilotCraftId) return "You're flying it: dock it at a shipyard yourself.";
        if (!NeedsRepair(craft)) return $"{craft.Name} has nothing to repair.";
        if (craft.IsShipyard) return $"{craft.Name} is a shipyard: it repairs itself.";
        if (craft.Stats.TotalThrustN <= 0) return $"{craft.Name} can't fly: a tug must bring it to a shipyard.";
        if (craft.SeekRepair) return $"{craft.Name} is on its way for repairs already.";
        return null;
    }

    /// <summary>Sends a craft to the nearest shipyard for repairs (#148).</summary>
    public ActionResult OrderSeekRepair(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (SeekRepairBlocker(craft) is { } why) return new(false, why);
        craft.SeekRepair = true;
        return new(true, $"{craft.Name} is going to the nearest shipyard for repairs" + (craft.Job is { Active: true } ? ", then back to its job." : "."));
    }

    /// <summary>Calls off a repair trip: the craft goes back to what it was doing.</summary>
    public ActionResult CancelSeekRepair(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (!craft.SeekRepair) return new(false, $"{craft.Name} isn't going for repairs.");
        EndSeekRepair(craft);
        return new(true, $"{craft.Name} won't go for repairs now.");
    }

    private void EndSeekRepair(CraftState craft)
    {
        craft.SeekRepair = false;
        craft.SeekRepairStatus = null;
        if (craft.ServiceAt is var yardId && !yardId.IsNone && !NeedsService(craft))
        {
            craft.ServiceAt = CraftId.None;
            if (craft.DockedToCraftId == yardId) TryUndock(craft.Id);
        }
        if (craft.Job is not { Active: true }) craft.Controls = ShipControls.Idle;
    }

    /// <summary>One tick of a repair trip; true when it flew the craft. Called before its job's own servicing.</summary>
    private bool StepSeekRepair(CraftState craft)
    {
        if (craft.Id == PilotCraftId || craft.IsWreck) { EndSeekRepair(craft); return false; }
        if (!NeedsRepair(craft))
        {
            EndSeekRepair(craft);
            AddEvent($"{craft.Name} is repaired" + (craft.Job is { Active: true } ? " and back to its job." : "."), EventKind.Fleet, TargetRef.Of(craft));
            return false;
        }
        var flew = StepService(craft, craft.Job is { Active: true } job ? job : null, forced: true);
        return flew;
    }

    /// <summary>
    /// Your craft that are past saving where they are (#144): broken badly enough to have lost half their parts, or every engine of a
    /// design that had some. Not shipyards, not the one you fly, and only those whose crew have somewhere to go.
    /// </summary>
    public IReadOnlyList<CraftState> DefunctCrafts() =>
        _crafts.Where(c => IsPlayers(c) && !c.IsWreck && !c.IsShipyard && c.Id != PilotCraftId && c.IntactDesign is { } intact
                           && (MissingParts(c) * 2 >= intact.Cells.Count || c.Stats.TotalThrustN <= 0 && intact.ComputeStats(Parts!).TotalThrustN > 0)
                           && ScrapBlocker(c) is null)
            .OrderBy(c => c.Id.Value).ToList();

    /// <summary>Marks every defunct craft as a wreck at once (#144); how many.</summary>
    public ActionResult ScrapDefunct()
    {
        var defunct = DefunctCrafts();
        if (defunct.Count == 0) return new(false, "None of your craft is defunct.");
        var done = defunct.Count(c => ScrapCraft(c.Id).Done);
        return new(done > 0, $"{done} craft marked as wrecks: salvagers will take them apart.");
    }
}
