using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Programs: jobs as steps (#120).</summary>
public sealed class ProgramTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private static readonly MaterialCatalog Materials = TestData.Materials();
    private const int Second = FixedStepClock.TicksPerSecond;

    private static (SectorState Sector, CraftState Ship, CraftState Yard) Game()
    {
        var game = TestData.NewGame();
        game.Sector.GetCraft(game.PlayerCraftId).Inventory.Add("fuel", 150); // test games start without
        return (game.Sector, game.Sector.GetCraft(game.PlayerCraftId), game.Sector.GetCraft(game.ShipyardId));
    }

    private static ProgramCondition If(TestKind kind, double value, bool below = false, uint station = 0, string material = "") =>
        new() { Tests = new() { new ProgramTest { Kind = kind, Value = value, Below = below, StationId = station, Material = material } } };

    private static ProgramStep Say(int id, string text) => new() { Id = id, Kind = StepKind.Message, Text = text };

    private static ProgramJob Run(SectorState sector, CraftState ship, params ProgramStep[] steps)
    {
        var saved = sector.SaveProgram(0, $"P{sector.Programs.Count}", steps, out var id);
        Assert.True(saved.Done, saved.Message);
        Assert.True(sector.StartJob(ship.Id, new ProgramJob(id)).Done);
        return (ProgramJob)ship.Job!;
    }

    private static void Step(SectorState sector, double seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * Second && until?.Invoke() != true; i++) sector.Step();
    }

    private static void Dock(SectorState sector, CraftState ship, CraftState yard, int port = 0)
    {
        ship.Position = ship.PreviousPosition = sector.BerthPosition(yard, port, ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
    }

    private static List<string> Said(SectorState sector, CraftState ship) => sector.Events.Where(e => e.Text.StartsWith($"{ship.Name}: ", StringComparison.Ordinal)).Select(e => e.Text[(ship.Name.Length + 2)..]).ToList();

    private static readonly ProgramStep Forever = new() { Id = 900, Kind = StepKind.Wait, Seconds = 80_000 };

    // --- Order, ifs and conditions ---

    [Theory]
    [InlineData(10, "first", "low", "last")]
    [InlineData(90, "first", "high", "deep", "last")]
    public void Steps_go_in_order_and_ifs_choose(double percent, params string[] expected)
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        ship.Inventory.Add("metal", (long)(ship.CargoCapacityKg * percent / 100) - ship.CargoKg);
        // One message a minute: space them out.
        ProgramStep Pause(int id) => new() { Id = id, Kind = StepKind.Wait, Seconds = 61 };
        Run(sector, ship, Say(1, "first"), Pause(2), new ProgramStep
        {
            Id = 3, Kind = StepKind.If, Until = If(TestKind.CargoPercent, 50),
            Then = new() { Say(4, "high"), Pause(5), new() { Id = 6, Kind = StepKind.If, Until = If(TestKind.CargoPercent, 80), Then = new() { Say(7, "deep"), Pause(8) } } },
            Otherwise = new() { Say(9, "low"), Pause(10) },
        }, Say(11, "last"), Forever);
        Step(sector, 300);
        Assert.Equal(expected, Said(sector, ship));
    }

    [Fact]
    public void A_step_is_passed_over_unless_its_condition_holds_and_any_or_all_are_minded()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        var yes = new ProgramTest { Kind = TestKind.FuelTonnes, Value = 0.1 };
        var no = new ProgramTest { Kind = TestKind.FuelTonnes, Value = 50 };
        Assert.True(sector.Holds(new ProgramCondition { Any = true, Tests = new() { no, yes } }, ship, 0));
        Assert.False(sector.Holds(new ProgramCondition { Tests = new() { no, yes } }, ship, 0));
        Assert.True(sector.Holds(new ProgramCondition { Tests = new() { yes, no with { Below = true } } }, ship, 0));
        Assert.False(sector.Holds(new ProgramCondition { Any = true, Tests = new() { no, yes with { Below = true } } }, ship, 0));
        Run(sector, ship, Say(1, "no") with { OnlyIf = new() { Tests = new() { no } } }, Say(2, "yes") with { OnlyIf = new() { Tests = new() { yes } } }, Forever);
        Step(sector, 2);
        Assert.Equal(new[] { "yes" }, Said(sector, ship));
    }

    [Fact]
    public void Every_test_measures_what_it_says()
    {
        var (sector, ship, yard) = Game();
        ship.Inventory.Add("metal", 2_500);
        ship.StoredEnergyKJ = 0.3 * ship.Stats.EnergyCapacityKJ;
        double Of(TestKind kind, uint station = 0, string material = "", double seconds = 0) => sector.Measure(new ProgramTest { Kind = kind, StationId = station, Material = material }, ship, seconds);
        Assert.Equal(100.0 * ship.CargoKg / ship.CargoCapacityKg, Of(TestKind.CargoPercent), 9);
        Assert.Equal(2.5, Of(TestKind.CargoTonnes, material: "metal"), 9);
        Assert.Equal(30, Of(TestKind.PowerPercent), 6);
        Assert.Equal(0.15, Of(TestKind.FuelTonnes), 9);
        Assert.Equal(2, Of(TestKind.StepMinutes, seconds: 120), 9);
        Assert.Equal(yard.Inventory["metal"] / 1000.0, Of(TestKind.StationStockTonnes, yard.Id.Value, "metal"), 9);
        Assert.Equal(2, Of(TestKind.StationFreePorts, yard.Id.Value), 9);
        Dock(sector, ship, yard);
        Assert.Equal(1, Of(TestKind.StationFreePorts, yard.Id.Value), 9);
        Assert.Equal(100, Of(TestKind.HealthPercent), 9);
        sector.DebugDamagePart(ship.Id, ship.Design.Cells[0].Pos, 0.5);
        Assert.Equal(100 * (1 - 0.5 / 14), Of(TestKind.HealthPercent), 6);
        foreach (var gone in new[] { TestKind.StationStockTonnes, TestKind.StationFreePorts }) Assert.Equal(0, Of(gone, 9999, "metal"));
    }

    // --- Each action ---

    [Fact]
    public void Load_takes_up_to_an_amount_and_unload_keeps_one_with_exact_mass()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, ship, new() { Id = 1, Kind = StepKind.Load, Material = "metal", Tonnes = 7.5, UntilDone = true }, new() { Id = 2, Kind = StepKind.Wait, Seconds = 5 }, new() { Id = 3, Kind = StepKind.Unload, Material = "metal", Tonnes = 2 }, Forever);
        Step(sector, 2);
        Assert.Equal(7_500, ship.Inventory["metal"]);
        Step(sector, 10);
        Assert.Equal(2_000, ship.Inventory["metal"]);
        Assert.Equal(5_500, ship.Job!.DeliveredKg);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Unloading_everything_keeps_the_fuel_and_refuelling_tops_it_up()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        ship.Inventory.Add("metal", 100);
        ship.Inventory.Add("slag", 200);
        var fuel = ship.Inventory["fuel"];
        Run(sector, ship, new() { Id = 1, Kind = StepKind.Unload, UntilDone = true }, new() { Id = 2, Kind = StepKind.Wait, Seconds = 5 }, new() { Id = 3, Kind = StepKind.Refuel, Tonnes = 1.2 }, Forever);
        Step(sector, 2);
        Assert.Equal((0, 0, fuel), (ship.Inventory["metal"], ship.Inventory["slag"], ship.Inventory["fuel"]));
        Step(sector, 8);
        Assert.Equal(1_200, ship.Inventory["fuel"]);
    }

    [Fact]
    public void Flying_and_docking_take_the_ship_there()
    {
        var (sector, ship, yard) = Game();
        var job = Run(sector, ship, new() { Id = 1, Kind = StepKind.FlyTo, StationId = yard.Id.Value }, Say(2, "there"), new() { Id = 3, Kind = StepKind.Dock, StationId = yard.Id.Value }, Say(4, "in"), Forever);
        Step(sector, 400, () => ship.IsDocked);
        Assert.True(ship.IsDocked, job.Status);
        Assert.Contains("there", Said(sector, ship));
    }

    [Fact]
    public void Flying_to_a_body_surveys_it()
    {
        var (sector, ship, _) = Game();
        var rock = TestData.HomeRocks(sector).First();
        foreach (var site in rock.Sites) site.Surveyed = false;
        rock.Surveyed = false;
        var job = Run(sector, ship, new() { Id = 1, Kind = StepKind.FlyTo, BodyId = rock.Id.Value }, Say(2, "there"), Forever);
        Step(sector, 1_800, () => Said(sector, ship).Count > 0);
        Assert.Equal(new[] { "there" }, Said(sector, ship));
        Assert.True(rock.AnySiteSurveyed, job.Status);
    }

    [Fact]
    public void Mining_and_delivering_round_and_round_as_a_mining_job_does()
    {
        var (sector, ship, yard) = Game();
        yard.LegacyUnlimitedStorage = true;
        var rock = TestData.HomeRocks(sector).First(b => b.DepositsKg.ContainsKey("iron_ore"));
        var mining = new MiningJob(rock.Id, "iron_ore", yard.Id, 0.25);
        var made = ProgramEdit.FromJob(mining)!.Value;
        var mass = sector.ComputeMassLedger().TotalKg;
        var job = Run(sector, ship, made.Steps.ToArray());
        Step(sector, 3_600, () => job.CompletedCycles >= 2);
        Assert.True(job.CompletedCycles >= 2, job.Status);
        Assert.True(job.DeliveredKg >= 15_000, $"{job.DeliveredKg}");
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        // About as fast as the plain job.
        var (plain, other, yard2) = Game();
        yard2.LegacyUnlimitedStorage = true;
        Assert.True(plain.StartJob(other.Id, new MiningJob(rock.Id, "iron_ore", yard2.Id, 0.25)).Done);
        Step(plain, 3_600, () => other.Job!.CompletedCycles >= 2);
        Assert.InRange(sector.TimeS, plain.TimeS * 0.8, plain.TimeS * 1.25);
    }

    [Fact]
    public void Freight_as_a_program_waits_for_stock_and_minds_what_is_kept()
    {
        var (sector, ship, yard) = Game();
        var depot = new ShipDesign("d", "Depot", new[] { new DesignCell(new GridPos(0, 0), "storage_silo"), new DesignCell(new GridPos(1, 0), "docking_port", 1) });
        var other = new CraftState(sector.AllocateCraftId(), "Depot", depot, depot.ComputeStats(Catalog), yard.Position, 0) { LegacyNoCrew = true };
        sector.AddCraft(other);
        var home = SectorGenerator.HomePlanet(sector);
        sector.PutOnRails(other.Id, yard.RailOrbit! with { PhaseAtZeroRad = yard.RailOrbit!.PhaseAtZeroRad + 600 / (home.RadiusM + 600) });
        yard.Inventory.TryRemove(new Dictionary<string, long> { ["metal"] = yard.Inventory["metal"] });
        var job = Run(sector, ship, ProgramEdit.FromJob(new HaulJob(yard.Id, "metal", other.Id, 5_000, 8_000))!.Value.Steps.ToArray());
        Step(sector, 120);
        Assert.Contains("until", job.Status);
        sector.DebugAddStock(yard.Id, "metal", 30_000);
        Step(sector, 1_200, () => other.Inventory["metal"] > 0);
        Assert.True(other.Inventory["metal"] > 5_000, job.Status);
        var had = other.Inventory["metal"];
        Step(sector, 600);
        Assert.Equal(had, other.Inventory["metal"]); // above what's kept: passed over
    }

    [Fact]
    public void Recharging_has_a_level_and_a_limit_and_repairs_go_to_the_yard()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        ship.StoredEnergyKJ = 0;
        yard.StoredEnergyKJ = yard.Stats.EnergyCapacityKJ;
        var job = Run(sector, ship, new() { Id = 1, Kind = StepKind.Recharge, Percent = 30 }, Say(2, "charged"), new() { Id = 3, Kind = StepKind.Recharge, Percent = 100, Seconds = 4 }, new() { Id = 4, Kind = StepKind.Repair }, Forever);
        Step(sector, 30, () => Said(sector, ship).Count > 0);
        Assert.True(ship.StoredEnergyKJ >= 0.29 * ship.Stats.EnergyCapacityKJ, job.Status);
        sector.DebugDamagePart(ship.Id, ship.Design.Cells[3].Pos, 0.9); // light: it wouldn't go by itself
        Step(sector, 600, () => !SectorState.NeedsRepair(ship) && job.StepId == 900);
        Assert.False(SectorState.NeedsRepair(ship), job.Status);
        Assert.Equal(900, job.StepId);
    }

    [Fact]
    public void Waiting_is_by_time_or_until_something_and_needs_nobody()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        var job = Run(sector, ship, new() { Id = 1, Kind = StepKind.Wait, Seconds = 10 }, Say(2, "time"), new() { Id = 3, Kind = StepKind.Wait, Until = If(TestKind.StationStockTonnes, 200, station: yard.Id.Value, material: "metal") }, Forever);
        Step(sector, 9);
        Assert.Equal(1, job.StepId);
        Assert.DoesNotContain("waiting", job.Status);
        Assert.DoesNotContain(sector.Alerts(), a => a.Target.Craft == ship.Id);
        Step(sector, 3);
        Assert.Equal(3, job.StepId);
        Assert.True(ship.IsDocked);
        sector.DebugAddStock(yard.Id, "metal", 200_000);
        Step(sector, 2);
        Assert.Equal(900, job.StepId);
    }

    // --- When it can't be done ---

    [Theory]
    [InlineData(StepFallback.KeepTrying, 1, true)]
    [InlineData(StepFallback.NextStep, 900, true)]
    [InlineData(StepFallback.StopJob, 1, false)]
    public void What_it_does_when_a_step_cant_be_done(StepFallback fallback, int step, bool active)
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        var job = Run(sector, ship, new() { Id = 1, Kind = StepKind.Dock, StationId = 4321, GiveUpSeconds = 10, Fallback = fallback }, Forever);
        Step(sector, 5);
        Assert.StartsWith("waiting: step 1 of 2: the station is missing", job.Status);
        Assert.Contains(sector.Alerts(), a => a.Target.Craft == ship.Id);
        Step(sector, 20);
        Assert.Equal((step, active), (job.StepId, job.Active));
        if (!active) Assert.Contains(sector.Events, e => e.Text.Contains("stopped its program") && e.Text.Contains("missing"));
    }

    [Fact]
    public void Starting_over_and_rounds_with_nothing_to_do_dont_spin()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        var job = Run(sector, ship, Say(1, "again") with { OnlyIf = If(TestKind.FuelTonnes, 500) }, new() { Id = 2, Kind = StepKind.If, Until = If(TestKind.FuelTonnes, 500), Then = new() { Forever } });
        sector.Step();
        Assert.Contains("nothing to do", job.Status);
        var cycles = job.CompletedCycles;
        Step(sector, 4);
        Assert.Equal(cycles, job.CompletedCycles);
        var over = Run(sector, ship, new ProgramStep { Id = 1, Kind = StepKind.Dock, StationId = 4321, GiveUpSeconds = 3, Fallback = StepFallback.FirstStep });
        Step(sector, 30);
        Assert.True(over.Active);
    }

    [Fact]
    public void Messages_come_at_most_once_a_minute()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        Run(sector, ship, Say(1, "hello"), new() { Id = 2, Kind = StepKind.Wait, Seconds = 1 });
        Step(sector, 150);
        Assert.Equal(3, Said(sector, ship).Count);
    }

    [Fact]
    public void Whats_in_the_way_is_said()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        string Status(ProgramStep step)
        {
            var job = Run(sector, ship, step with { Id = 1 });
            Step(sector, 0.2);
            return job.Status;
        }
        Assert.Contains("no material is chosen", Status(new() { Kind = StepKind.Load, StationId = yard.Id.Value }));
        Assert.Contains("no body is chosen", Status(new() { Kind = StepKind.Mine, Material = "iron_ore" }));
        Assert.Contains("the ship itself", Status(new() { Kind = StepKind.Dock, StationId = ship.Id.Value }));
        Assert.Contains("body chosen is gone", Status(new() { Kind = StepKind.FlyTo, BodyId = 5555 }));
        yard.FactionId = 4;
        Assert.Contains("isn't yours", Status(new() { Kind = StepKind.Dock, StationId = yard.Id.Value }));
        yard.FactionId = 0;
        sector.TryUndock(ship.Id);
        Assert.Contains("isn't docked", Status(new() { Kind = StepKind.Load, Material = "metal" }));
        Assert.False(sector.StartJob(ship.Id, new ProgramJob(99)).Done);
        Assert.False(sector.StartJob(yard.Id, new ProgramJob(1)).Done);
    }

    // --- Shared programs ---

    [Fact]
    public void A_program_is_shared_and_changing_it_keeps_each_ships_place()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        ProgramStep Stay(int id, double s = 80_000) => new() { Id = id, Kind = StepKind.Wait, Seconds = s };
        var job = Run(sector, ship, Stay(1, 1), Stay(2));
        Step(sector, 5);
        Assert.Equal(2, job.StepId);
        Assert.Equal(1, sector.ProgramUsers(job.ProgramId));
        // A step put in before it: still at its own step, and not begun again.
        var begun = job.BegunTick;
        Assert.Contains("1 ship", sector.SaveProgram(job.ProgramId, "P0", new[] { Stay(1, 1), Stay(3), Stay(2) }, out _).Message);
        Step(sector, 2);
        Assert.Equal((2, begun), (job.StepId, job.BegunTick));
        // The step itself changed: begun again. Gone: from the start.
        sector.SaveProgram(job.ProgramId, "P0", new[] { Stay(1, 1), Stay(2, 70_000) }, out _);
        Step(sector, 1);
        Assert.Equal(2, job.StepId);
        Assert.NotEqual(begun, job.BegunTick);
        sector.SaveProgram(job.ProgramId, "P0", new[] { Stay(7), Stay(8) }, out _);
        Step(sector, 1);
        Assert.Equal(7, job.StepId);
        Assert.Contains("runs", sector.DeleteProgram(job.ProgramId).Message);
        sector.StopJob(ship.Id);
        Assert.True(sector.DeleteProgram(job.ProgramId).Done);
        Assert.False(sector.DeleteProgram(job.ProgramId).Done);
    }

    [Fact]
    public void Names_are_kept_apart_and_numbers_not_used_twice()
    {
        var (sector, _, _) = Game();
        Assert.True(sector.SaveProgram(0, " My  run ", new[] { Say(1, "x") }, out var a).Done);
        Assert.Equal("My run", sector.ProgramById(a)!.Name);
        Assert.False(sector.SaveProgram(0, "my RUN", new[] { Say(1, "x") }, out _).Done);
        Assert.True(sector.SaveProgram(a, "my RUN", new[] { Say(1, "x") }, out _).Done);
        Assert.True(sector.SaveProgram(0, "Other", new[] { Say(1, "x") }, out var b).Done);
        Assert.NotEqual(a, b);
        Assert.False(sector.SaveProgram(77, "Lost", new[] { Say(1, "x") }, out _).Done);
    }

    // --- Editing ---

    [Fact]
    public void Editing_finds_steps_wherever_they_are()
    {
        var steps = new List<ProgramStep> { Say(1, "a"), new() { Id = 2, Kind = StepKind.If, Until = If(TestKind.FuelTonnes, 1), Then = new() { Say(3, "b"), Say(4, "c") } }, Say(5, "d") };
        int[] Ids(IEnumerable<ProgramStep> s) => JobProgram.All(s).Select(x => x.Id).ToArray();
        Assert.Equal(6, ProgramEdit.NextId(steps));
        Assert.Equal(new[] { 1, 2, 3, 6, 4, 5 }, Ids(ProgramEdit.AddAfter(steps, 3, Say(6, "e"))));
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 5 }, Ids(ProgramEdit.AddInside(steps, 2, true, Say(6, "e"))));
        Assert.Equal(6, ProgramEdit.Find(ProgramEdit.AddInside(steps, 2, true, Say(6, "e")), 2)!.Otherwise[0].Id);
        Assert.Equal(new[] { 1, 2, 4, 3, 5 }, Ids(ProgramEdit.Move(steps, 4, -1)));
        Assert.Equal(Ids(steps), Ids(ProgramEdit.Move(steps, 3, -1)));
        Assert.Equal(new[] { 1, 5 }, Ids(ProgramEdit.Remove(steps, 2)));
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 7, 8, 5 }, Ids(ProgramEdit.Copy(steps, 2)));
        Assert.Equal("z", ProgramEdit.Find(ProgramEdit.Replace(steps, Say(4, "z")), 4)!.Text);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Ids(steps)); // the old list is as it was
        Assert.Null(ProgramEdit.FromJob(new SurveyJob()));
    }

    [Fact]
    public void What_isnt_a_program_is_refused_with_the_reason()
    {
        string? Wrong(params ProgramStep[] steps) => JobProgram.Problem("P", steps, Materials);
        Assert.Null(Wrong(Say(1, "x")));
        Assert.Contains("name", JobProgram.Problem(" ", new[] { Say(1, "x") }, Materials));
        Assert.Contains("at least one", Wrong());
        Assert.Contains("same number", Wrong(Say(1, "x"), Say(1, "y")));
        Assert.Contains("unknown kind", Wrong(Say(1, "x") with { Kind = (StepKind)99 }));
        Assert.Contains("out of range", Wrong(Say(1, "x") with { Seconds = double.NaN }));
        Assert.Contains("out of range", Wrong(Say(1, "x") with { Percent = 101 }));
        Assert.Contains("Unknown material", Wrong(Say(1, "x") with { Material = "cheese" }));
        Assert.Contains("nothing to test", Wrong(new ProgramStep { Id = 1, Kind = StepKind.If }));
        Assert.Contains("no tests", Wrong(Say(1, "x") with { OnlyIf = new() }));
        Assert.Contains("Only an if", Wrong(Say(1, "x") with { Then = new() { Say(2, "y") } }));
        Assert.Contains("at most 64", Wrong(Enumerable.Range(1, 65).Select(i => Say(i, "x")).ToArray()));
        var deep = Say(50, "x");
        for (var i = 1; i <= 4; i++) deep = new ProgramStep { Id = i, Kind = StepKind.If, Until = If(TestKind.FuelTonnes, 1), Then = new() { deep } };
        Assert.Contains("deep at most", Wrong(deep));
    }

    [Fact]
    public void A_dry_run_marks_each_step_and_changes_nothing()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        var before = SaveGameSerializer.ToJson(sector, ship.Id);
        var checks = sector.CheckProgram(ship, new[]
        {
            new ProgramStep { Id = 1, Kind = StepKind.If, Until = If(TestKind.FuelTonnes, 0.1), Then = new() { Say(2, "x") }, Otherwise = new() { Say(3, "y") } },
            new ProgramStep { Id = 4, Kind = StepKind.Dock, StationId = 4321 }, Say(5, "z") with { OnlyIf = If(TestKind.CargoPercent, 99) }, new ProgramStep { Id = 6, Kind = StepKind.Wait },
        }).ToDictionary(c => c.StepId);
        Assert.Equal((true, true, false, false, false), (checks[1].Reached, checks[2].Reached, checks[3].Reached, checks[2].Wrong, checks[5].Reached));
        Assert.True(checks[4].Wrong);
        Assert.Contains("missing", checks[4].Note);
        Assert.True(checks[6].Wrong);
        Assert.Equal(before, SaveGameSerializer.ToJson(sector, ship.Id));
        Assert.Contains("only if the hold is at least 99% full", sector.Describe(Say(5, "z") with { OnlyIf = If(TestKind.CargoPercent, 99) }));
    }

    // --- Files and saves ---

    [Fact]
    public void Files_find_stations_by_name_and_leave_the_rest_to_choose()
    {
        var steps = new List<ProgramStep>
        {
            new() { Id = 1, Kind = StepKind.Mine, BodyId = 7, Material = "iron_ore" }, new() { Id = 2, Kind = StepKind.Unload, StationId = 3, OnlyIf = If(TestKind.StationFreePorts, 1, station: 4) },
            new() { Id = 3, Kind = StepKind.If, Until = If(TestKind.StationStockTonnes, 5, station: 3, material: "metal"), Then = new() { new() { Id = 4, Kind = StepKind.Dock, StationId = 4 } } },
        };
        var file = new JobProgram(1, "Run", steps).ToFile(i => i == 3 ? "Yard" : "Depot", _ => "Rock");
        var (name, read) = JobProgram.FromFile(file, Materials, n => n == "Yard" ? 33u : 0, n => n == "Rock" ? 77u : 0);
        Assert.Equal("Run", name);
        Assert.Equal((77u, 33u, 0u), (read[0].BodyId, read[1].StationId, read[1].OnlyIf!.Tests[0].StationId));
        Assert.Equal((33u, 0u), (read[2].Until!.Tests[0].StationId, read[2].Then[0].StationId));
        var same = JobProgram.FromFile(file, Materials, n => n == "Yard" ? 3u : 4, _ => 7).Steps;
        Assert.Equal(file, new JobProgram(1, "Run", same).ToFile(i => i == 3 ? "Yard" : "Depot", _ => "Rock"));
        foreach (var bad in new[] { "", "null", "[]", "{", """{ "kind": "race" }""", """{ "kind": "program", "schemaVersion": 2 }""", """{ "kind": "program", "schemaVersion": 1, "name": "x" }""", """{ "kind": "program", "schemaVersion": 1, "name": "x", "steps": [ null ] }""",
                     """{ "kind": "program", "schemaVersion": 1, "name": "x", "steps": [ { "id": 1, "kind": "Explode" } ] }""", """{ "kind": "program", "schemaVersion": 1, "name": "x", "steps": [ { "id": 1, "kind": "Load", "material": "cheese" } ] }""",
                     """{ "kind": "program", "schemaVersion": 1, "name": "x", "steps": [ { "id": 1, "kind": "Wait", "until": { "tests": [ null ] } } ] }""", """{ "kind": "program", "schemaVersion": 1, "name": "x", "steps": [ { "id": 1, "kind": "Wait", "then": null } ] }""" })
            Assert.Throws<FormatException>(() => JobProgram.FromFile(bad, Materials, _ => 0, _ => 0));
    }

    [Fact]
    public void Saves_at_any_moment_continue_exactly()
    {
        var (sector, ship, yard) = Game();
        yard.LegacyUnlimitedStorage = true;
        var rock = TestData.HomeRocks(sector).First(b => b.DepositsKg.ContainsKey("iron_ore"));
        var job = Run(sector, ship, new() { Id = 1, Kind = StepKind.Mine, BodyId = rock.Id.Value, Material = "iron_ore", Percent = 10 },
            new() { Id = 2, Kind = StepKind.Unload, StationId = yard.Id.Value, UntilDone = true }, Say(3, "round"), new() { Id = 4, Kind = StepKind.Recharge, Percent = 90, Seconds = 20 },
            new() { Id = 5, Kind = StepKind.If, Until = If(TestKind.FuelTonnes, 2, below: true), Then = new() { new() { Id = 6, Kind = StepKind.Refuel, StationId = yard.Id.Value, Tonnes = 3 } }, Otherwise = new() { new() { Id = 7, Kind = StepKind.Wait, Seconds = 9 } } });
        var rng = new Random(4);
        for (var round = 0; round < 14; round++)
        {
            Step(sector, rng.Next(20, 160));
            var json = SaveGameSerializer.ToJson(sector, ship.Id);
            var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
            Assert.Equal(json, SaveGameSerializer.ToJson(loaded, ship.Id));
            for (var i = 0; i < 25 * Second; i++) { sector.Step(); loaded.Step(); }
            Assert.Equal(SaveGameSerializer.ToJson(sector, ship.Id), SaveGameSerializer.ToJson(loaded, ship.Id));
        }
        Assert.True(job.CompletedCycles > 0, job.Status);
    }

    [Fact]
    public void A_save_with_a_bad_program_is_refused_and_one_whose_program_is_gone_says_so()
    {
        var (sector, ship, yard) = Game();
        Dock(sector, ship, yard);
        var job = Run(sector, ship, Say(1, "hello"), Forever);
        var json = SaveGameSerializer.ToJson(sector, ship.Id);
        Assert.Contains("Program 1", Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json.Replace("\"id\": 900", "\"id\": 1"), Catalog)).Message);
        Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json.Replace("\"kind\": \"Message\"", "\"kind\": \"Explode\""), Catalog));
        var lost = SaveGameSerializer.FromJson(json.Replace("\"programId\": 1", "\"programId\": 5"), Catalog).Sector;
        lost.Step();
        Assert.Contains("program is gone", lost.GetCraft(ship.Id).Job!.Status);
        Assert.True(job.Active);
    }

    // --- Scale ---

    [Fact]
    public void A_thousand_programmed_ships_cost_about_what_plain_jobs_do()
    {
        SectorState Crowd(bool programmed)
        {
            var (sector, _, yard) = Game();
            yard.LegacyUnlimitedStorage = true;
            sector.DebugSurveyAll();
            var rock = TestData.HomeRocks(sector).First(b => b.DepositsKg.ContainsKey("iron_ore"));
            var design = TestData.StarterShip();
            var stats = design.ComputeStats(Catalog);
            sector.SaveProgram(0, "Run", new[]
            {
                new ProgramStep { Id = 1, Kind = StepKind.Mine, BodyId = rock.Id.Value, Material = "iron_ore", Percent = 25 }, new ProgramStep { Id = 2, Kind = StepKind.Unload, StationId = yard.Id.Value, UntilDone = true },
                new ProgramStep { Id = 3, Kind = StepKind.If, Until = If(TestKind.PowerPercent, 30, below: true), Then = new() { new ProgramStep { Id = 4, Kind = StepKind.Recharge, StationId = yard.Id.Value, Percent = 30, Seconds = 10 } } },
            }, out var id);
            for (var i = 0; i < 1_000; i++)
            {
                var craft = new CraftState(sector.AllocateCraftId(), $"M{i}", design, stats, yard.Position + new Vector2D(200 + 30 * (i % 100), -300 - 30 * (i / 100)), 0) { Velocity = yard.Velocity };
                sector.AddCraft(craft);
                Assert.True((programmed ? sector.StartJob(craft.Id, new ProgramJob(id)) : sector.StartJob(craft.Id, new MiningJob(rock.Id, "iron_ore", yard.Id, 0.25))).Done);
            }
            for (var i = 0; i < 20; i++) sector.Step();
            return sector;
        }
        var (programs, plain) = (Crowd(true), Crowd(false));
        var (a, b) = (new Stopwatch(), new Stopwatch());
        for (var round = 0; round < 30; round++) // turn about: whatever else the machine is doing slows both
        {
            a.Start();
            for (var i = 0; i < 4; i++) programs.Step();
            a.Stop();
            b.Start();
            for (var i = 0; i < 4; i++) plain.Step();
            b.Stop();
        }
        Assert.True(a.ElapsedMilliseconds < 1.6 * b.ElapsedMilliseconds + 50, $"{a.ElapsedMilliseconds / 120.0} ms a tick, plain {b.ElapsedMilliseconds / 120.0}");
    }
}
