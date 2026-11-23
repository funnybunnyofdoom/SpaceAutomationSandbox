using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Development tuning: multipliers applied on top of part stats, so pacing can be tried in game
/// without editing data files. Part of the simulation state (saved), so a save replays the same way.
/// Defaults are 1 (no change). Game speed is not here: it only changes how fast ticks are run, not results.
/// </summary>
public sealed class SimulationTuning
{
    public const double Min = 0.01;
    public const double Max = 1_000;

    private double _drill = 1, _refining = 1, _fabrication = 1;

    public double DrillRateMultiplier { get => _drill; set => _drill = Clamp(value); }
    public double RefiningRateMultiplier { get => _refining; set => _refining = Clamp(value); }
    public double FabricationRateMultiplier { get => _fabrication; set => _fabrication = Clamp(value); }

    public bool IsDefault => _drill == 1 && _refining == 1 && _fabrication == 1;

    public void Reset() => _drill = _refining = _fabrication = 1;

    private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, Min, Max) : 1;

    public override string ToString() => $"drill ×{_drill:0.##}, refining ×{_refining:0.##}, fabrication ×{_fabrication:0.##}";
}

/// <summary>
/// Debug tools for development playtesting. The cheats deliberately break the "no mass created"
/// rule, so they are only reachable from the debug menu and are named as such.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Mass added by debug cheats since this sector was created or loaded (not saved), so diagnostics can tell it apart from a leak.</summary>
    public long DebugAddedKg { get; private set; }

    public SimulationTuning Tuning { get; } = new();

    /// <summary>CHEAT: adds material to a craft's inventory out of nothing (ignores capacity).</summary>
    public void DebugAddStock(CraftId craftId, string materialId, long kg)
    {
        if (kg <= 0) throw new ArgumentOutOfRangeException(nameof(kg));
        GetCraft(craftId).Inventory.Add(materialId, kg);
        DebugAddedKg += kg;
    }

    /// <summary>CHEAT: marks every body surveyed.</summary>
    public void DebugSurveyAll()
    {
        foreach (var body in _bodies) body.Surveyed = true;
    }

    /// <summary>DEBUG: records a crater (#28) without removing any mass (only to look at how craters draw).</summary>
    public void DebugDigCrater(BodyId bodyId, double bearingRad, long kg) => GetBody(bodyId).Dig(bearingRad, kg);

    /// <summary>
    /// DEBUG: damages one part as an impact would: sets its health, or breaks it off at 0 (its
    /// materials become debris). For tests and trying out repairs.
    /// </summary>
    public void DebugDamagePart(CraftId craftId, Construction.GridPos pos, double health)
    {
        var craft = GetCraft(craftId);
        if (Parts is null) throw new InvalidOperationException("No part catalog: this sector takes no damage.");
        var cell = craft.Design.Cells.First(c => c.Pos == pos);
        if (health <= 0) { BreakOff(craft, new List<Construction.DesignCell> { cell }); return; }
        if (health > 1) throw new ArgumentOutOfRangeException(nameof(health));
        if (health < 1) craft.HealthList[pos] = health; else craft.HealthList.Remove(pos);
        craft.Stats = craft.Design.ComputeStats(Parts, craft.HealthList);
    }
}
