using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Combat (#84, #85): weapons, shots, damage, wrecks and raids, near the player and far away.</summary>
public sealed class CombatTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Minute = 60 * FixedStepClock.TicksPerSecond;

    private static readonly ShipDesign Gunship = new("gunship", "Gunship", SectorState.RaiderDesign.Cells);

    /// <summary>Deep in empty space, out of any gravity worth the name: a raider pinned to a point and a gunship facing it.</summary>
    private static (SectorState Sector, CraftState Ship, CraftState Raider, Faction Faction) Duel(double distance = 500, bool watched = true, ShipDesign? design = null)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var at = sector.GetPosition(sector.Stars[0].Id) + new Vector2D(0, -3_000_000);
        var ship = Add(sector, design ?? Gunship, at, 0, "Gunship");
        var faction = sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: 0);
        var raider = Add(sector, SectorState.RaiderDesign, at + new Vector2D(0, -distance), Math.PI, "Raider");
        raider.FactionId = faction.Id;
        sector.PilotCraftId = watched ? ship.Id : CraftId.None;
        return (sector, ship, raider, faction);
    }

    private static CraftState Add(SectorState sector, ShipDesign design, Vector2D at, double heading, string name)
    {
        var craft = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), at, heading);
        sector.AddCraft(craft);
        craft.Inventory.Add("shells", 200);
        return craft;
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    private static double Hurt(CraftState c) => c.PartsLost + c.PartHealth.Values.Sum(h => 1 - h);

    [Fact]
    public void RaiderDesign_IsAValidShip() => Assert.Empty(SectorState.RaiderDesign.Validate(Catalog));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_sides_fire_and_hurt_each_other_near_and_far(bool watched)
    {
        var (sector, ship, raider, _) = Duel(watched: watched);
        Run(sector, Minute / 3);
        Assert.True(Hurt(raider) > 0.1, $"raider {Hurt(raider)}");
        Assert.True(Hurt(ship) > 0.1, $"ship {Hurt(ship)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mass_stays_exact_through_a_fight(bool watched)
    {
        var (sector, ship, raider, _) = Duel(watched: watched);
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, 3 * Minute);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        Assert.True(ship.IsWreck || raider.IsWreck, $"ship {Hurt(ship)}, raider {Hurt(raider)}");
    }

    [Fact]
    public void Near_and_far_fights_do_similar_damage()
    {
        double Damage(bool watched)
        {
            var (sector, ship, raider, _) = Duel(watched: watched);
            raider.HoldFire = true;
            ship.HoldFire = false;
            Run(sector, Minute / 6);
            return Hurt(raider);
        }
        Assert.InRange(Damage(true) / Damage(false), 0.4, 2.5);
    }

    [Fact]
    public void Guns_use_ammunition_and_stop_without_it()
    {
        var gunOnly = new ShipDesign("g", "G", Gunship.Cells.Where(c => c.PartId != "laser"));
        var (sector, ship, raider, _) = Duel(design: gunOnly);
        raider.HoldFire = true;
        Run(sector, 5 * FixedStepClock.TicksPerSecond);
        Assert.True(ship.Inventory["shells"] < 200);
        Assert.Equal(200 - ship.Inventory["shells"], sector.EjectedKg.GetValueOrDefault("shells"));
        ship.Inventory.TryRemove(new Dictionary<string, long> { ["shells"] = ship.Inventory["shells"] });
        Run(sector, 3 * FixedStepClock.TicksPerSecond); // shots in flight land
        var hurt = Hurt(raider);
        Run(sector, 5 * FixedStepClock.TicksPerSecond);
        Assert.Equal(hurt, Hurt(raider));
    }

    [Fact]
    public void Lasers_need_power()
    {
        var laserOnly = new ShipDesign("l", "L", Gunship.Cells.Where(c => c.PartId is not ("autocannon" or "solar_panel")));
        var (sector, ship, raider, _) = Duel(distance: 300, design: laserOnly);
        raider.HoldFire = true;
        ship.StoredEnergyKJ = 0;
        Run(sector, 100);
        Assert.Equal(0, Hurt(raider));
        ship.StoredEnergyKJ = ship.Stats.EnergyCapacityKJ;
        Run(sector, 100);
        Assert.True(Hurt(raider) > 0);
        Assert.True(ship.StoredEnergyKJ < ship.Stats.EnergyCapacityKJ);
    }

    [Fact]
    public void Range_arc_hold_fire_and_bodies_stop_weapons()
    {
        var (sector, _, raider, _) = Duel(distance: 2_000);
        raider.HoldFire = true;
        Run(sector, 20);
        Assert.Equal(0, Hurt(raider)); // out of range

        (sector, var ship, raider, _) = Duel();
        ship.HoldFire = raider.HoldFire = true;
        Run(sector, 100);
        Assert.Equal(0, Hurt(raider) + Hurt(ship));

        var gunOnly = new ShipDesign("g", "G", Gunship.Cells.Where(c => c.PartId != "laser"));
        (sector, ship, raider, _) = Duel(design: gunOnly);
        raider.HoldFire = true;
        ship.HeadingRad = ship.PreviousHeadingRad = Math.PI; // facing away
        Run(sector, 100);
        Assert.Equal(0, Hurt(raider));
        Assert.Equal(200, ship.Inventory["shells"]);
    }

    [Fact]
    public void Nobody_fires_without_an_enemy_and_gunships_dont_hit_each_other()
    {
        var game = TestData.NewGame();
        var at = game.Sector.GetPosition(game.Sector.Stars[0].Id) + new Vector2D(0, -3_000_000);
        var a = Add(game.Sector, Gunship, at, 0, "A");
        var b = Add(game.Sector, Gunship, at + new Vector2D(0, -300), Math.PI, "B");
        Run(game.Sector, 200);
        Assert.Equal(0, Hurt(a) + Hurt(b));
        Assert.Equal(400, a.Inventory["shells"] + b.Inventory["shells"]);
        Assert.Empty(game.Sector.Shots);
    }

    [Fact]
    public void A_ship_without_its_cockpit_is_a_wreck_that_keeps_its_cargo_and_does_nothing()
    {
        var (sector, ship, raider, _) = Duel(design: new ShipDesign("bare", "Bare", Gunship.Cells.Where(c => c.PartId is not ("autocannon" or "laser"))));
        ship.Inventory.TryRemove(new Dictionary<string, long> { ["shells"] = 200 });
        Run(sector, 5 * Minute, () => ship.IsWreck);
        Assert.True(ship.IsWreck, $"{Hurt(ship)}");
        Assert.DoesNotContain(ship.Design.Cells, c => c.PartId == "cockpit");
        Assert.Contains(sector.Events, e => e.Text.Contains("wreck is adrift"));
        Assert.False(SectorState.Operational(ship));
        var hurt = Hurt(ship);
        Run(sector, 200);
        Assert.Equal(hurt, Hurt(ship)); // nobody shoots a wreck
        Assert.Contains(ship, sector.Crafts);
        Assert.False(raider.IsWreck);
    }

    [Fact]
    public void Shells_are_made_only_for_a_request_and_docked_gunships_take_them()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var metal = yard.Inventory["metal"];
        Run(sector, Minute);
        Assert.Equal(0, yard.Inventory["shells"]);
        Assert.Equal(metal, yard.Inventory["metal"]);
        sector.RestoreRequest(yard.Id, "shells", 900);
        Run(sector, 2 * Minute);
        Assert.Equal(900, yard.Inventory["shells"]);

        var ship = new CraftState(sector.AllocateCraftId(), "Gunship", Gunship, Gunship.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(ship);
        var port = sector.FreePorts(yard)[0];
        ship.Position = sector.BerthPosition(yard, port, ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        Run(sector, 40);
        Assert.Equal(SectorState.MagazineKg, ship.Inventory["shells"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_fight_continues_exactly_after_a_save(bool watched)
    {
        var (sector, _, _, _) = Duel(watched: watched);
        Run(sector, 63);
        var player = sector.PilotCraftId;
        var json = SaveGameSerializer.ToJson(sector, player);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, player));
        Run(sector, 400);
        Run(loaded, 400);
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(loaded, player));
    }
}
