using System;
using SpaceAutomationSandbox.Construction;

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
        if (GetBody(craft.LandedOnBodyId).Kind is BodyKind.GasGiant or BodyKind.Star)
            return new(false, $"{GetBody(craft.LandedOnBodyId).Name} is a {(GetBody(craft.LandedOnBodyId).Kind == BodyKind.Star ? "star" : "gas giant")}: there's nothing solid to anchor to.");
        if (!AnchorTouchesGround(craft))
            return new(false, $"{craft.Name}'s anchor clamp isn't touching {GetBody(craft.LandedOnBodyId).Name}: land on the clamp side.");
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

    /// <summary>
    /// #42: an anchor part (clamp or foundation) must be among the craft's lowest cells, i.e. within
    /// one cell of the cell closest to the body's centre. A ship resting on its nose can't anchor.
    /// </summary>
    public bool AnchorTouchesGround(CraftState craft)
    {
        if (!craft.IsLanded) return false;
        var centre = GetPosition(craft.LandedOnBodyId);
        double lowest = double.PositiveInfinity, lowestAnchor = double.PositiveInfinity;
        foreach (var (cell, position) in CellPositions(craft, craft.Position, craft.HeadingRad))
        {
            var distance = position.DistanceTo(centre);
            lowest = Math.Min(lowest, distance);
            if (Catalog(cell.PartId) is { } part && (part.IsAnchorClamp || part.IsFoundation)) lowestAnchor = Math.Min(lowestAnchor, distance);
        }
        return lowestAnchor <= lowest + Construction.ShipDesign.CellSizeM / 2;
    }

    /// <summary>
    /// Heading that points the craft's first anchor part (clamp or foundation) straight at the body below,
    /// for landing on it. Null when the craft has none.
    /// </summary>
    public double? AnchorDownHeading(CraftState craft, Vector2D bodyCentre)
    {
        foreach (var cell in craft.Design.Cells)
        {
            if (Catalog(cell.PartId) is not { } part || !(part.IsAnchorClamp || part.IsFoundation)) continue;
            var localX = cell.Pos.X - craft.Stats.CenterOfMassX;
            var localY = cell.Pos.Y - craft.Stats.CenterOfMassY;
            if (Math.Abs(localX) < 1e-9 && Math.Abs(localY) < 1e-9) continue; // at the centre: any heading works
            var down = bodyCentre - craft.Position;
            // Rotating (localX, localY) by heading h must give a vector pointing along `down`.
            return FlightModel.WrapAngle(Math.Atan2(down.Y, down.X) - Math.Atan2(localY, localX));
        }
        return null;
    }

    /// <summary>Called whenever a craft comes to rest on a body: foundations lock immediately.</summary>
    private static void OnLanded(CraftState craft)
    {
        if (craft.Stats.IsFixedStructure) craft.IsAnchored = true;
    }
}
