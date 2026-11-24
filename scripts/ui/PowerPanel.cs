using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Power (O, #75): the power network of the craft you fly (everything docked together shares one pool), and
/// your target's if it's a different one. Sources (solar, generators with their fuel and hours left),
/// batteries, what's being used, every machine that draws power, and why something can't run.
/// </summary>
public partial class PowerPanel : PanelContainer
{
    private const double RefreshSeconds = 0.25;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _player;
    private Label _body = null!;
    private double _sinceRefresh;

    public PowerPanel(Func<SectorState> sector, Func<CraftState?> player)
    {
        _sector = sector;
        _player = player;
        Build();
    }

    public PowerPanel() : this(() => new SectorState(0), () => null) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -470;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "POWER", InputActions.Power);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(new Label
        {
            Text = "Craft docked together share one power pool. Solar comes first; generators burn fuel from their own craft's stock "
                   + "only for what's missing (and to top the batteries up).",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0), Modulate = new Color(1, 1, 1, 0.65f),
        });
        _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0) };
        box.AddChild(_body);
    }

    public void Open()
    {
        Visible = true;
        Refresh();
        _sinceRefresh = RefreshSeconds; // and again next frame, once a tick has built the networks
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds) return;
        _sinceRefresh = 0;
        Refresh();
    }

    private static string Kw(double kJPerTick) => $"{kJPerTick / FixedStepClock.TickSeconds:0} kW";

    private static string Hours(double seconds) =>
        double.IsInfinity(seconds) || seconds > 99 * 3600 ? "99+ h" : seconds >= 3600 ? $"{seconds / 3600:0.0} h" : $"{seconds / 60:0} min";

    private void Refresh()
    {
        var sector = _sector();
        var player = _player();
        if (player is null) { _body.Text = "Nothing to show: you aren't flying a craft."; return; }
        var text = new StringBuilder();
        var root = sector.PowerRootOf(player);
        Network(text, sector, root, "Your network");
        if (!player.TargetCraftId.IsNone && sector.Crafts.FirstOrDefault(c => c.Id == player.TargetCraftId) is { } target
            && sector.PowerRootOf(target) != root)
            Network(text, sector, sector.PowerRootOf(target), $"Target: {target.Name}");
        _body.Text = text.ToString().Replace("\r\n", "\n"); // AppendLine writes CRLF on Windows: one break, not two
    }

    private static void Network(StringBuilder text, SectorState sector, CraftState root, string heading)
    {
        var members = sector.PowerMembersOf(root);
        text.AppendLine($"\n{heading}: {string.Join(", ", members.Select(m => m.Name))}");
        if (members.Any(m => m.LegacyFreePower)) { text.AppendLine("   unlimited power (from an old save)"); return; }

        var solar = members.Sum(m => m.SolarKJThisTick + m.GeothermalKJThisTick);
        var generated = members.Sum(m => m.GeneratedKJThisTick);
        var used = root.NetworkUsedKJThisTick;
        var stored = members.Sum(m => m.StoredEnergyKJ);
        var capacity = members.Sum(m => m.Stats.EnergyCapacityKJ);
        text.AppendLine($"   making {Kw(solar + generated)}   using {Kw(used)}"
                        + (used > solar + generated + 1e-6 ? $"   (batteries empty in {Hours(stored / ((used - solar - generated) / FixedStepClock.TickSeconds))})" : ""));
        text.AppendLine($"   batteries {stored / 1000:0.0} / {capacity / 1000:0} MJ ({(capacity > 0 ? stored / capacity * 100 : 0):0}%)");

        text.AppendLine("   Sources");
        foreach (var m in members.Where(m => m.Stats.PowerGenerationKW > 0))
            text.AppendLine($"      {m.Name}: solar {Kw(m.SolarKJThisTick)} of {m.Stats.PowerGenerationKW:0} kW ({m.SolarFactor * 100:0}% sunlight)"
                            + (m.InShadow ? $"  IN SHADOW (light dimmed to {SectorState.ShadowLight * 100:0}%)" : ""));
        foreach (var m in members.Where(m => m.Stats.GeothermalKW > 0)) // #77
            text.AppendLine($"      {m.Name}: geothermal {Kw(m.GeothermalKJThisTick)} of {m.Stats.GeothermalKW:0} kW"
                            + (sector.GeothermalHeat(m) is { } why ? $"   ({why})" : ""));
        foreach (var m in members)
            foreach (var g in m.Stats.GeneratorList)
            {
                var batches = g.BurnsKg.Min(b => m.Inventory[b.Key] / Math.Max(1, b.Value));
                var energyLeft = batches * g.KJPerBatch + m.GeneratorBankKJ.GetValueOrDefault(g.PartId);
                var fuel = string.Join(", ", g.BurnsKg.Keys.Select(k => $"{m.Inventory[k] / 1000.0:0.##} t {k}"));
                text.AppendLine($"      {m.Name}: {g.PartId.Replace('_', ' ')} {Kw(m.GeneratedKJThisTick)} of {g.KW:0} kW; "
                                + (energyLeft <= 0 ? $"NO FUEL (burns {string.Join(" + ", g.BurnsKg.Select(b => $"{b.Value} kg {b.Key}"))})"
                                    : $"{fuel} left, {Hours(energyLeft / g.KW)} at full output"));
            }
        if (members.All(m => m.Stats.PowerGenerationKW <= 0 && m.Stats.GeneratorList.Count == 0 && m.Stats.GeothermalKW <= 0)) text.AppendLine("      none: only batteries");

        foreach (var m in members.Where(m => m.Stats.ConverterList.Count > 0)) // #78
            foreach (var c in m.Stats.ConverterList)
                text.AppendLine($"   Converter: {m.Name}: {c.PartId.Replace('_', ' ')} {Kw(m.ConvertedKJThisTick)} of {c.KW:0} kW "
                                + $"({string.Join(" + ", c.InputsKg.Select(i => $"{i.Value} kg {i.Key}"))} into {string.Join(" + ", c.OutputsKg.Select(o => $"{o.Value} kg {o.Key}"))}; "
                                + $"spare power only, above {SectorState.ConverterSpareAbove * 100:0}% battery)");

        text.AppendLine("   Users (most they draw)");
        foreach (var m in members)
        {
            var s = m.Stats;
            var users = new List<string>();
            void U(string name, double kw) { if (kw > 0) users.Add($"{name} {kw:0}"); }
            U("thrusters", s.ThrustPowerKW + s.ReversePowerKW + s.LeftPowerKW + s.RightPowerKW);
            U("drills", s.DrillPowerKW); U("refinery", s.RefineryPowerKW); U("fabricator", s.FabricatorPowerKW);
            U("ejector", s.EjectorPowerKW); U("habitats", s.HabitatPowerKW); U("farms", s.FarmPowerKW);
            U("robots", s.RobotPowerKW); U("labs", s.LabPowerKW);
            if (users.Count == 0) continue;
            text.AppendLine($"      {m.Name}: {string.Join(", ", users)} kW" + (m.PowerShort ? "   ! SHORT OF POWER: something couldn't run" : ""));
        }
    }
}
