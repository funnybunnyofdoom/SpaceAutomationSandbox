using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#76 large solar arrays; #77 stellar heat collectors; #78 methane; #79 fusion; #80 nuclear-thermal rockets.</summary>
public sealed class EnergyPassThreeTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;

    private static CraftState AddStation(SectorState sector, Vector2D at, params (int X, int Y, string Part)[] cells)
    {
        var design = new ShipDesign("s", "S", cells.Select(c => new DesignCell(new GridPos(c.X, c.Y), c.Part)));
        var craft = new CraftState(sector.AllocateCraftId(), "S", design, design.ComputeStats(Catalog), at, 0);
        sector.AddCraft(craft);
        return craft;
    }

    [Fact]
    public void Large_solar_arrays_are_for_stations_only()
    {
        var ship = new ShipDesign("a", "A", new[]
        {
            new DesignCell(new GridPos(0, 0), "cockpit"), new DesignCell(new GridPos(0, 1), "thruster"), new DesignCell(new GridPos(1, 0), "solar_array"),
        });
        Assert.Contains(ship.Validate(Catalog, DesignRole.Ship), e => e.Contains("stations only"));
        var station = new ShipDesign("b", "B", new[] { new DesignCell(new GridPos(0, 0), "docking_port"), new DesignCell(new GridPos(1, 0), "solar_array") });
        Assert.Empty(station.Validate(Catalog, DesignRole.Station));
        Assert.True(Catalog.Get("solar_array").PowerGenerationKW / Catalog.Get("solar_array").MassKg
                    > Catalog.Get("solar_panel_mk3").PowerGenerationKW / Catalog.Get("solar_panel_mk3").MassKg); // more per tonne
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 0.0)]
    [InlineData(3.0, 0.5)]
    [InlineData(4.0, 1.0)]
    public void A_heat_collector_only_works_close_to_its_star(double sunlight, double share) =>
        Assert.Equal(share, SectorState.StellarHeatShare(sunlight), 6);

    [Fact]
    public void A_heat_collector_near_Cinder_gives_full_output_and_none_at_Home()
    {
        var sector = TestData.NewGame().Sector;
        var cinder = sector.Bodies.Single(b => b.Name == "Cinder");
        var near = AddStation(sector, sector.GetPosition(cinder.Id) + new Vector2D(0, -(cinder.RadiusM + 20_000)), (0, 0, "heat_collector"), (1, 0, "battery"));
        var far = AddStation(sector, TestData.SunnySideOfHome(sector, 20_000), (0, 0, "heat_collector"), (1, 0, "battery"));
        near.StoredEnergyKJ = far.StoredEnergyKJ = 0;
        sector.Step();
        Assert.True(near.StellarHeatKJThisTick > 0);
        Assert.Equal(0, far.StellarHeatKJThisTick);
    }

    [Fact]
    public void Methane_ice_is_on_the_icy_bodies_and_melts_into_methane()
    {
        var sector = TestData.NewGame(surveyAll: false).Sector;
        Assert.True(sector.Bodies.Single(b => b.Name == "Rime").DepositsKg.ContainsKey("methane_ice"));
        Assert.True(sector.Bodies.Single(b => b.Name == "Giant-b").DepositsKg.ContainsKey("methane_ice"));
        Assert.True(Catalog.Recipes.TryGet("melt_methane_ice", out var recipe));
        Assert.Equal(recipe.InputsKg!.Values.Sum(), recipe.OutputsKg!.Values.Sum());
    }

    [Fact]
    public void A_methane_generator_burns_methane_and_oxygen_with_mass_exact()
    {
        var sector = TestData.NewGame().Sector;
        var station = AddStation(sector, TestData.SunnySideOfHome(sector, 200_000), (0, 0, "methane_generator"), (1, 0, "battery"), (-1, 0, "storage_silo"));
        sector.DebugAddStock(station.Id, "methane", 100);
        sector.DebugAddStock(station.Id, "oxygen", 400);
        station.StoredEnergyKJ = 0;
        var mass = sector.ComputeMassLedger().TotalKg;
        for (var i = 0; i < 120 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(station.Stats.EnergyCapacityKJ, station.StoredEnergyKJ, 3);
        Assert.Equal(4 * (100 - station.Inventory["methane"]), 400 - station.Inventory["oxygen"]);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void An_isotope_separator_pulls_deuterium_from_water_with_mass_exact()
    {
        var sector = TestData.NewGame().Sector;
        var station = AddStation(sector, TestData.SunnySideOfHome(sector, 200_000), // beyond Home's gravity: it doesn't fall in
            (0, 0, "isotope_separator"), (1, 0, "solar_array"), (2, 0, "solar_array"), (0, 1, "battery"), (-1, 0, "storage_silo"));
        sector.DebugAddStock(station.Id, "water", 1_000);
        var mass = sector.ComputeMassLedger().TotalKg;
        for (var i = 0; i < 1_400 * TicksPerSecond; i++) sector.Step(); // 500 MJ a batch at 800 kW: about 625 s
        Assert.True(station.Inventory["deuterium"] >= 1);
        Assert.Equal(1_000, station.Inventory["water"] + station.Inventory["deuterium"]);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void A_fusion_reactor_runs_a_long_time_on_a_kilogram_and_needs_fusion_power()
    {
        var sector = TestData.NewGame().Sector;
        Assert.False(sector.IsPartUnlocked("fusion_reactor"));
        Assert.False(sector.IsPartUnlocked("isotope_separator"));
        var station = AddStation(sector, TestData.SunnySideOfHome(sector, 200_000), (0, 0, "fusion_reactor"), (1, 0, "battery_mk2"), (-1, 0, "storage_silo"));
        sector.DebugAddStock(station.Id, "deuterium", 2);
        station.StoredEnergyKJ = 0;
        for (var i = 0; i < 60 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(station.Stats.EnergyCapacityKJ, station.StoredEnergyKJ, 3);
        Assert.Equal(1, station.Inventory["deuterium"]); // one kilogram holds far more than a battery
        var gen = station.Stats.GeneratorList.Single();
        Assert.True(gen.KJPerBatch / (gen.KW * 3600) > 1); // over an hour at full output per kg
    }

    [Fact]
    public void Nuclear_thermal_and_methane_rockets_are_drives_with_their_own_propellant()
    {
        var sector = TestData.NewGame().Sector;
        Assert.False(sector.IsPartUnlocked("nuclear_rocket")); // with Nuclear physics
        Assert.True(sector.IsPartUnlocked("methalox_rocket"));
        Assert.Contains("fuel_rods", Catalog.Get("nuclear_rocket").PropellantKgPerS!.Keys);
        Assert.Contains("methane", Catalog.Get("methalox_rocket").PropellantKgPerS!.Keys);

        var design = new ShipDesign("n", "N", new[]
        {
            new DesignCell(new GridPos(0, -1), "cockpit"), new DesignCell(new GridPos(0, 0), "storage_silo"), new DesignCell(new GridPos(0, 1), "nuclear_rocket"),
            new DesignCell(new GridPos(1, 0), "battery_mk2"),
        });
        var ship = new CraftState(sector.AllocateCraftId(), "N", design, design.ComputeStats(Catalog), TestData.SunnySideOfHome(sector, 200_000), 0);
        sector.AddCraft(ship);
        sector.DebugAddStock(ship.Id, "hydrogen", 10);
        sector.DebugAddStock(ship.Id, "fuel_rods", 1);
        var mass = sector.ComputeMassLedger().TotalKg;
        ship.Controls = new ShipControls(1, 0, false);
        for (var i = 0; i < 60 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(7, ship.Inventory["hydrogen"]); // 0.05 kg/s for a minute
        Assert.Equal(1, ship.Inventory["fuel_rods"]); // a rod lasts far longer
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }
}
