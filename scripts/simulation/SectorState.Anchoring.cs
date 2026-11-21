namespace SpaceAutomationSandbox.Simulation;

/// <summary>Outcome of a player action (anchor, dock, transfer…): whether it happened, and a message saying why (not).</summary>
public readonly record struct ActionResult(bool Done, string Message);

/// <summary>
/// Anchoring: a landed craft with an anchor clamp can lock itself to the body it rests on and
/// release again (mobile structures). A craft with a foundation anchors itself when it lands and
/// can never release (fixed structures). While anchored, thrust can't lift the craft off.
/// Anchoring moves nothing and creates nothing; it only changes what the craft is allowed to do.
/// </summary>
public sealed partial class SectorState
{
    public ActionResult TryAnchor(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (craft.IsAnchored) return new(false, $"{craft.Name} is already anchored.");
        if (!craft.IsLanded) return new(false, $"{craft.Name} must be landed on a body to anchor.");
        if (!craft.Stats.CanAnchor) return new(false, $"{craft.Name} has no anchor clamp.");
        craft.IsAnchored = true;
        return new(true, $"{craft.Name} anchored to {GetBody(craft.LandedOnBodyId).Name}.");
    }

    public ActionResult TryRelease(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (!craft.IsAnchored) return new(false, $"{craft.Name} is not anchored.");
        if (craft.Stats.IsFixedStructure) return new(false, $"{craft.Name} is permanently anchored by its foundation.");
        craft.IsAnchored = false;
        return new(true, $"{craft.Name} released; it can lift off again.");
    }

    /// <summary>Restores a saved anchored flag, refusing combinations that the rules can't produce.</summary>
    public void RestoreAnchored(CraftId craftId, bool anchored)
    {
        var craft = GetCraft(craftId);
        if (anchored && !craft.IsLanded) throw new System.InvalidOperationException($"{craft.Id} is anchored but not landed.");
        if (anchored && !craft.Stats.CanAnchor) throw new System.InvalidOperationException($"{craft.Id} is anchored without a clamp or foundation.");
        if (!anchored && craft.IsLanded && craft.Stats.IsFixedStructure)
            throw new System.InvalidOperationException($"{craft.Id} has a foundation and is landed, so it must be anchored.");
        craft.IsAnchored = anchored;
    }

    /// <summary>Called whenever a craft comes to rest on a body: foundations lock immediately.</summary>
    private static void OnLanded(CraftState craft)
    {
        if (craft.Stats.IsFixedStructure) craft.IsAnchored = true;
    }
}
