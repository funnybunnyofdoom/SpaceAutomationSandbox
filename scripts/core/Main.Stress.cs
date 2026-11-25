using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Performance check in the real game (#39), rendering included (tools/Benchmarks times the simulation only).
/// <c>-- --new-game --stress=1000 --fps-report=20</c> adds N automated miners around the starter shipyard
/// (the benchmark's scenario), turns vsync off, waits 5 s, then prints frame times over the given seconds
/// and quits. The target is 1,000 ships at 60 fps (16.7 ms per frame).
/// </summary>
public partial class Main
{
    private List<double>? _frameSamples;

    /// <summary>Per-frame costs on the main thread (ms), reported by the stress check.</summary>
    private readonly TickCostMeter _drawCost = new(), _hudCost = new(), _audioCost = new();
    private double _stressWarmS, _stressReportS;
    private ulong _lastFrameUsec;

    private void SetUpStress(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith("--stress=")) is { } stressArg && int.TryParse(stressArg["--stress=".Length..], out var count)
            && _catalog != null && _starterDesign != null && _player != null)
        {
            _sector.DebugSurveyAll();
            var yard = _sector.Crafts.First(c => c.Stats.FabricationRateKgPerS > 0);
            var home = SectorGenerator.HomePlanet(_sector).Id;
            var rocks = _sector.Bodies.Where(b => b.IsMineable && b.Orbit?.Parent == home).ToList();
            var stats = _starterDesign.ComputeStats(_catalog);
            for (var i = 0; i < count; i++)
            {
                var craft = new CraftState(_sector.AllocateCraftId(), $"M{i}", _starterDesign, stats,
                    yard.Position + new Vector2D(200 + 30 * (i % 100), -300 - 30 * (i / 100)), 0) { Velocity = yard.Velocity };
                _sector.AddCraft(craft);
                var rock = rocks[i % rocks.Count];
                _sector.StartJob(craft.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25));
            }
            GD.Print($"[Stress] {count} automated miners added");
        }
        if (args.FirstOrDefault(a => a.StartsWith("--fps-report=")) is { } reportArg && double.TryParse(reportArg["--fps-report=".Length..], out var seconds))
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            _frameSamples = new List<double>();
            _stressWarmS = 5;
            _stressReportS = seconds;
        }
    }

    private void StressFrame(double delta)
    {
        if (_frameSamples is null) return;
        // Real time between frames: Godot smooths delta, which would hide spikes.
        var now = Time.GetTicksUsec();
        var frameMs = _lastFrameUsec == 0 ? delta * 1000 : (now - _lastFrameUsec) / 1000.0;
        _lastFrameUsec = now;
        if (_stressWarmS > 0) { _stressWarmS -= frameMs / 1000; return; }
        _frameSamples.Add(frameMs);
        _stressReportS -= frameMs / 1000;
        if (_stressReportS > 0) return;
        var sorted = _frameSamples.OrderByDescending(s => s).ToList();
        var worst = sorted.Take(System.Math.Max(1, sorted.Count / 100)).Average();
        var mean = _frameSamples.Average();
        GD.Print($"[FPS] crafts={_sector.Crafts.Count} frames={_frameSamples.Count} mean={mean:0.00} ms ({1000 / mean:0} fps) worst1%={worst:0.00} ms ({1000 / worst:0} fps) tick={_tickCost.AverageMs:0.00} ms avg, {_tickCost.MaxMs:0.00} ms max; per frame: draw {_drawCost.AverageMs:0.00} ms, hud {_hudCost.AverageMs:0.00} ms, audio {_audioCost.AverageMs:0.00} ms");
        _frameSamples = null;
        GetTree().Quit();
    }

    /// <summary>Dev/test hook <c>--open-programs</c>: a program with an if and some tests, on the craft flown, with the panel open (#120).</summary>
    private void DemoProgram()
    {
        if (_player is null) return;
        _sector.DebugSurveyAll();
        var yard = _sector.Crafts.First(c => c.IsShipyard).Id.Value;
        var rock = _sector.Bodies.First(b => b.IsMineable && b.DepositsKg.ContainsKey("iron_ore")).Id.Value;
        var steps = new List<ProgramStep>
        {
            new() { Id = 1, Kind = StepKind.Mine, BodyId = rock, Material = "iron_ore", Percent = 80, GiveUpSeconds = 300, Fallback = StepFallback.NextStep },
            new() { Id = 2, Kind = StepKind.Unload, StationId = yard, UntilDone = true },
            new()
            {
                Id = 3, Kind = StepKind.If, Until = new() { Any = true, Tests = new() { new() { Kind = TestKind.PowerPercent, Below = true, Value = 40 }, new() { Kind = TestKind.FuelTonnes, Below = true, Value = 1 } } },
                Then = new() { new() { Id = 4, Kind = StepKind.Recharge, StationId = yard, Percent = 90, Seconds = 60 }, new() { Id = 5, Kind = StepKind.Refuel, StationId = yard, Tonnes = 2 } },
                Otherwise = new() { new() { Id = 6, Kind = StepKind.Message, Text = "all well", OnlyIf = new() { Tests = new() { new() { Kind = TestKind.StationStockTonnes, StationId = yard, Material = "iron_ore", Value = 50 } } } } },
            },
            new() { Id = 7, Kind = StepKind.Repair, OnlyIf = new() { Tests = new() { new() { Kind = TestKind.HealthPercent, Below = true, Value = 90 } } } },
        };
        _sector.SaveProgram(0, "Iron run", steps, out var id);
        _sector.StartJob(_player.Id, new ProgramJob(id));
        _programPanel?.Open(id);
    }
}
