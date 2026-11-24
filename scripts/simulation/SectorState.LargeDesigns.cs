using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Large-ship building (#111; the creator's choice: hidden until Fabricator Mk3): opens for good once a craft of yours with a part
/// that opens it has been working. The editor reads <see cref="LargeDesigns"/>; the simulation builds any design either way.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>The blueprint editor has its large grid and tools. Saved; never goes back.</summary>
    public bool LargeDesigns { get; internal set; }

    private void StepLargeDesigns()
    {
        if (LargeDesigns || Tick % FixedStepClock.TicksPerSecond != 0 || Parts is null) return;
        foreach (var craft in _crafts)
        {
            if (!craft.IsShipyard || craft.IsWreck || !IsPlayers(craft) || !Operational(craft)) continue;
            if (!craft.Design.CellArray.Any(c => Parts.Get(c.PartId).OpensLargeDesigns)) continue;
            LargeDesigns = true;
            AddEvent($"{craft.Name}'s Fabricator Mk3 is at work: the blueprint editor now has a 101 x 101 grid and tools for large ships.", EventKind.Fleet, TargetRef.Of(craft));
            return;
        }
    }
}
