using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#75 power sources framework: fuel generators that follow demand, shared power pools, exact mass.</summary>
public sealed class GeneratorTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;

    /// <summary>A small station with a fuel generator, a battery, a docking port and a drill-less power user (a refinery).</summary>
    private static (SectorState Sector, CraftState Station) StationWithGenerator(bool solar = false)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var cells = new List<DesignCell>
        {
            new(new GridPos(0, 0), "fuel_generator"),
            new(new GridPos(1, 0), "battery"),
            new(new GridPos(0, -1), "docking_port"),
            new(new GridPos(-1, 0), "storage_silo"),
        };
        if (solar) cells.Add(new DesignCell(new GridPos(0, 1), "solar_panel"));
        var design = new ShipDesign("gen_station", "Generator station", cells);
        var yard = sector.GetCraft(game.ShipyardId);
        var station = new CraftState(sector.AllocateCraftId(), "Gen", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(0, -4000), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(station);
        return (sector, station);
    }

    [Fact]
    public void The_fuel_generator_part_is_a_generator()
    {
        var gen = Catalog.Get("fuel_generator");
        Assert.True(gen.GeneratorKW > 0);
        var stats = new ShipDesign("g", "g", new[] { new DesignCell(new GridPos(0, 0), "fuel_generator") }).ComputeStats(Catalog);
        Assert.Equal(gen.GeneratorKW, stats.GeneratorKW);
        Assert.Equal(1, stats.GeneratorList[0].ExhaustKg); // burns 1 kg of fuel, leaves nothing: 1 kg of exhaust
    }

    [Fact]
    public void A_generator_burns_only_to_fill_the_battery_then_idles()
    {
        var (sector, station) = StationWithGenerator();
        sector.DebugAddStock(station.Id, "fuel", 1_000);
        station.StoredEnergyKJ = 0;
        var mass = sector.ComputeMassLedger().TotalKg;

        for (var i = 0; i < 200 * TicksPerSecond; i++) sector.Step(); // 400 kW fills 50 MJ in 125 s
        Assert.Equal(station.Stats.EnergyCapacityKJ, station.StoredEnergyKJ, 3);
        var burned = 1_000 - station.Inventory["fuel"];
        var gen = station.Stats.GeneratorList[0];
        // Just enough batches for the battery (plus at most one batch kept in the generator's store).
        Assert.InRange(burned, (long)(station.Stats.EnergyCapacityKJ / gen.KJPerBatch), (long)(station.Stats.EnergyCapacityKJ / gen.KJPerBatch) + 1);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg); // the burned fuel is exhaust (ejected mass)

        var fuelNow = station.Inventory["fuel"];
        for (var i = 0; i < 60 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(fuelNow, station.Inventory["fuel"]); // nothing needs power: nothing burned
    }

    [Fact]
    public void A_generator_never_gives_more_than_its_rating_per_tick()
    {
        var (sector, station) = StationWithGenerator();
        sector.DebugAddStock(station.Id, "fuel", 1_000);
        station.StoredEnergyKJ = 0;
        sector.Step();
        Assert.Equal(station.Stats.GeneratorKW * FixedStepClock.TickSeconds, station.StoredEnergyKJ, 6);
    }

    [Fact]
    public void Without_fuel_a_generator_gives_nothing()
    {
        var (sector, station) = StationWithGenerator();
        station.StoredEnergyKJ = 0;
        for (var i = 0; i < 10 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(0, station.StoredEnergyKJ);
    }

    [Fact]
    public void Solar_comes_first_and_the_generator_covers_only_the_rest()
    {
        var (sector, station) = StationWithGenerator(solar: true);
        sector.DebugAddStock(station.Id, "fuel", 1_000);
        station.StoredEnergyKJ = station.Stats.EnergyCapacityKJ; // full: solar alone keeps it full
        for (var i = 0; i < 30 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(1_000, station.Inventory["fuel"]);
    }

    [Fact]
    public void A_station_generator_powers_a_docked_ships_needs()
    {
        var (sector, station) = StationWithGenerator();
        sector.DebugAddStock(station.Id, "fuel", 5_000);
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), "S", design, design.ComputeStats(Catalog), station.Position, 0);
        sector.AddCraft(ship);
        ship.Position = sector.BerthPosition(station, 0, ship.CollisionRadiusM, station.Position);
        ship.Velocity = station.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        ship.StoredEnergyKJ = 0;
        station.StoredEnergyKJ = 0;

        for (var i = 0; i < 240 * TicksPerSecond; i++) sector.Step();
        Assert.True(ship.StoredEnergyKJ > 0.9 * ship.Stats.EnergyCapacityKJ); // charged by the station's generator (and its own solar)
        Assert.True(station.Inventory["fuel"] < 5_000);
    }

    [Fact]
    public void A_generators_store_survives_a_save_exactly()
    {
        var (sector, station) = StationWithGenerator();
        sector.DebugAddStock(station.Id, "fuel", 1_000);
        station.StoredEnergyKJ = 0;
        for (var i = 0; i < 7; i++) sector.Step();
        Assert.True(station.GeneratorBankTotalKJ > 0); // part of a batch is left over
        var player = sector.PilotCraftId;
        var json = SaveGameSerializer.ToJson(sector, player);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(station.GeneratorBankTotalKJ, loaded.GetCraft(station.Id).GeneratorBankTotalKJ);
        for (var i = 0; i < 600; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(loaded, player));
    }
}
