using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Native animals (#93) and ecosystems (#94).</summary>
public sealed class AnimalTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    private static ShipDesign With(params string[] extra)
    {
        var cells = TestData.StarterShip().Cells.Where(c => c.PartId != "drill").ToList();
        for (var i = 0; i < extra.Length; i++) cells.Add(new DesignCell(new GridPos(2 + i, 0), extra[i]));
        return new ShipDesign("a", "A", cells);
    }

    /// <summary>Cinder, fully grown with a herd at 60% everywhere, and a ship anchored on it.</summary>
    private static (SectorState Sector, CelestialBodyState World, CraftState Ship, SurfaceSite Site) Game(AnimalTemper temper, params string[] extra)
    {
        var sector = TestData.NewGame().Sector;
        var world = sector.Bodies.First(b => b.Name == "Cinder");
        sector.DebugAddLife(world.Id, FloraHazard.None);
        sector.DebugAddAnimals(world.Id, temper);
        foreach (var s in world.SiteList) { s.Flora = 1; s.Fauna = 0.6; }
        world.HasAnimals = true;
        var ship = Land(sector, world, With(extra));
        sector.PilotCraftId = CraftId.None;
        return (sector, world, ship, sector.SiteUnder(ship)!);
    }

    private static CraftState Land(SectorState sector, CelestialBodyState world, ShipDesign design)
    {
        var ship = new CraftState(sector.AllocateCraftId(), "Ship", design, design.ComputeStats(Catalog), Vector2D.Zero, 0);
        sector.AddCraft(ship);
        TestData.LandAt(sector, ship, world);
        Run(sector, 20);
        var anchored = sector.TryAnchor(ship.Id);
        Assert.True(anchored.Done, anchored.Message);
        return ship;
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    [Fact]
    public void Herds_grow_towards_what_the_plants_feed_and_both_settle()
    {
        var (sector, world, _, _) = Game(AnimalTemper.Harmless);
        foreach (var s in world.SiteList) s.Fauna = 0.1;
        Run(sector, 240 * Minute);
        Assert.All(world.Sites, s => Assert.InRange(s.Fauna, 0.5, 1));
        Assert.All(world.Sites, s => Assert.InRange(s.Flora, 0.6, 1)); // grazed but alive
        Assert.Equal(LifeHealth.Thriving, SectorState.HealthOf(world));
    }

    [Fact]
    public void Without_plants_animals_starve_and_die_out()
    {
        var (sector, world, _, _) = Game(AnimalTemper.Harmless);
        foreach (var s in world.SiteList) s.Flora = 0;
        world.SiteList[0].Flora = 0.02; // the world itself still lives
        Run(sector, 120 * Minute, () => !world.HasAnimals || !world.HasLife);
        Assert.False(world.HasAnimals);
        Assert.All(world.Sites, s => Assert.Equal(0, s.Fauna));
    }

    [Fact]
    public void Animals_drift_to_where_there_is_more_to_eat()
    {
        var (sector, world, _, _) = Game(AnimalTemper.Harmless);
        foreach (var s in world.SiteList) s.Fauna = 0;
        world.SiteList[0].Fauna = 0.8;
        Run(sector, 30 * Minute);
        Assert.True(world.SiteList[1].Fauna > 0.01);
        Assert.True(world.SiteList[^1].Fauna > 0.01);
    }

    [Fact]
    public void A_repeller_clears_its_site_and_the_animals_go_next_door()
    {
        var (sector, world, ship, site) = Game(AnimalTemper.Harmless, "repeller");
        var total = world.Sites.Sum(s => s.Fauna);
        Run(sector, 15 * Minute);
        Assert.True(ship.RepellerOn);
        Assert.True(site.Fauna < 0.05, $"{site.Fauna}");
        Assert.True(world.Sites.Sum(s => s.Fauna) > 0.9 * total);
        // Without power it stops.
        var (dark, _, unpowered, _) = Game(AnimalTemper.Harmless, "repeller");
        unpowered.Design = new ShipDesign("n", "N", unpowered.Design.Cells.Where(c => c.PartId != "solar_panel"));
        unpowered.Stats = unpowered.Design.ComputeStats(Catalog);
        unpowered.StoredEnergyKJ = 0;
        Run(dark, 40);
        Assert.False(unpowered.RepellerOn);
        Assert.Contains("no power", unpowered.AnimalStatus);
    }

    [Fact]
    public void A_ranch_makes_food_without_killing_off_the_herd()
    {
        var (sector, world, ship, site) = Game(AnimalTemper.Harmless, "ranch");
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, 60 * Minute);
        Assert.True(ship.Inventory["food"] > 2_000, $"{ship.Inventory["food"]}");
        Assert.True(site.Fauna > 0.95 * SectorState.RanchLeaves * site.Flora);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        site.Fauna = 0.3;
        Run(sector, 40);
        Assert.Contains("needs to grow", ship.AnimalStatus);
    }

    [Fact]
    public void Culling_gives_biomass_is_resented_and_needs_weapons_and_the_law()
    {
        var (sector, world, ship, site) = Game(AnimalTemper.Harmless, "laser");
        var faction = sector.MeetCivilization(sector.Stars[0], cultureId: "conservationist", baseline: 0);
        var indifferent = sector.MeetCivilization(sector.Stars[0], cultureId: "militarist", baseline: 0);
        var mass = sector.ComputeMassLedger().TotalKg;
        Assert.True(sector.SetCulling(ship.Id, true).Done);
        Run(sector, 2 * Minute);
        Assert.True(site.Fauna < 0.5);
        Assert.True(ship.Inventory["biomass"] > 1_000);
        Assert.True(faction.Grievance > indifferent.Grievance);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        sector.SetLaw(Laws.ProtectLife, true);
        Run(sector, 2);
        Assert.False(ship.Culling);
        Assert.Contains("Protect native life", sector.SetCulling(ship.Id, true).Message);
        var (other, _, unarmed, _) = Game(AnimalTemper.Harmless);
        Assert.Contains("no weapons", other.SetCulling(unarmed.Id, true).Message);
    }

    [Fact]
    public void Pens_carry_animals_to_another_world_with_exact_mass()
    {
        var (sector, world, ship, site) = Game(AnimalTemper.Crowding, "animal_pen");
        var mass = sector.ComputeMassLedger().TotalKg;
        var before = site.Fauna;
        Assert.True(sector.LoadAnimals(ship.Id).Done);
        Assert.Equal(5_000, ship.PenKg);
        Assert.True(site.Fauna < before);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        Assert.False(sector.LoadAnimals(ship.Id).Done); // full

        var other = sector.Bodies.First(b => b.Name == "Moon");
        Assert.Contains("Nothing grows", Move(sector, ship, other, () => sector.ReleaseAnimals(ship.Id)).Message);
        sector.DebugAddLife(other.Id, FloraHazard.None);
        other.HasAnimals = false;
        foreach (var s in other.SiteList) { s.Flora = 0.9; s.Fauna = 0; }
        var released = sector.ReleaseAnimals(ship.Id);
        Assert.True(released.Done, released.Message);
        Assert.Equal(0, ship.PenKg);
        Assert.True(other.HasAnimals);
        Assert.Equal(world.AnimalName, other.AnimalName);
        Assert.Equal(AnimalTemper.Crowding, other.AnimalTemper);
        Run(sector, Minute);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    private static ActionResult Move(SectorState sector, CraftState ship, CelestialBodyState to, Func<ActionResult> then)
    {
        sector.TryRelease(ship.Id);
        TestData.LandAt(sector, ship, to);
        Run(sector, 20);
        Assert.True(sector.TryAnchor(ship.Id).Done);
        return then();
    }

    [Fact]
    public void Two_kinds_dont_share_a_world()
    {
        var (sector, _, ship, _) = Game(AnimalTemper.Harmless, "animal_pen");
        sector.LoadAnimals(ship.Id);
        ship.PenAnimal = "Stranger";
        Assert.Contains("won't share", sector.ReleaseAnimals(ship.Id).Message);
    }

    [Fact]
    public void Animals_damage_machines_only_when_they_have_reason_to()
    {
        double Worn(AnimalTemper temper, double flora, double fauna)
        {
            var (sector, world, ship, _) = Game(temper);
            for (var i = 0; i < 20; i++)
            {
                foreach (var s in world.SiteList) { s.Flora = flora; s.Fauna = fauna; }
                Run(sector, 10 * Second);
            }
            return ship.PartHealth.Values.Sum(h => 1 - h);
        }
        Assert.Equal(0, Worn(AnimalTemper.Harmless, 0.2, 0.2));
        Assert.Equal(0, Worn(AnimalTemper.Crowding, 1, 0.5));
        Assert.True(Worn(AnimalTemper.Crowding, 1, 0.95) > 0);
        Assert.Equal(0, Worn(AnimalTemper.Hungry, 1, 0.95));
        Assert.True(Worn(AnimalTemper.Hungry, 0.2, 0.15) > 0);
    }

    [Fact]
    public void Thriving_worlds_regrow_faster_than_stressed_ones()
    {
        double Growth(double others)
        {
            var (sector, world, _, _) = Game(AnimalTemper.Harmless);
            world.HasAnimals = false;
            foreach (var s in world.SiteList) { s.Fauna = 0; s.Flora = others; }
            world.SiteList[3].Flora = 0.2;
            world.SiteList[2].Flora = world.SiteList[4].Flora = 0.2; // same neighbours either way
            Run(sector, 10 * Minute);
            return world.SiteList[3].Flora - 0.2;
        }
        Assert.True(Growth(1) > 1.8 * Growth(0.2));
    }

    [Fact]
    public void Those_who_care_for_life_judge_you_by_the_worlds_you_found()
    {
        var (sector, world, _, _) = Game(AnimalTemper.Harmless);
        var caring = sector.MeetCivilization(sector.Stars[0], cultureId: "conservationist", baseline: 0);
        var indifferent = sector.MeetCivilization(sector.Stars[0], cultureId: "militarist", baseline: 0);
        Run(sector, 15 * Second);
        Assert.True(world.LifeFound);
        Assert.Equal(SectorState.ThrivingOpinion, caring.LifeOpinion);
        Assert.Equal(0, indifferent.LifeOpinion);
        foreach (var s in world.SiteList) s.Flora = 0;
        Run(sector, 15 * Second);
        Assert.Equal(SectorState.LostOpinion, caring.LifeOpinion);
        Assert.False(world.HasAnimals); // nothing left to eat
        Assert.True(caring.TargetAttitude(sector.ActiveLaws) < indifferent.TargetAttitude(sector.ActiveLaws));
    }

    [Fact]
    public void Animals_survive_a_save_exactly()
    {
        var (sector, world, ship, _) = Game(AnimalTemper.Crowding, "animal_pen", "laser");
        sector.LoadAnimals(ship.Id);
        sector.SetCulling(ship.Id, true);
        sector.MeetCivilization(sector.Stars[0], cultureId: "conservationist", baseline: 0);
        Run(sector, Minute + 7);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Run(sector, 3 * Minute);
        Run(loaded, 3 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }
}
