using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Armour, missiles and point defense (#87), and shields (#90).</summary>
public sealed class DefenseTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond;

    /// <summary>The starter ship's body with extra parts in a row beside it.</summary>
    private static ShipDesign With(params string[] extra)
    {
        var cells = TestData.StarterShip().Cells.Where(c => c.PartId is not ("drill" or "anchor_clamp")).ToList(); // 15 parts at most: no crew needed
        for (var i = 0; i < extra.Length; i++) cells.Add(new DesignCell(new GridPos(2 + i, 0), extra[i]));
        return new ShipDesign("test", "Test", cells);
    }

    private static (SectorState Sector, CraftState Ship, CraftState Enemy) Duel(ShipDesign mine, ShipDesign theirs, double distance = 500, bool watched = true)
    {
        var sector = TestData.NewGame().Sector;
        var at = sector.GetPosition(sector.Stars[0].Id) + new Vector2D(0, -3_000_000);
        var faction = sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: 0);
        var ship = Add(sector, mine, at, 0, "Ship");
        var enemy = Add(sector, theirs, at + new Vector2D(0, -distance), Math.PI, "Enemy");
        enemy.FactionId = faction.Id;
        sector.PilotCraftId = watched ? ship.Id : CraftId.None;
        return (sector, ship, enemy);
    }

    private static CraftState Add(SectorState sector, ShipDesign design, Vector2D at, double heading, string name)
    {
        var craft = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), at, heading);
        sector.AddCraft(craft);
        foreach (var ammo in craft.Stats.WeaponList.Select(w => w.Weapon.AmmoMaterial).Where(m => m is not null).Distinct()) craft.Inventory.Add(ammo!, 200);
        return craft;
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    private static double Hurt(CraftState c) => c.PartsLost + c.PartHealth.Values.Sum(h => 1 - h);

    [Fact]
    public void MissileRaider_IsAValidShip() => Assert.Empty(SectorState.MissileRaiderDesign.Validate(Catalog));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missiles_hit_a_distant_target_and_use_ammunition(bool watched)
    {
        var (sector, ship, enemy) = Duel(With("missile_launcher"), SectorState.RaiderDesign, 1_800, watched);
        enemy.HoldFire = true;
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, 25 * Second);
        Assert.True(Hurt(enemy) >= 1, $"{Hurt(enemy)}");
        Assert.True(ship.Inventory["missiles"] < 200);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Missiles_steer_to_a_target_that_moves_aside()
    {
        var (sector, _, enemy) = Duel(With("missile_launcher"), SectorState.RaiderDesign, 1_500);
        enemy.HoldFire = true;
        Run(sector, 2); // launched straight at it
        Assert.Single(sector.Shots);
        enemy.Position += new Vector2D(300, 0);
        Run(sector, 7 * Second);
        Assert.True(Hurt(enemy) > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Point_defense_stops_missiles(bool watched)
    {
        double Damage(params string[] extra)
        {
            var (sector, ship, enemy) = Duel(With(extra), new ShipDesign("m", "M", SectorState.MissileRaiderDesign.Cells.Where(c => c.PartId != "laser")), 2_000, watched);
            Run(sector, 25 * Second); // missiles only
            Assert.False(enemy.IsWreck);
            return Hurt(ship);
        }
        var open = Damage("hull", "hull", "hull");
        var defended = Damage("point_defense", "point_defense", "point_defense");
        Assert.True(open > 0);
        Assert.True(defended < 0.6 * open, $"{defended} against {open}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Armour_in_the_way_takes_the_hits(bool watched)
    {
        // A wall of plates in front of the ship, against the same enemy as an unarmoured one.
        ShipDesign Walled(string part) => new("w", "W", TestData.StarterShip().Cells
            .Concat(Enumerable.Range(-2, 5).Select(x => new DesignCell(new GridPos(x, -4), part))));
        // How badly the ship behind the wall is hurt (the enemy fires; the ship itself is unarmed).
        double Behind(string part)
        {
            var (sector, ship, _) = Duel(Walled(part), SectorState.RaiderDesign, 400, watched);
            Run(sector, (watched ? 40 : 90) * Second);
            var left = ship.Design.Cells.Select(c => c.Pos).ToHashSet();
            return TestData.StarterShip().Cells.Sum(c => !left.Contains(c.Pos) ? 1 : 1 - ship.PartHealth.GetValueOrDefault(c.Pos, 1));
        }
        var armoured = Behind("armour_plate");
        var plain = Behind("hull");
        Assert.True(armoured < plain, $"armoured {armoured}, plain {plain}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shields_take_weapon_damage_first_and_recharge(bool watched)
    {
        var (sector, ship, enemy) = Duel(With("shield_generator"), SectorState.RaiderDesign, 400, watched);
        enemy.HoldFire = true;
        Run(sector, 25 * Second);
        Assert.Equal(ship.Stats.ShieldCapacity, ship.ShieldCharge, 6);
        enemy.HoldFire = false;
        Run(sector, 60 * Second, () => ship.ShieldCharge < 1);
        Assert.InRange(ship.ShieldCharge, 0, 1);
        Assert.Equal(0, Hurt(ship));
        enemy.HoldFire = true;
        Run(sector, 30 * Second);
        Assert.Equal(ship.Stats.ShieldCapacity, ship.ShieldCharge, 6);
        Assert.Equal(0, Hurt(ship));
    }

    [Fact]
    public void Shields_need_power_and_dont_stop_crashes()
    {
        var bare = new ShipDesign("p", "P", With("shield_generator").Cells.Where(c => c.PartId != "solar_panel"));
        var (sector, ship, enemy) = Duel(bare, SectorState.RaiderDesign, 5_000);
        Run(sector, 25 * Second);
        Assert.True(ship.ShieldCharge > 0);
        ship.StoredEnergyKJ = 0;
        Run(sector, 5 * Second);
        Assert.Equal(0, ship.ShieldCharge);
        Assert.True(ship.ShieldPowerless);

        (sector, ship, _) = Duel(With("shield_generator"), SectorState.RaiderDesign, 5_000);
        Run(sector, 25 * Second);
        var charge = ship.ShieldCharge;
        var other = Add(sector, TestData.StarterShip(), ship.Position + new Vector2D(0, -200), Math.PI, "Rammer");
        other.Velocity = ship.Velocity + new Vector2D(0, 60);
        Run(sector, 5 * Second, () => Hurt(ship) > 0);
        Assert.True(Hurt(ship) > 0);
        Assert.Equal(charge, ship.ShieldCharge, 6);
    }

    [Fact]
    public void Missiles_are_made_on_request_and_loaded_when_docked()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        Run(sector, 60 * Second);
        Assert.Equal(0, yard.Inventory["missiles"]);
        sector.RestoreRequest(yard.Id, "missiles", 600);
        Run(sector, 120 * Second);
        Assert.Equal(600, yard.Inventory["missiles"]);
        var ship = Add(sector, With("missile_launcher"), yard.Position, 0, "Ship");
        ship.Inventory.TryRemove(new Dictionary<string, long> { ["missiles"] = 200 });
        ship.Position = sector.BerthPosition(yard, sector.FreePorts(yard)[0], ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        Run(sector, 2 * Second);
        Assert.Equal(SectorState.MagazineKg, ship.Inventory["missiles"]);
    }

    [Fact]
    public void Only_larger_raids_bring_a_missile_raider()
    {
        int Launchers(int ships)
        {
            var game = TestData.NewGame();
            var faction = game.Sector.MeetCivilization(game.Sector.Stars[0], cultureId: "pacifist", baseline: -80);
            game.Sector.StartRaid(faction, game.Sector.GetCraft(game.ShipyardId), ships);
            return game.Sector.Crafts.Count(c => !SectorState.IsPlayers(c) && c.Stats.WeaponList.Any(w => w.Weapon.Kind == WeaponKind.Missile));
        }
        Assert.Equal(0, Launchers(3));
        Assert.Equal(1, Launchers(4));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missiles_and_shields_survive_a_save_exactly(bool watched)
    {
        var (sector, _, _) = Duel(With("shield_generator", "point_defense", "missile_launcher"), SectorState.MissileRaiderDesign, 1_500, watched);
        Run(sector, 67);
        var player = sector.PilotCraftId;
        var json = SaveGameSerializer.ToJson(sector, player);
        if (watched) Assert.Contains("isMissile", json);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, player));
        Run(sector, 600);
        Run(loaded, 600);
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(loaded, player));
    }
}
