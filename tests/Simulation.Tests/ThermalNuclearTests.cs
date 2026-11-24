using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#77 geothermal taps (hot sites, bodies near their star); #79 uranium, fuel rods, reactors and spent fuel.</summary>
public sealed class ThermalNuclearTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;

    private static CelestialBodyState Body(SectorState sector, string name) => sector.Bodies.Single(b => b.Name == name);

    /// <summary>A small tap station (clamp side down) landed and anchored on a body at a bearing.</summary>
    private static CraftState TapStation(SectorState sector, CelestialBodyState body, double bearing)
    {
        var design = new ShipDesign("tap", "Tap", new[]
        {
            new DesignCell(new GridPos(0, 0), "geothermal_tap"),
            new DesignCell(new GridPos(1, 0), "battery"),
            new DesignCell(new GridPos(0, 1), "anchor_clamp"),
        });
        var craft = new CraftState(sector.AllocateCraftId(), "Tap", design, design.ComputeStats(Catalog), sector.GetPosition(body.Id), 0);
        sector.AddCraft(craft);
        var offset = new Vector2D(Math.Cos(bearing), Math.Sin(bearing)) * (body.RadiusM + craft.CollisionRadiusM + 1);
        craft.Position = craft.PreviousPosition = sector.GetPosition(body.Id) + offset;
        craft.HeadingRad = craft.PreviousHeadingRad = FlightModel.WrapAngle(bearing + Math.PI / 2);
        craft.Velocity = sector.GetVelocityAt(body.Id, sector.TimeS);
        sector.RestoreLanding(craft.Id, body.Id, offset);
        Assert.True(sector.TryAnchor(craft.Id).Done);
        craft.StoredEnergyKJ = 0;
        return craft;
    }

    [Fact]
    public void Planets_and_moons_have_hot_spots_asteroids_dont_and_the_same_every_time()
    {
        var a = TestData.NewGame(surveyAll: false).Sector;
        var b = TestData.NewGame(surveyAll: false).Sector;
        foreach (var body in a.Bodies.Where(x => x.IsMineable))
        {
            var twin = b.Bodies.Single(x => x.Id == body.Id);
            Assert.Equal(body.Sites.Select(s => s.IsHot), twin.Sites.Select(s => s.IsHot));
            if (body.Kind == BodyKind.Asteroid) Assert.DoesNotContain(body.Sites, s => s.IsHot);
            if (body.Kind == BodyKind.Planet) Assert.Contains(body.Sites, s => s.IsHot);
        }
    }

    [Fact]
    public void A_tap_on_a_hot_spot_gives_power_and_one_on_a_cold_site_doesnt()
    {
        var sector = TestData.NewGame().Sector;
        var rime = Body(sector, "Rime"); // a far, cold planet (Home has no surface sites: it isn't mineable)
        Assert.False(sector.IsHotBody(rime));
        var hot = rime.Sites.First(s => s.IsHot);
        var cold = rime.Sites.First(s => !s.IsHot);
        var onHot = TapStation(sector, rime, hot.CentreRad);
        var onCold = TapStation(sector, rime, cold.CentreRad);
        for (var i = 0; i < 10 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(10 * onHot.Stats.GeothermalKW, onHot.StoredEnergyKJ, 3);
        Assert.Equal(0, onCold.StoredEnergyKJ);
        Assert.Contains("hot spot", sector.GeothermalHeat(onCold));
        Assert.Null(sector.GeothermalHeat(onHot));
    }

    [Fact]
    public void Near_its_star_a_body_is_hot_all_over()
    {
        var sector = TestData.NewGame().Sector;
        var cinder = Body(sector, "Cinder");
        Assert.True(sector.IsHotBody(cinder));
        var cold = cinder.Sites.FirstOrDefault(s => !s.IsHot) ?? cinder.Sites[0];
        var tap = TapStation(sector, cinder, cold.CentreRad);
        for (var i = 0; i < 5 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(5 * tap.Stats.GeothermalKW, tap.StoredEnergyKJ, 3);
    }

    [Fact]
    public void A_tap_in_flight_gives_nothing()
    {
        var sector = TestData.NewGame().Sector;
        var rime = Body(sector, "Rime");
        var tap = TapStation(sector, rime, rime.Sites.First(s => s.IsHot).CentreRad);
        Assert.True(sector.TryRelease(tap.Id).Done);
        Assert.Contains("anchored", sector.GeothermalHeat(tap)!);
    }

    [Fact]
    public void Uranium_is_on_Cinder_at_home_and_the_reactor_needs_nuclear_physics()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        Assert.Equal(SectorGenerator.UraniumAtHomeKg, Body(sector, "Cinder").DepositsKg["uranium_ore"]);
        Assert.Single(sector.Bodies, b => b.DepositsKg.ContainsKey("uranium_ore"));
        Assert.False(sector.IsPartUnlocked("reactor"));
        Assert.True(sector.IsPartUnlocked("geothermal_tap"));
        Assert.Contains("reactor", Catalog.Research.Techs.Single(t => t.Id == "nuclear_physics").Parts!);
    }

    [Fact]
    public void Fuel_rods_are_refined_from_uranium_ore()
    {
        Assert.True(Catalog.Recipes.TryGet("make_fuel_rods", out var recipe));
        Assert.Equal(1000, recipe.InputsKg!["uranium_ore"]);
        Assert.Equal(recipe.InputsKg.Values.Sum(), recipe.OutputsKg!.Values.Sum());
    }

    [Fact]
    public void A_reactor_turns_fuel_rods_into_spent_fuel_which_an_ejector_throws_out_with_mass_exact()
    {
        var sector = TestData.NewGame().Sector;
        var design = new ShipDesign("plant", "Plant", new[]
        {
            new DesignCell(new GridPos(0, 0), "reactor"), new DesignCell(new GridPos(1, 0), "battery"),
            new DesignCell(new GridPos(-1, 0), "storage_silo"), new DesignCell(new GridPos(0, 1), "slag_ejector"),
        });
        var plant = new CraftState(sector.AllocateCraftId(), "Plant", design, design.ComputeStats(Catalog), TestData.SunnySideOfHome(sector, 20_000), 0);
        sector.AddCraft(plant);
        sector.DebugAddStock(plant.Id, "fuel_rods", 5);
        plant.StoredEnergyKJ = 0;
        var mass = sector.ComputeMassLedger().TotalKg;

        for (var i = 0; i < 30 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(plant.Stats.EnergyCapacityKJ, plant.StoredEnergyKJ, 3);
        Assert.Equal(4, plant.Inventory["fuel_rods"]); // one rod holds an hour at 1 MW: plenty for one battery
        Assert.Equal(0, plant.Inventory["spent_fuel"]); // the ejector threw it out
        Assert.Equal(1, sector.EjectedKg["spent_fuel"]);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Without_an_ejector_spent_fuel_piles_up()
    {
        var sector = TestData.NewGame().Sector;
        var design = new ShipDesign("plant", "Plant", new[]
        {
            new DesignCell(new GridPos(0, 0), "reactor"), new DesignCell(new GridPos(1, 0), "battery"), new DesignCell(new GridPos(-1, 0), "storage_silo"),
        });
        var plant = new CraftState(sector.AllocateCraftId(), "Plant", design, design.ComputeStats(Catalog), TestData.SunnySideOfHome(sector, 20_000), 0);
        sector.AddCraft(plant);
        sector.DebugAddStock(plant.Id, "fuel_rods", 5);
        plant.StoredEnergyKJ = 0;
        for (var i = 0; i < 30 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(1, plant.Inventory["spent_fuel"]);
    }
}
