using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Hazards and events (#123).</summary>
public sealed class HazardTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond;

    private static (SectorState Sector, CraftState Ship, CraftState Yard) Game(WorldAmount amount = WorldAmount.Off)
    {
        var options = new WorldOptions();
        foreach (var kind in Enum.GetValues<HazardKind>()) options[SectorState.OptionOf(kind)] = amount;
        var game = NewGame.Create(7, Catalog, TestData.StarterShip(), TestData.StarterShipyard(), options: options);
        return (game.Sector, game.Sector.GetCraft(game.PlayerCraftId), game.Sector.GetCraft(game.ShipyardId));
    }

    private static string Json(SectorState sector, CraftState ship) => SaveGameSerializer.ToJson(sector, ship.Id);

    private static SectorState Again(SectorState sector, CraftState ship) => SaveGameSerializer.FromJson(Json(sector, ship), Catalog).Sector;

    private static void Step(SectorState sector, int ticks) { for (var i = 0; i < ticks; i++) sector.Step(); }

    /// <summary>The ship held in full sunlight beside Home.</summary>
    private static void Sunny(SectorState sector, CraftState ship, double altitude = 3_000)
    {
        var home = SectorGenerator.HomePlanet(sector);
        var angle = TestData.SunnyAngle(sector, altitude);
        var radius = home.RadiusM + altitude;
        sector.PutOnRails(ship.Id, new CircularOrbit(home.Id, radius, 1e9, angle - 2 * Math.PI * sector.TimeS / 1e9));
    }

    private static CraftState Add(SectorState sector, ShipDesign design, string name)
    {
        var craft = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), Vector2D.Zero, 0);
        sector.AddCraft(craft);
        return craft;
    }

    private static ShipDesign With(params (int X, int Y, string Part)[] parts) => new("test_ship", "Test ship",
        TestData.StarterShip().Cells.Where(c => c.PartId is not ("drill" or "anchor_clamp" or "cargo")).Concat(parts.Select(p => new DesignCell(new GridPos(p.X, p.Y), p.Part))));

    private static double Worn(CraftState craft) => craft.PartHealth.Values.Sum(h => 1 - h);

    // --- When ---

    [Fact]
    public void They_come_as_often_as_the_option_says_and_off_is_never()
    {
        foreach (var (amount, low, high) in new[] { (WorldAmount.Off, 0, 0), (WorldAmount.Rare, 1, 6), (WorldAmount.Normal, 8, 17), (WorldAmount.Often, 24, 42) })
        {
            // Without the rest of the world, so that a day goes quickly.
            var sector = new SectorState(3) { Parts = Catalog };
            var star = new CelestialBodyState(sector.AllocateBodyId(), "Star", BodyKind.Star, 1e17, 1400, false, null);
            sector.AddBody(star);
            sector.AddBody(new CelestialBodyState(sector.AllocateBodyId(), "Rock", BodyKind.Planet, 1e15, 5500, true, new CircularOrbit(star.Id, 400_000, 1e7, 0)));
            foreach (var kind in Enum.GetValues<HazardKind>()) sector.Options[SectorState.OptionOf(kind)] = amount;
            var seen = new HashSet<int>();
            for (var i = 0; i < 24 * 3600 * Second; i++)
            {
                sector.Step();
                foreach (var hazard in sector.Hazards)
                    if (seen.Add(hazard.Id)) Assert.True(hazard.StartTick - sector.Tick >= SectorState.ForecastTicks - 2);
            }
            Assert.InRange(seen.Count, low, high);
        }
    }

    [Fact]
    public void Turning_it_up_or_off_counts_at_once_and_the_schedule_saves()
    {
        var (sector, ship, _) = Game(WorldAmount.Rare);
        Step(sector, Second + 1);
        Assert.Equal(3, sector.HazardNext.Count);
        Assert.Equal(Json(sector, ship), Json(Again(sector, ship), ship));
        foreach (var kind in Enum.GetValues<HazardKind>()) sector.Options[SectorState.OptionOf(kind)] = WorldAmount.Often;
        Step(sector, Second);
        Assert.All(sector.HazardNext.Values, t => Assert.True(t - sector.Tick <= 1.5 * 2.25 * 3600 * Second));
        foreach (var kind in Enum.GetValues<HazardKind>()) sector.Options[SectorState.OptionOf(kind)] = WorldAmount.Off;
        Step(sector, Second);
        Assert.Empty(sector.HazardNext);
    }

    [Fact]
    public void A_forecast_is_logged_and_needs_you()
    {
        var (sector, ship, _) = Game();
        sector.DebugHazard(HazardKind.SolarFlare, SectorState.ForecastTicks);
        Assert.Contains(sector.Events, e => e.Kind == EventKind.Danger && e.Text.Contains("flare in 5 minutes"));
        Assert.Contains(sector.Alerts(), a => a.Kind == AlertKind.Hazard && a.Text.Contains("in 5:00"));
        Assert.Equal(Json(sector, ship), Json(Again(sector, ship), ship));
    }

    // --- Flares ---

    [Fact]
    public void A_flare_stops_panels_and_wears_parts_in_the_open_but_never_kills()
    {
        var (sector, ship, _) = Game();
        Sunny(sector, ship);
        Step(sector, 2);
        Assert.True(ship.SolarFactor > 0.5);
        var total = sector.ComputeMassLedger().TotalKg;
        for (var i = 0; i < 12; i++)
        {
            var flare = sector.DebugHazard(HazardKind.SolarFlare)!;
            Step(sector, 3);
            if (!ship.InShadow) Assert.Equal(0, ship.SolarFactor);
            Step(sector, (int)(flare.EndTick - sector.Tick) + 2);
        }
        Assert.True(Worn(ship) > 2);
        Assert.Equal(TestData.StarterShip().Cells.Count, ship.Design.Cells.Count);
        Assert.All(ship.PartHealth.Values, h => Assert.True(h >= SectorState.HazardFloor - 1e-9));
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        Step(sector, 2);
        Assert.True(ship.SolarFactor > 0.5);
        Assert.Empty(sector.Hazards);
    }

    [Fact]
    public void One_flare_costs_a_moderate_amount()
    {
        var (sector, ship, _) = Game();
        Sunny(sector, ship);
        var flare = sector.DebugHazard(HazardKind.SolarFlare)!;
        Step(sector, (int)flare.EndTick + 2);
        Assert.InRange(Worn(ship) / ship.Design.Cells.Count, 0.03, 0.25);
    }

    [Fact]
    public void Shadow_docking_and_shields_protect()
    {
        var (sector, ship, yard) = Game();
        var home = SectorGenerator.HomePlanet(sector);
        var shaded = Add(sector, TestData.StarterShip(), "Shaded");
        var behind = sector.GetPosition(home.Id) - sector.GetPosition(sector.Stars[0].Id);
        var dark = Math.Atan2(behind.Y, behind.X);
        sector.PutOnRails(shaded.Id, new CircularOrbit(home.Id, home.RadiusM + 1_000, 1e9, dark));
        var docked = Add(sector, TestData.StarterShip(), "Docked");
        docked.Position = docked.PreviousPosition = sector.BerthPosition(yard, 0, docked.CollisionRadiusM, yard.Position);
        docked.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(docked.Id).Done);
        var shielded = Add(sector, With((2, 0, "shield_generator"), (-2, 0, "battery"), (-2, -1, "battery")), "Shielded");
        Sunny(sector, shielded, 4_000);
        Sunny(sector, ship);
        shielded.ShieldCharge = shielded.Stats.ShieldCapacity;
        shielded.StoredEnergyKJ = shielded.Stats.EnergyCapacityKJ;
        var flare = sector.DebugHazard(HazardKind.SolarFlare)!;
        for (var i = 0; i < flare.EndTick; i++)
        {
            if (i % 200 == 0) shielded.StoredEnergyKJ = shielded.Stats.EnergyCapacityKJ; // powered
            sector.Step();
        }
        Assert.True(Worn(ship) > 0);
        Assert.Equal(0, Worn(shaded));
        Assert.Equal(0, Worn(docked));
        Assert.Equal(0, Worn(shielded));
    }

    // --- Meteors ---

    private static int Shower(SectorState sector, CraftState ship, Action<SectorState>? each = null)
    {
        var home = SectorGenerator.HomePlanet(sector);
        var shower = sector.DebugHazard(HazardKind.MeteorShower, near: home.Id)!;
        var most = 0;
        while (sector.Tick < shower.EndTick + 60 * Second)
        {
            each?.Invoke(sector);
            sector.Step();
            most = Math.Max(most, sector.Meteors.Count);
        }
        Assert.Empty(sector.Meteors);
        return most;
    }

    [Fact]
    public void Meteors_hit_what_sits_still_wear_it_and_some_ore_comes_down_with_the_ledger_exact()
    {
        var (sector, ship, yard) = Game();
        Sunny(sector, ship);
        var total = sector.ComputeMassLedger().TotalKg;
        var parts = yard.Design.Cells.Count;
        Assert.True(Shower(sector, ship) > 0);
        Assert.True(Worn(ship) + Worn(yard) > 0.4, $"{Worn(ship)} {Worn(yard)}");
        Assert.Equal(parts, yard.Design.Cells.Count);
        Assert.All(ship.PartHealth.Values.Concat(yard.PartHealth.Values), h => Assert.True(h >= SectorState.HazardFloor - 1e-9));
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        Assert.Contains(sector.Events, e => e.Text.Contains("of ore came down"));
        Assert.Equal(Json(sector, ship), Json(Again(sector, ship), ship));
    }

    [Fact]
    public void Weapons_shoot_meteors()
    {
        double worn(bool armed)
        {
            var (sector, ship, yard) = Game();
            var target = Add(sector, With((2, 0, "laser"), (-2, 0, "laser"), (2, -1, "point_defense")), "Target");
            target.HoldFire = !armed;
            Sunny(sector, target, 4_000);
            var down = 0;
            var before = 0;
            Shower(sector, ship, s =>
            {
                target.StoredEnergyKJ = target.Stats.EnergyCapacityKJ;
                if (s.Beams.Count > 0 && s.Meteors.Count < before) down++;
                before = s.Meteors.Count;
            });
            Assert.Equal(armed, down > 0);
            return Worn(target);
        }
        Assert.True(worn(true) < worn(false) + 1e-9);
    }

    [Fact]
    public void A_game_saved_part_way_through_everything_goes_on_exactly()
    {
        var (sector, ship, _) = Game(WorldAmount.Often);
        Sunny(sector, ship);
        sector.DebugHazard(HazardKind.SolarFlare);
        sector.DebugHazard(HazardKind.Comet);
        sector.DebugHazard(HazardKind.MeteorShower, near: SectorGenerator.HomePlanet(sector).Id);
        Step(sector, 12 * Second);
        Assert.NotEmpty(sector.Meteors);
        var again = Again(sector, ship);
        Assert.Equal(Json(sector, ship), Json(again, ship));
        Step(sector, 60 * Second);
        Step(again, 60 * Second);
        Assert.Equal(Json(sector, ship), Json(again, ship));
    }

    // --- Comets ---

    [Fact]
    public void A_comet_comes_with_its_mass_and_goes_with_whats_left()
    {
        var (sector, ship, _) = Game();
        var total = sector.ComputeMassLedger().TotalKg;
        var hazard = sector.DebugHazard(HazardKind.Comet)!;
        Step(sector, 2);
        var comet = sector.GetBody(hazard.Body);
        Assert.True(SectorState.IsComet(comet) && comet.DepositsKg["methane_ice"] > 0 && sector.StarOf(comet.Id) is not null);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        // Its speed is its position's change, all along its visit.
        for (var t = 0.0; t < 14_000; t += 700)
        {
            var moved = (sector.GetPositionAt(comet.Id, t + 0.5) - sector.GetPositionAt(comet.Id, t - 0.5) - sector.GetVelocityAt(comet.Id, t)).Length;
            Assert.True(moved < 0.01, $"{moved}");
            Assert.InRange(sector.GetVelocityAt(comet.Id, t).Length, 20, 300);
        }
        var middle = (hazard.StartTick + hazard.EndTick) / 2 * FixedStepClock.TickSeconds;
        Assert.InRange(sector.GetPositionAt(comet.Id, middle).DistanceTo(sector.GetPositionAt(hazard.Star, middle)), 319_000, 325_000);
        sector.RestoreLanding(ship.Id, comet.Id, new Vector2D(comet.RadiusM + 10, 0));
        ship.Inventory.Add("ice", 5);
        total += 5;
        sector.GetBody(comet.Id).Extract("methane_ice", 1_000);
        ship.Inventory.Add("methane_ice", 1_000);
        // To its end, without playing the hours in between.
        var json = Json(sector, ship).Replace($"\"endTick\": {hazard.EndTick}", $"\"endTick\": {sector.Tick + 5}");
        var late = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Step(late, 10);
        Assert.DoesNotContain(late.Bodies, SectorState.IsComet);
        Assert.Equal(total, late.ComputeMassLedger().TotalKg);
        Assert.True(late.GetCraft(ship.Id).LandedOnBodyId.IsNone);
        Assert.Empty(late.Hazards);
    }

    [Fact]
    public void Comets_dont_change_the_ids_of_other_bodies()
    {
        var (sector, _, _) = Game();
        var next = sector.NextBodyId;
        sector.DebugHazard(HazardKind.Comet);
        Step(sector, 2);
        Assert.Equal(next, sector.NextBodyId);
    }

    // --- Shelter ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_ship_with_a_job_shelters_and_goes_back_to_it_unless_told_to_keep_working(bool keepWorking)
    {
        var (sector, ship, yard) = Game();
        ship.Inventory.Add("fuel", 100);
        sector.DebugSurveyAll();
        ship.Position = ship.PreviousPosition = yard.Position + new Vector2D(0, -900);
        ship.Velocity = yard.Velocity;
        var rock = sector.Bodies.First(b => b.Name == "Rock-1");
        Assert.True(sector.StartJob(ship.Id, new MiningJob(rock.Id, "iron_ore", yard.Id, 0.8)).Done);
        ship.KeepWorking = keepWorking;
        var flare = sector.DebugHazard(HazardKind.SolarFlare, 100)!;
        var sheltered = false;
        while (sector.Tick < flare.EndTick)
        {
            sector.Step();
            sheltered |= ship.IsDocked && ship.Job!.Status.Contains("shelter");
        }
        Assert.Equal(!keepWorking, sheltered);
        Assert.Equal(Json(sector, ship), Json(Again(sector, ship), ship));
        for (var i = 0; i < 120 * Second && ship.IsDocked; i++) sector.Step();
        Assert.False(ship.IsDocked);
        Assert.True(ship.ShelterAt.IsNone && ship.IsAutomated);
    }

    [Fact]
    public void With_more_ships_than_ports_the_rest_carry_on()
    {
        var (sector, ship, yard) = Game();
        sector.DebugSurveyAll();
        var rock = sector.Bodies.First(b => b.Name == "Rock-1");
        var ships = new List<CraftState>();
        for (var i = 0; i < 6; i++)
        {
            var miner = Add(sector, TestData.StarterShip(), $"Miner {i}");
            miner.Position = miner.PreviousPosition = yard.Position + new Vector2D(200 * (i - 3), -1_200);
            miner.Velocity = yard.Velocity;
            Assert.True(sector.StartJob(miner.Id, new MiningJob(rock.Id, "iron_ore", yard.Id, 0.8)).Done);
            ships.Add(miner);
        }
        var flare = sector.DebugHazard(HazardKind.SolarFlare, 100)!;
        var most = 0;
        while (sector.Tick < flare.EndTick) { sector.Step(); most = Math.Max(most, ships.Count(s => !s.ShelterAt.IsNone)); }
        Assert.InRange(most, 1, yard.Stats.DockingPorts.Count);
        Step(sector, 60 * Second);
        Assert.All(ships, s => Assert.True(s.ShelterAt.IsNone));
    }
}
