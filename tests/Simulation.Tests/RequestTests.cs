using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;
using Xunit.Abstractions;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Station requests and serve-requests freighters with cargo and berth reservations (#1).</summary>
public class RequestTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private readonly ITestOutputHelper _log;

    public RequestTests(ITestOutputHelper log) => _log = log;

    /// <summary>A small depot (one docking port, 40 t) on the yard's orbit, a little ahead of it.</summary>
    private static CraftState AddDepot(SectorState sector, CraftState yard, string name = "Depot", double phaseOffset = 0.25)
    {
        var design = new ShipDesign("depot_" + name.ToLowerInvariant(), name, new[]
        {
            new DesignCell(new GridPos(0, -1), "docking_port"), new DesignCell(new GridPos(-1, 0), "cargo"),
            new DesignCell(new GridPos(0, 0), "hull"), new DesignCell(new GridPos(1, 0), "cargo"),
        });
        var depot = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(depot);
        sector.PutOnRails(depot.Id, yard.RailOrbit! with { PhaseAtZeroRad = yard.RailOrbit!.PhaseAtZeroRad + phaseOffset });
        return depot;
    }

    private static CraftState AddFreighter(SectorState sector, CraftState near, string name)
    {
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog),
            near.Position + new Vector2D(200 + 80 * sector.Crafts.Count, -300), 0) { Velocity = near.Velocity };
        sector.AddCraft(ship);
        return ship;
    }

    private static long Mass(SectorState sector) =>
        sector.Bodies.Sum(b => b.DepositsKg.Values.Sum())
        + sector.Crafts.Sum(c => c.CargoKg + c.BuildQueue.Sum(o => o.MaterialsKg.Values.Sum()) + (c.RefiningBatch?.InputsKg.Values.Sum() ?? 0))
        + sector.EjectedKg.Values.Sum();

    [Fact]
    public void SetRequest_Validates_AndZeroClears()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);

        Assert.False(sector.SetRequest(yard.Id, "unobtainium", 1_000).Done);
        Assert.False(sector.SetRequest(yard.Id, "metal", -1).Done);
        Assert.False(sector.SetRequest(ship.Id, "metal", 1_000).Done); // no docking port
        Assert.False(sector.SetRequest(yard.Id, "metal", yard.CargoCapacityKg + 1).Done);
        Assert.True(sector.SetRequest(yard.Id, "metal", 50_000).Done);
        Assert.Equal(50_000, yard.Requests["metal"]);
        Assert.True(sector.SetRequest(yard.Id, "metal", 0).Done);
        Assert.Empty(yard.Requests);
    }

    [Fact]
    public void Freighter_FillsARequest_ThenWaitsWithAReason_ConservingMass()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = AddDepot(sector, yard);
        var freighter = sector.GetCraft(game.PlayerCraftId);
        var metal = yard.Inventory["metal"];
        Assert.True(sector.SetRequest(depot.Id, "metal", 12_000).Done);
        Assert.True(sector.StartJob(freighter.Id, new ServeRequestsJob(1_000)).Done);
        var job = (ServeRequestsJob)freighter.Job!;
        var mass = Mass(sector);

        var ticks = 0;
        for (; ticks < 30 * TicksPerMinute && depot.Inventory["metal"] < 12_000; ticks++)
        {
            sector.Step();
            if (ticks % 600 == 0) Assert.Equal(mass, Mass(sector));
        }
        _log.WriteLine($"filled in {ticks / (double)TicksPerMinute:0.0} min: {job.Status}");

        Assert.Equal(12_000, depot.Inventory["metal"]); // exactly the request, no more
        Assert.Equal(metal - 12_000, yard.Inventory["metal"]);
        Assert.Equal(1, job.CompletedCycles);
        Assert.Equal(12_000, job.DeliveredKg);
        for (var i = 0; i < 2 * TicksPerMinute; i++) sector.Step();
        Assert.Equal(HaulJobState.Waiting, job.State);
        Assert.Equal("no open requests", job.WaitReason);
        Assert.Equal(mass, Mass(sector));
    }

    [Fact]
    public void TwoFreighters_NeverClaimTheSameCargo()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = AddDepot(sector, yard);
        var a = AddFreighter(sector, yard, "A");
        var b = AddFreighter(sector, yard, "B");
        sector.SetRequest(depot.Id, "metal", 10_000);
        sector.StartJob(a.Id, new ServeRequestsJob(1_000));
        sector.StartJob(b.Id, new ServeRequestsJob(1_000));
        sector.Step();

        var claimA = ((ServeRequestsJob)a.Job!).Contract;
        Assert.NotNull(claimA);
        Assert.Equal(10_000, claimA!.AmountKg);                 // A claims the whole request…
        Assert.Null(((ServeRequestsJob)b.Job!).Contract);       // …so B finds nothing left to claim
        Assert.Equal("no open requests", b.Job!.WaitReason);
        Assert.Equal(10_000, sector.IncomingKg(depot.Id, "metal"));
        Assert.Equal(0, sector.OpenRequestKg(depot.Id, "metal"));
    }

    [Fact]
    public void Freighters_SplitALargeRequest_AndRespectTheSourcesOwnRequest()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = AddDepot(sector, yard);
        var a = AddFreighter(sector, yard, "A");
        var b = AddFreighter(sector, yard, "B");
        var c = AddFreighter(sector, yard, "C");
        // The yard holds 30 t of metal but wants to keep 22 t itself: only 8 t is spare.
        sector.SetRequest(yard.Id, "metal", 22_000);
        sector.SetRequest(depot.Id, "metal", 40_000);
        foreach (var ship in new[] { a, b, c }) sector.StartJob(ship.Id, new ServeRequestsJob(1_000));
        sector.Step();

        var claims = new[] { a, b, c }.Select(s => ((ServeRequestsJob)s.Job!).Contract).Where(x => x is not null).ToList();
        Assert.Equal(8_000, claims.Sum(x => x!.AmountKg));
        Assert.All(claims, x => Assert.Equal(yard.Id, x!.PickupId));
        Assert.Contains("no station has spare metal", c.Job!.WaitReason ?? b.Job!.WaitReason ?? "");
    }

    [Fact]
    public void Berths_AreReserved_OneVisitPerDockingPort()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var source = AddDepot(sector, yard, "Source", 0.25); // one docking port
        var sink = AddDepot(sector, yard, "Sink", 0.5);
        source.Inventory.Add("silicate", 30_000);
        var a = AddFreighter(sector, yard, "A");
        var b = AddFreighter(sector, yard, "B");
        a.Inventory.Add("slag", 30_000); // A only has room for 10 t, so 20 t stays open for B
        sector.SetRequest(sink.Id, "silicate", 30_000);
        sector.StartJob(a.Id, new ServeRequestsJob(1_000));
        sector.StartJob(b.Id, new ServeRequestsJob(1_000));
        sector.Step();

        Assert.Equal(10_000, ((ServeRequestsJob)a.Job!).Contract!.AmountKg);
        Assert.Null(((ServeRequestsJob)b.Job!).Contract);
        Assert.Contains("docking ports are reserved", b.Job!.WaitReason);
    }

    [Fact]
    public void ServeJob_MidTrip_SurvivesSaveLoad_AndContinuesIdentically()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = AddDepot(sector, yard);
        var freighter = sector.GetCraft(game.PlayerCraftId);
        sector.SetRequest(depot.Id, "electronics", 5_000);
        sector.StartJob(freighter.Id, new ServeRequestsJob(1_000));
        for (var i = 0; i < 40 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        Assert.NotNull(((ServeRequestsJob)freighter.Job!).Contract);

        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        for (var i = 0; i < 3 * TicksPerMinute; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        Assert.Equal(5_000, loaded.GetCraft(depot.Id).Inventory["electronics"]);
    }
}
