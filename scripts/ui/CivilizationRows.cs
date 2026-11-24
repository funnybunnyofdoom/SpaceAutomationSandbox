using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>A civilization as rows (#103): attitude with where it's heading, goodwill, grievance and truce.</summary>
public static class CivilizationRows
{
    public static Severity Of(Disposition disposition) => disposition switch
    {
        Disposition.Hostile => Severity.Bad, Disposition.Wary => Severity.Warning, Disposition.Friendly => Severity.Good, _ => Severity.Normal,
    };

    public static List<HudRow> For(SectorState sector, Faction f)
    {
        var target = f.TargetAttitude(sector.ActiveLaws);
        var rows = new List<HudRow>
        {
            new(f.Disposition == Disposition.Hostile ? "danger" : f.Disposition == Disposition.Wary ? "warn" : f.Disposition == Disposition.Friendly ? "ok" : "idle",
                $"{f.Disposition} {f.Attitude:+0;-0;0}, heading to {target:+0;-0;0}", Of(f.Disposition), (f.Attitude + 100) / 200, (System.Math.Clamp(target, -100, 100) + 100) / 200,
                "Attitude from -100 to +100; the mark is where it's heading"),
        };
        if (f.Goodwill > 0) rows.Add(new("trade", $"goodwill {f.Goodwill:0.0} of {Faction.MaxGoodwill:0}", Severity.Good, f.Goodwill / Faction.MaxGoodwill, Tip: "From trade and gifts"));
        if (f.Grievance >= 1) rows.Add(new("mining", $"grievance {f.Grievance:0}", Severity.Warning, f.Grievance / 100, Tip: "From mining in their system; fades with time. Full bar: 100"));
        if (sector.Tick < f.TruceUntilTick) rows.Add(new("clock", $"truce for {(f.TruceUntilTick - sector.Tick) * FixedStepClock.TickSeconds / 60:0} more min"));
        return rows;
    }
}
