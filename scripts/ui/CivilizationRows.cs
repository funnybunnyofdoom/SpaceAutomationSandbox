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

    /// <summary>A civilization's portrait, in its race's colour (#118); a plain one if its race isn't there.</summary>
    public static Godot.Control Portrait(Faction f, int width = 64)
    {
        var race = f.Race ?? new Construction.RaceDefinition { Id = "none" };
        var frame = new Godot.PanelContainer { SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkBegin, TooltipText = f.Race is null ? "" : $"{race.Name}: {race.Description}" };
        var style = new Godot.StyleBoxFlat { BgColor = new Godot.Color(0, 0, 0, 0.5f), BorderColor = Accent(f) };
        style.SetBorderWidthAll(2);
        style.SetContentMarginAll(2);
        frame.AddThemeStyleboxOverride("panel", style);
        frame.AddChild(new Godot.TextureRect
        {
            Texture = RaceLibrary.Portrait(race), CustomMinimumSize = new Godot.Vector2(width, width * 5 / 4), ExpandMode = Godot.TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = Godot.TextureRect.StretchModeEnum.KeepAspectCovered,
            TextureFilter = Godot.CanvasItem.TextureFilterEnum.LinearWithMipmaps, MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
        });
        return frame;
    }

    public static Godot.Color Accent(Faction f) =>
        f.Race is { Accent.Length: > 0 } race && Godot.Color.HtmlIsValid(race.Accent) ? new Godot.Color(race.Accent) : new Godot.Color(0.7f, 0.75f, 0.8f);

    public static List<HudRow> For(SectorState sector, Faction f)
    {
        var target = f.TargetAttitude(sector.ActiveLaws);
        var rows = new List<HudRow>
        {
            new(f.Disposition == Disposition.Hostile ? "danger" : f.Disposition == Disposition.Wary ? "warn" : f.Disposition == Disposition.Friendly ? "ok" : "idle",
                $"{f.Disposition} {f.Attitude:+0;-0;0}, heading to {target:+0;-0;0}", Of(f.Disposition), (f.Attitude + 100) / 200, (System.Math.Clamp(target, -100, 100) + 100) / 200,
                "Attitude from -100 to +100; the mark is where it's heading"),
        };
        if (f.Race is { } race) rows.Add(new("crew", $"{race.Name}" + (f.RaceView == 0 ? "" : f.RaceId == sector.Race.Id ? $": your own race, {f.RaceView:+0;-0}" : $": sees the {sector.Race.Name} {f.RaceView:+0;-0}"), f.RaceView < 0 ? Severity.Warning : f.RaceView > 0 ? Severity.Good : Severity.Muted, Tip: race.Description));
        if (f.Goodwill > 0) rows.Add(new("trade", $"goodwill {f.Goodwill:0.0} of {Faction.MaxGoodwill:0}", Severity.Good, f.Goodwill / Faction.MaxGoodwill, Tip: "From trade and gifts"));
        if (f.Grievance >= 1) rows.Add(new("mining", $"grievance {f.Grievance:0}", Severity.Warning, f.Grievance / 100, Tip: "From mining in their system; fades with time. Full bar: 100"));
        if (sector.Tick < f.TruceUntilTick) rows.Add(new("clock", $"truce for {(f.TruceUntilTick - sector.Tick) * FixedStepClock.TickSeconds / 60:0} more min"));
        return rows;
    }
}
