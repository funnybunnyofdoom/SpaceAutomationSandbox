using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Moving materials between a docked ship and its station. Whole kg, all-or-nothing, and only
/// across a dock. Ship cargo is limited by its cargo bays; stations have no limit yet (storage
/// parts come later). Nothing is created or lost: what leaves one inventory arrives in the other.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Moves <paramref name="kg"/> of a material from one craft to another, if they are docked together.</summary>
    public ActionResult TryTransfer(CraftId fromId, CraftId toId, string materialId, long kg)
    {
        var from = GetCraft(fromId);
        var to = GetCraft(toId);
        if (kg <= 0) return new(false, "Nothing to move.");
        if (from.DockedToCraftId != to.Id && to.DockedToCraftId != from.Id)
            return new(false, $"{from.Name} and {to.Name} must be docked together to move cargo.");
        if (from.Inventory[materialId] < kg)
            return new(false, $"{from.Name} has only {from.Inventory[materialId]} kg of {materialId}.");
        if (to.FreeCargoKg < kg)
            return new(false, $"{to.Name} has room for only {to.FreeCargoKg} kg more.");

        from.Inventory.TryRemove(new Dictionary<string, long> { [materialId] = kg });
        to.Inventory.Add(materialId, kg);
        return new(true, $"Moved {kg} kg of {materialId} from {from.Name} to {to.Name}.");
    }

    /// <summary>The most of a material that can move right now (stock on one side, room on the other).</summary>
    public long MaxTransferKg(CraftId fromId, CraftId toId, string materialId) =>
        Math.Min(GetCraft(fromId).Inventory[materialId], GetCraft(toId).FreeCargoKg);
}
