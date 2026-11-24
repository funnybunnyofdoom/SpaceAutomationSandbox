using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Planetary life (#91) and native plants (#92).</summary>
public sealed class LifeTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    private static ShipDesign HarvesterShip(bool drillToo = false)
    {
        var cells = TestData.StarterShip().Cells.Select(c => c.PartId == "drill" ? c with { PartId = "harvester" } : c).ToList();
        if (drillToo) cells.Add(new DesignCell(new GridPos(1, -3), "drill"));
        return new ShipDesign("h", "Harvester ship", cells);
    }

    /// <summary>Cinder with life, fully covered, and a ship anchored on a site of it.</summary>
    private static (SectorState Sector, CelestialBodyState World, CraftState Ship, SurfaceSite Site) Game(ShipDesign? design = null, FloraHazard hazard = FloraHazard.None)
    {
        var sector = TestData.NewGame().Sector;
        var world = sector.Bodies.First(b => b.Name == "Cinder");
        sector.DebugAddLife(world.Id, hazard);
        foreach (var s in world.SiteList) s.Flora = 1;
        design ??= HarvesterShip();
        var ship = new CraftState(sector.AllocateCraftId(), "Ship", design, design.ComputeStats(Catalog), Vector2D.Zero, 0);
        sector.AddCraft(ship);
        TestData.LandAt(sector, ship, world);
        Run(sector, 20);
        var anchored = sector.TryAnchor(ship.Id);
        Assert.True(anchored.Done, anchored.Message);
        sector.PilotCraftId = CraftId.None;
        return (sector, world, ship, sector.SiteUnder(ship)!);
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    [Fact]
    public void Life_is_rare_temperate_rocky_and_never_at_home()
    {
        int living = 0, temperate = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var sector = new SectorState(seed);
            var home = SectorGenerator.Generate(seed);
            Assert.DoesNotContain(home.Bodies, b => b.HasLife);
            for (var i = 1; i <= 8; i++)
            {
                var (star, _) = SectorGenerator.GenerateSystem(home, i);
                foreach (var b in home.Bodies.Where(b => b.Orbit?.Parent == star.Id && b.Kind == BodyKind.Planet))
                {
                    var light = star.SunlightFactor * Math.Pow(SectorGenerator.HomeOrbitRadiusM / b.Orbit!.RadiusM, 2);
                    var suits = b.BulkMaterialId == "rock" && light is >= SectorState.TemperateMin and <= SectorState.TemperateMax;
                    if (suits) temperate++;
                    if (!b.HasLife) continue;
                    Assert.True(suits);
                    Assert.Contains(b.Sites, s => s.Flora > 0);
                    living++;
                }
            }
        }
        Assert.True(temperate > 150);
        Assert.InRange(living / (double)temperate, 0.06, 0.2);
    }

    [Fact]
    public void Harvesting_takes_biomass_from_the_bodys_own_mass_and_thins_the_plants()
    {
        var (sector, world, ship, site) = Game();
        var mass = sector.ComputeMassLedger().TotalKg;
        var body = world.WholeMassKg;
        Run(sector, Minute);
        Assert.True(ship.Inventory["biomass"] > 1_000);
        Assert.Equal(body - ship.Inventory["biomass"], world.WholeMassKg);
        Assert.InRange(site.Flora, 0.9, 0.999);
        Assert.Empty(world.Craters); // it cuts plants, it doesn't dig
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Plants_grow_back_and_spread_to_neighbours()
    {
        var (sector, world, ship, _) = Game(TestData.StarterShip());
        sector.TryRelease(ship.Id);
        foreach (var s in world.SiteList) s.Flora = 0;
        world.SiteList[0].Flora = 0.3;
        world.HasAnimals = false;
        foreach (var s in world.SiteList) s.Fauna = 0;
        Run(sector, 240 * Minute); // a stressed world recovers slowly (#94)
        Assert.True(world.SiteList[0].Flora > 0.8, $"{world.SiteList[0].Flora}");
        Assert.True(world.SiteList[1].Flora > 0.05);
        Assert.True(world.SiteList[^1].Flora > 0.05); // the ring wraps round
    }

    [Fact]
    public void A_world_with_nothing_left_is_lost_for_good()
    {
        var (sector, world, _, _) = Game(TestData.StarterShip());
        foreach (var s in world.SiteList) s.Flora = 0;
        Run(sector, 30 * Minute);
        Assert.False(world.HasLife);
        Assert.All(world.Sites, s => Assert.Equal(0, s.Flora));
        Assert.Contains(sector.Events, e => e.Text.Contains("died out"));
    }

    [Fact]
    public void Mining_harms_the_plants_around_the_drill()
    {
        var (sector, world, ship, site) = Game(TestData.StarterShip());
        var other = world.Sites.First(s => s != site);
        Run(sector, 2 * Minute);
        Assert.True(ship.CargoKg > 5_000);
        Assert.True(site.Flora < 0.999);
        Assert.Equal(1, other.Flora);
    }

    [Fact]
    public void Jobs_leave_a_quarter_standing_and_wait_for_it_to_grow()
    {
        var (sector, world, ship, site) = Game();
        foreach (var s in world.SiteList) s.Flora = 0.26; // 5 t above the reserve at each site
        var yard = sector.Crafts.First(c => c.IsShipyard);
        var started = sector.StartJob(ship.Id, new MiningJob(world.Id, "biomass", yard.Id, 1));
        Assert.True(started.Done, started.Message);
        Run(sector, 5 * Minute);
        Assert.True(ship.Inventory["biomass"] > 0);
        Assert.All(world.Sites, s => Assert.True(s.Flora >= SectorState.JobLeavesCover - 1e-5));
        site.Flora = SectorState.JobLeavesCover; // at the reserve a job finds nothing there
        Assert.Equal(0, sector.MineableAt(world, "biomass", site.CentreRad));
    }

    [Fact]
    public void Harvesting_needs_a_harvester_and_drills_a_drill()
    {
        var (sector, world, ship, _) = Game();
        var yard = sector.Crafts.First(c => c.IsShipyard);
        Assert.Contains("no drill", sector.StartJob(ship.Id, new MiningJob(world.Id, "iron_ore", yard.Id, 1)).Message);
        var (s2, w2, miner, _) = Game(TestData.StarterShip());
        Assert.Contains("no harvester", s2.StartJob(miner.Id, new MiningJob(w2.Id, "biomass", s2.Crafts.First(c => c.IsShipyard).Id, 1)).Message);
    }

    [Fact]
    public void The_law_stops_mining_and_harvesting_on_living_sites_only()
    {
        var (sector, world, ship, site) = Game(HarvesterShip());
        sector.SetLaw(Laws.ProtectLife, true);
        Run(sector, 200);
        Assert.Equal(0, ship.CargoKg);
        Assert.Equal(0, sector.MineableSurveyed(world, "rock"));
        site.Flora = 0;
        Assert.True(sector.MineableAt(world, "rock", site.CentreRad) > 0);
        Assert.True(sector.MineableSurveyed(world, "rock") > 0);
        Assert.Same(site, SectorState.MiningSite(world, "rock", 0, spareLiving: true));
        Assert.True(Culture.Get("conservationist").StanceOn(Laws.ProtectLife) > 0);
    }

    [Fact]
    public void A_job_held_up_by_the_law_says_so()
    {
        var (sector, world, ship, _) = Game();
        var yard = sector.Crafts.First(c => c.IsShipyard);
        sector.StartJob(ship.Id, new MiningJob(world.Id, "biomass", yard.Id, 1));
        sector.SetLaw(Laws.ProtectLife, true);
        Run(sector, 2 * Minute, () => ship.Job!.WaitReason is not null);
        Assert.Contains("Protect native life", ship.Job!.WaitReason);
    }

    [Fact]
    public void Harming_life_in_a_claimed_system_is_a_grievance()
    {
        var (sector, world, ship, _) = Game();
        var faction = sector.MeetCivilization(sector.Stars[0], cultureId: "conservationist", baseline: 0);
        Run(sector, 3 * Minute);
        var harvested = faction.Grievance;
        var mining = ship.Inventory["biomass"] / 1000.0 * SectorState.GrievancePerTonne - 3 * SectorState.GrievanceFadePerMinute; // less what faded
        Assert.True(harvested > mining + 0.15, $"{harvested} against {mining}"); // more than mining alone
    }

    [Fact]
    public void Finding_and_sampling_give_research_points_once()
    {
        var (sector, world, _, _) = Game();
        Run(sector, 15 * Second);
        Assert.True(world.LifeFound && world.LifeSampled);
        Assert.Equal(SectorState.LifeFoundPoints + SectorState.LifeSampledPoints, sector.ResearchPoints, 6);
        Run(sector, Minute);
        Assert.Equal(SectorState.LifeFoundPoints + SectorState.LifeSampledPoints, sector.ResearchPoints, 6);
    }

    [Fact]
    public void Clinging_plants_slow_machines_and_corrosive_ones_wear_them_but_never_away()
    {
        long Cut(FloraHazard hazard) { var (s, _, ship, _) = Game(hazard: hazard); Run(s, Minute); return ship.Inventory["biomass"]; }
        Assert.InRange(Cut(FloraHazard.Clinging) / (double)Cut(FloraHazard.None), 0.45, 0.6);

        var (sector, world, worn, _) = Game(TestData.StarterShip(), FloraHazard.Corrosive);
        Run(sector, 30 * Minute);
        Assert.NotEmpty(worn.PartHealth);
        foreach (var cell in worn.Design.Cells) sector.DebugDamagePart(worn.Id, cell.Pos, 0.11);
        foreach (var s in world.SiteList) s.Flora = 1;
        Run(sector, 60 * Minute);
        Assert.Equal(0, worn.PartsLost);
        Assert.All(worn.PartHealth.Values, h => Assert.True(h >= 0.1 - 1e-9));
    }

    [Fact]
    public void Biomass_is_refined_into_food()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var food = yard.Inventory["food"];
        yard.Inventory.Add("biomass", 5_000);
        Run(game.Sector, 3 * Minute);
        Assert.True(yard.Inventory["food"] > food + 1_000);
    }

    [Fact]
    public void Life_survives_a_save_exactly()
    {
        var (sector, world, _, _) = Game(hazard: FloraHazard.Corrosive);
        Run(sector, Minute + 7);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.True(loaded.GetBody(world.Id).HasLife);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Run(sector, 3 * Minute);
        Run(loaded, 3 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }
}
