using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Civilization fleets (#89): standing patrols, who fires on whom, and attacking one that isn't hostile.</summary>
public sealed class FleetTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Minute = 60 * FixedStepClock.TicksPerSecond;

    /// <summary>A second system with a civilization in it.</summary>
    private static (SectorState Sector, CelestialBodyState Star, Faction Faction) Game(double baseline, string culture = "pacifist")
    {
        var sector = TestData.NewGame().Sector;
        var (star, _) = SectorGenerator.GenerateSystem(sector, 1);
        sector.PilotCraftId = CraftId.None;
        return (sector, star, sector.MeetCivilization(star, cultureId: culture, baseline: baseline));
    }

    private static List<CraftState> Patrol(SectorState sector, Faction faction) =>
        sector.Crafts.Where(c => c.IsPatrol && c.FactionId == faction.Id && !c.IsWreck).ToList();

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    private static double Hurt(CraftState c) => c.PartsLost + c.PartHealth.Values.Sum(h => 1 - h);

    /// <summary>A gunship of the player's beside a craft, facing it.</summary>
    private static CraftState Gunship(SectorState sector, CraftState near)
    {
        var design = new ShipDesign("g", "G", SectorState.RaiderDesign.Cells);
        var ship = new CraftState(sector.AllocateCraftId(), "Gunship", design, design.ComputeStats(Catalog), near.Position + new Vector2D(0, 400), 0) { Velocity = near.Velocity };
        sector.AddCraft(ship);
        ship.Inventory.Add("shells", 200);
        return ship;
    }

    [Fact]
    public void Civilizations_keep_a_patrol_in_their_own_system_and_mass_balances()
    {
        var (sector, star, faction) = Game(0);
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, Minute);
        var patrol = Patrol(sector, faction);
        Assert.Equal(SectorState.PatrolSize(faction), patrol.Count);
        Assert.All(patrol, c => Assert.Equal(star.Id, sector.StarAt(c.Position)!.Id));
        Assert.Equal(patrol.Count >= 3 ? 1 : 0, patrol.Count(c => c.Stats.WeaponList.Any(w => w.Weapon.Kind == WeaponKind.Missile))); // every third ship built
        Run(sector, 30 * Minute);
        Assert.Equal(SectorState.PatrolSize(faction), Patrol(sector, faction).Count);
        Assert.All(Patrol(sector, faction), c => Assert.Equal(0, Hurt(c))); // they fly without crashing
        Assert.All(Patrol(sector, faction), c => Assert.Equal(star.Id, sector.StarAt(c.Position)!.Id));
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Never_in_the_home_system()
    {
        var sector = TestData.NewGame().Sector;
        sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: -80);
        Run(sector, Minute);
        Assert.DoesNotContain(sector.Crafts, c => !SectorState.IsPlayers(c));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    [InlineData(60)]
    public void Patrols_that_arent_hostile_and_your_ships_leave_each_other_alone(double baseline)
    {
        var (sector, _, faction) = Game(baseline);
        Run(sector, Minute);
        var theirs = Patrol(sector, faction)[0];
        var mine = Gunship(sector, theirs);
        sector.SetTargetCraft(mine.Id, theirs.Id); // even targeted
        sector.PilotCraftId = mine.Id;
        Run(sector, Minute / 2);
        Assert.Equal(0, Hurt(theirs));
        Assert.Equal(200, mine.Inventory["shells"]);
        Assert.Empty(sector.Shots);
    }

    [Fact]
    public void Hostile_patrols_attack_what_they_meet()
    {
        var (sector, _, faction) = Game(-80);
        Run(sector, Minute);
        var theirs = Patrol(sector, faction)[0];
        var design = TestData.StarterShip();
        var hauler = new CraftState(sector.AllocateCraftId(), "Hauler", design, design.ComputeStats(Catalog), theirs.Position + new Vector2D(0, 2_000), 0) { Velocity = theirs.Velocity };
        sector.AddCraft(hauler);
        Run(sector, 2 * Minute, () => Hurt(hauler) > 0);
        Assert.True(Hurt(hauler) > 0);
    }

    [Fact]
    public void An_ordered_attack_makes_them_hostile_and_both_sides_fire()
    {
        var (sector, _, faction) = Game(40);
        Run(sector, Minute);
        var theirs = Patrol(sector, faction)[0];
        var mine = Gunship(sector, theirs);
        Assert.Equal(Disposition.Friendly, faction.Disposition);
        Assert.True(sector.DeclareAttack(faction.Id).Done);
        Assert.Equal(Disposition.Hostile, faction.Disposition);
        Assert.False(sector.DeclareAttack(faction.Id).Done); // already
        Run(sector, Minute / 2);
        Assert.True(Hurt(theirs) > 0);
        Run(sector, 30 * Minute);
        Assert.Equal(Disposition.Hostile, faction.Disposition); // it doesn't wear off in minutes
        Assert.Contains(sector.Events, e => e.Text.Contains("You attacked"));
    }

    [Fact]
    public void Lost_patrol_ships_are_replaced_slowly()
    {
        var (sector, _, faction) = Game(0);
        Run(sector, Minute);
        Run(sector, (int)(SectorState.FleetReplaceS * FixedStepClock.TicksPerSecond)); // well after the first three
        var lost = Patrol(sector, faction)[0];
        lost.IsWreck = true;
        Run(sector, 40);
        var after = Patrol(sector, faction).Count;
        Run(sector, (int)(SectorState.FleetReplaceS * FixedStepClock.TicksPerSecond) + 40);
        Assert.Equal(SectorState.PatrolSize(faction), Patrol(sector, faction).Count);
        Assert.InRange(after, SectorState.PatrolSize(faction) - 1, SectorState.PatrolSize(faction));
        Assert.Contains(lost, sector.Crafts); // the wreck stays, to salvage
    }

    [Fact]
    public void A_friendly_civilization_fights_its_enemies_and_neutral_ones_dont()
    {
        bool Fight(double friendlyBaseline)
        {
            var (sector, star, friendly) = Game(friendlyBaseline, "pacifist");
            var enemy = sector.MeetCivilization(sector.Stars[0], cultureId: "militarist", baseline: 0); // wants armed gates: the opposite
            Assert.True(SectorState.AreEnemies(friendly, enemy));
            Run(sector, Minute);
            var theirs = Patrol(sector, friendly)[0];
            var visitor = Gunship(sector, theirs);
            visitor.FactionId = enemy.Id;
            visitor.IsPatrol = true;
            visitor.HoldFire = true;
            Run(sector, Minute / 2);
            return Hurt(visitor) > 0;
        }
        Assert.True(Fight(60));
        Assert.False(Fight(0));
    }

    [Fact]
    public void Patrols_survive_a_save_exactly()
    {
        var (sector, _, faction) = Game(-80);
        Run(sector, Minute + 11);
        Gunship(sector, Patrol(sector, faction)[0]);
        Run(sector, 57);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Run(sector, 3 * Minute);
        Run(loaded, 3 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }
}
