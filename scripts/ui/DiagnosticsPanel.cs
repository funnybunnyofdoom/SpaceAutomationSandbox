using System;
using System.Linq;
using System.Text;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Diagnostics (F4, #19), for understanding a large automated network:
/// <list type="bullet">
/// <item>time: requested and actual speed, the cost of recent ticks and how much of real time it uses;</item>
/// <item>jobs: counts by kind and state, and waiting jobs grouped by reason (with who is waiting);</item>
/// <item>open station requests;</item>
/// <item>the mass ledger by holder, with a conservation check against the moment this sector was opened.</item>
/// </list>
/// Read-only. Each job's recent history is in the job panel (J).
/// </summary>
public partial class DiagnosticsPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private const int MaxGroups = 12;
    private const int NamesPerGroup = 4;

    private readonly Func<SectorState> _sector;
    private readonly TimeWarp _warp;
    private readonly TickCostMeter _cost;
    private Label _text = null!;
    private double _sinceRefresh;
    private SectorState? _baselineSector;
    private long _baselineKg;
    private long _baselineCheatKg;
    private long _baselineTick;

    public DiagnosticsPanel(Func<SectorState> sector, TimeWarp warp, TickCostMeter cost)
    {
        _sector = sector;
        _warp = warp;
        _cost = cost;
        Build();
    }

    public DiagnosticsPanel() : this(() => new SectorState(0), new TimeWarp(), new TickCostMeter()) { }

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
        scroll.AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "DIAGNOSTICS", InputActions.Diagnostics);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        _text = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0) };
        box.AddChild(_text);
    }

    public void Open()
    {
        Visible = true;
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var ledger = sector.ComputeMassLedger();
        if (!ReferenceEquals(sector, _baselineSector))
        {
            // A new game or a load: conservation is checked from here on.
            _baselineSector = sector;
            _baselineKg = ledger.TotalKg;
            _baselineCheatKg = sector.DebugAddedKg;
            _baselineTick = sector.Tick;
        }

        var text = new StringBuilder();
        AppendTime(text);
        AppendJobs(text, sector);
        AppendRequests(text, sector);
        AppendImpacts(text, sector);
        AppendLedger(text, sector, ledger);
        _text.Text = text.ToString().Replace("\r\n", "\n"); // AppendLine writes CRLF on Windows; a Label shows \r as an extra line
    }

    private void AppendTime(StringBuilder text)
    {
        var avg = _cost.AverageMs;
        var budget = avg * _warp.ActualSpeed * FixedStepClock.TicksPerSecond / 10; // % of each real second spent simulating
        text.AppendLine("TIME");
        text.AppendLine($"  seed {_sector().Seed}, tick {_sector().Tick} ({FixedStepClock.TicksPerSecond}/s), game time {_sector().TimeS:0.0} s");
        text.AppendLine($"  speed {_warp}" + (_warp.SkippedTicks > 0 ? $", {_warp.SkippedTicks} ticks skipped so far" : ""));
        text.AppendLine($"  tick cost {avg:0.00} ms avg, {_cost.MaxMs:0.00} ms max (last {_cost.Count} ticks)");
        text.AppendLine($"  simulating uses {budget:0}% of real time");
        text.AppendLine($"  craft far away simulated cheaply (in transit or dormant): {_sector().AbstractCraftCount}");
        text.AppendLine();
    }

    private static void AppendJobs(StringBuilder text, SectorState sector)
    {
        var crafts = sector.Crafts;
        var jobs = crafts.Where(c => c.Job is not null).Select(c => c.Job!).ToList();
        var running = jobs.Where(j => j.Active).ToList();
        var waiting = running.Count(j => j.WaitReason is not null);
        text.AppendLine("JOBS");
        text.AppendLine($"  {crafts.Count(c => c.Stats.TotalThrustN > 0)} ships, {crafts.Count(c => c.Stats.TotalThrustN <= 0)} stations");
        text.AppendLine($"  {running.Count} running ({string.Join(", ", running.GroupBy(j => j.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"))}), "
                        + $"{waiting} waiting, {jobs.Count - running.Count} stopped");

        var groups = sector.WaitingJobs();
        if (groups.Count == 0) text.AppendLine("  nobody is waiting");
        foreach (var group in groups.Take(MaxGroups))
        {
            var names = string.Join(", ", group.Crafts.Take(NamesPerGroup).Select(id => sector.GetCraft(id).Name));
            text.AppendLine($"  {group.Crafts.Count}× {group.Reason}");
            text.AppendLine($"      {names}{(group.Crafts.Count > NamesPerGroup ? $" +{group.Crafts.Count - NamesPerGroup} more" : "")}");
        }
        if (groups.Count > MaxGroups) text.AppendLine($"  … and {groups.Count - MaxGroups} more reasons");
        text.AppendLine();
    }

    private static void AppendRequests(StringBuilder text, SectorState sector)
    {
        var requests = sector.Crafts.SelectMany(c => c.Requests.Select(r => (Station: c, Material: r.Key, Want: r.Value))).ToList();
        if (requests.Count == 0) return;
        text.AppendLine("STATION REQUESTS");
        foreach (var (station, material, want) in requests)
        {
            var open = sector.OpenRequestKg(station.Id, material);
            text.AppendLine($"  {station.Name}: {material} keep {want / 1000.0:0.0} t, has {station.Inventory[material] / 1000.0:0.0} t, "
                            + $"coming {sector.IncomingKg(station.Id, material) / 1000.0:0.0} t" + (open > 0 ? $", OPEN {open / 1000.0:0.0} t" : ""));
        }
        text.AppendLine();
    }

    private static void AppendImpacts(StringBuilder text, SectorState sector)
    {
        if (sector.RecentImpacts.Count == 0) return;
        text.AppendLine("RECENT DAMAGING IMPACTS");
        foreach (var e in sector.RecentImpacts.Reverse().Take(8))
        {
            var name = sector.Crafts.FirstOrDefault(c => c.Id == e.Craft)?.Name ?? e.Craft.ToString();
            text.AppendLine($"  {e.Tick * FixedStepClock.TickSeconds / 60:0.0} min: {name} hit {e.With} at {e.SpeedMps:0} m/s");
        }
        text.AppendLine();
    }

    private void AppendLedger(StringBuilder text, SectorState sector, MassLedger ledger)
    {
        static string T(long kg) => $"{kg / 1000.0:#,0.0} t";
        text.AppendLine("MASS LEDGER");
        text.AppendLine($"  bodies (mineable) {T(ledger.DepositsKg)}   stock {T(ledger.StockKg)}");
        text.AppendLine($"  kits {T(ledger.KitsKg)}   build orders {T(ledger.OrdersKg)}   refining {T(ledger.RefiningKg)}");
        text.AppendLine($"  craft (their parts) {T(ledger.CraftKg)}   ejected, burned or debris {T(ledger.EjectedKg)}");
        text.AppendLine($"  total {ledger.TotalKg:#,0} kg");
        var cheats = sector.DebugAddedKg - _baselineCheatKg;
        var drift = ledger.TotalKg - _baselineKg - cheats;
        var since = (sector.Tick - _baselineTick) * FixedStepClock.TickSeconds / 60;
        text.AppendLine(drift == 0
            ? $"  conserved: no change in {since:0.0} min" + (cheats > 0 ? $" (apart from {cheats:#,0} kg of debug cheats)" : "")
            : $"  MISMATCH: {drift:+#,0;-#,0} kg in {since:0.0} min that no cheat explains - please report it");
    }
}
