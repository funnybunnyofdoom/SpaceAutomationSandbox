using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#76 solar shadow; #78 electrolysers, fuel cells and automatic fuel requests.</summary>
public sealed class FuelChainTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;

    private static CraftState Station(SectorState sector, Vector2D at, params (int X, int Y, string Part)[] cells)
    {
        var design = new ShipDesign("test_station", "Test station", cells.Select(c => new DesignCell(new GridPos(c.X, c.Y), c.Part)));
        var station = new CraftState(sector.AllocateCraftId(), "Test", design, design.ComputeStats(Catalog), at, 0);
        sector.AddCraft(station);
        // Parked: held on a long orbit around Home so gravity doesn't pull it about.
        var home = SectorGenerator.HomePlanet(sector);
        var offset = at - sector.GetPosition(home.Id);
        var period = 1_000_000.0;
        sector.PutOnRails(station.Id, new CircularOrbit(home.Id, offset.Length, period, Math.Atan2(offset.Y, offset.X) - 2 * Math.PI * sector.TimeS / period));
        return station;
    }

    [Fact]
    public void A_body_between_the_star_and_a_panel_dims_its_sunlight()
    {
        var sector = TestData.NewGame().Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var centre = sector.GetPosition(home.Id);
        var toStar = sector.GetPosition(sector.Stars[0].Id) - centre;
        var night = centre - toStar * ((home.RadiusM + 2_000) / toStar.Length); // behind Home
        Assert.True(sector.InShadowAt(night));
        Assert.False(sector.InShadowAt(TestData.SunnySideOfHome(sector, 2_000)));

        var dark = Station(sector, night, (0, 0, "battery"), (1, 0, "solar_panel"));
        var lit = Station(sector, TestData.SunnySideOfHome(sector, 2_000), (0, 0, "battery"), (1, 0, "solar_panel"));
        dark.StoredEnergyKJ = lit.StoredEnergyKJ = 0;
        for (var i = 0; i < 10 * TicksPerSecond; i++) sector.Step();
        Assert.True(dark.InShadow);
        Assert.False(lit.InShadow);
        Assert.Equal(lit.StoredEnergyKJ * SectorState.ShadowLight, dark.StoredEnergyKJ, 3); // dimmed, not dark
    }

    [Fact]
    public void An_electrolyser_splits_spare_water_on_spare_power_only_with_mass_exact()
    {
        var sector = TestData.NewGame().Sector;
        var station = Station(sector, TestData.SunnySideOfHome(sector, 3_000),
            (0, 0, "electrolyser"), (1, 0, "solar_panel_mk3"), (2, 0, "solar_panel_mk3"), (0, 1, "battery"), (-1, 0, "storage_silo"), (0, -1, "docking_port"));
        sector.DebugAddStock(station.Id, "water", 900);
        var mass = sector.ComputeMassLedger().TotalKg;

        for (var i = 0; i < 700 * TicksPerSecond; i++) sector.Step(); // 90 MJ a batch at 300 kW: 300 s each
        var h = station.Inventory["hydrogen"];
        Assert.True(h > 0);
        Assert.Equal(8 * h, station.Inventory["oxygen"]);
        Assert.Equal(900 - 9 * h, station.Inventory["water"]);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void An_electrolyser_never_burns_generator_fuel_or_runs_on_a_low_battery()
    {
        var sector = TestData.NewGame().Sector;
        var station = Station(sector, TestData.SunnySideOfHome(sector, 3_000),
            (0, 0, "electrolyser"), (1, 0, "fuel_generator"), (0, 1, "battery"), (-1, 0, "storage_silo"));
        sector.DebugAddStock(station.Id, "water", 900);
        sector.DebugAddStock(station.Id, "fuel", 500);
        station.StoredEnergyKJ = 0;
        for (var i = 0; i < 300 * TicksPerSecond; i++) sector.Step();
        // The generator filled the battery (demand), but nothing above 90% was spare: no splitting, no fuel for it.
        Assert.Equal(0, station.Inventory["hydrogen"]);
        Assert.True(station.Inventory["fuel"] > 490 - (long)(station.Stats.EnergyCapacityKJ / 10_000) - 1);
    }

    [Fact]
    public void An_electrolyser_leaves_the_water_its_station_wants()
    {
        // A station's wanted water (its request, or its crew's automatic one) is never split.
        var sector = TestData.NewGame().Sector;
        var station = Station(sector, TestData.SunnySideOfHome(sector, 3_000),
            (0, 0, "electrolyser"), (1, 0, "solar_panel_mk3"), (2, 0, "solar_panel_mk3"), (0, 1, "battery"), (-1, 0, "storage_silo"), (0, -1, "docking_port"));
        sector.DebugAddStock(station.Id, "water", 900);
        Assert.True(sector.SetRequest(station.Id, "water", 900).Done);
        for (var i = 0; i < 400 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(0, station.Inventory["hydrogen"]);
        Assert.Equal(900, station.Inventory["water"]);

        sector.DebugAddStock(station.Id, "water", 9); // one batch more than it wants
        for (var i = 0; i < 700 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(1, station.Inventory["hydrogen"]);
        Assert.Equal(900, station.Inventory["water"]);
    }

    [Fact]
    public void A_fuel_cell_burns_hydrogen_and_oxygen_and_gives_the_water_back()
    {
        var sector = TestData.NewGame().Sector;
        var station = Station(sector, TestData.SunnySideOfHome(sector, 3_000), (0, 0, "fuel_cell"), (1, 0, "battery"), (-1, 0, "storage_silo"));
        sector.DebugAddStock(station.Id, "hydrogen", 10);
        sector.DebugAddStock(station.Id, "oxygen", 80);
        station.StoredEnergyKJ = 0;
        var mass = sector.ComputeMassLedger().TotalKg;
        for (var i = 0; i < 300 * TicksPerSecond; i++) sector.Step();
        Assert.True(station.StoredEnergyKJ > 0);
        var burned = 10 - station.Inventory["hydrogen"];
        Assert.True(burned > 0);
        Assert.Equal(9 * burned, station.Inventory["water"]); // nothing ejected: the water stays
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Stations_with_generators_ask_for_twelve_hours_of_fuel()
    {
        var sector = TestData.NewGame().Sector;
        var station = Station(sector, TestData.SunnySideOfHome(sector, 3_000), (0, 0, "fuel_generator"), (1, 0, "battery"), (0, -1, "docking_port"));
        var gen = station.Stats.GeneratorList[0];
        var expected = (long)Math.Ceiling(gen.KW * SectorState.AutoFuelHours * 3600 / gen.KJPerBatch * gen.BurnsKg["fuel"]);
        Assert.Equal(expected, SectorState.AutoSupplyKg(station, "fuel"));
        Assert.Equal(expected, SectorState.WantedKg(station, "fuel"));
        Assert.Equal(0, SectorState.AutoSupplyKg(station, "water")); // uncrewed: no food or water
    }

    [Fact]
    public void Converter_progress_survives_a_save_exactly()
    {
        var sector = TestData.NewGame().Sector;
        var station = Station(sector, TestData.SunnySideOfHome(sector, 3_000),
            (0, 0, "electrolyser"), (1, 0, "solar_panel_mk3"), (2, 0, "solar_panel_mk3"), (0, 1, "battery"), (-1, 0, "storage_silo"));
        sector.DebugAddStock(station.Id, "water", 900);
        for (var i = 0; i < 37; i++) sector.Step();
        var player = sector.PilotCraftId;
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, player), Catalog).Sector;
        for (var i = 0; i < 600; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(loaded, player));
    }
}
