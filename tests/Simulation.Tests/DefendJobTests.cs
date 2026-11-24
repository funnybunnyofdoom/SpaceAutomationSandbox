using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Fleet orders (#88): guard, escort and patrol, retreating for repairs and fetching ammunition.</summary>
public sealed class DefendJobTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Minute = 60 * FixedStepClock.TicksPerSecond;
    private static readonly ShipDesign Gunship = new("gunship", "Gunship", SectorState.RaiderDesign.Cells);

    private static (SectorState Sector, CraftState Yard, CraftState Ship, Faction Faction) Game(double away = 1_500)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = Add(sector, Gunship, Near(sector, yard, away), yard.Velocity, "Gunship");
        sector.PilotCraftId = CraftId.None;
        return (sector, yard, ship, sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: 0));
    }

    /// <summary>A point straight up from a craft (away from the body it's near).</summary>
    private static Vector2D Near(SectorState sector, CraftState craft, double up)
    {
        var outward = craft.Position - sector.GetPosition(sector.NearestBody(craft.Position).Body.Id);
        return craft.Position + outward * (up / outward.Length);
    }

    private static CraftState Add(SectorState sector, ShipDesign design, Vector2D at, Vector2D velocity, string name)
    {
        var craft = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), at, 0) { Velocity = velocity };
        sector.AddCraft(craft);
        if (craft.Stats.WeaponList.Count > 0) craft.Inventory.Add("shells", 200);
        return craft;
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    private static double Hurt(CraftState c) => c.PartsLost + c.PartHealth.Values.Sum(h => 1 - h);

    [Fact]
    public void Orders_are_refused_with_reasons()
    {
        var (sector, yard, ship, _) = Game();
        var plain = Add(sector, TestData.StarterShip(), Near(sector, yard, 3_000), yard.Velocity, "Plain");
        Assert.Contains("no weapons", sector.StartJob(plain.Id, new DefendJob(DefendMode.Guard, yard.Id)).Message);
        Assert.Contains("escort it", sector.StartJob(ship.Id, new DefendJob(DefendMode.Guard, plain.Id)).Message);
        Assert.Contains("another working craft", sector.StartJob(ship.Id, new DefendJob(DefendMode.Escort, ship.Id)).Message);
        Assert.Contains("star system", sector.StartJob(ship.Id, new DefendJob(SectorGenerator.HomePlanet(sector).Id)).Message);
    }

    [Fact]
    public void A_guard_holds_by_its_station_and_attacks_what_comes_near()
    {
        var (sector, yard, ship, faction) = Game();
        Assert.True(sector.StartJob(ship.Id, new DefendJob(DefendMode.Guard, yard.Id)).Done);
        Run(sector, 5 * Minute);
        Assert.InRange(ship.Position.DistanceTo(yard.Position), 0, 800);
        Assert.Equal(0, Hurt(ship)); // it didn't bump into anything
        Assert.Contains("guarding", ship.Job!.Status);

        var raider = Add(sector, SectorState.RaiderDesign, Near(sector, yard, 3_000), yard.Velocity, "Raider");
        raider.FactionId = faction.Id;
        raider.HoldFire = true;
        Run(sector, 5 * Minute, () => raider.IsWreck);
        Assert.True(raider.IsWreck, $"{ship.Job.Status} {ship.Position.DistanceTo(raider.Position):0} m, hurt {Hurt(raider)}");
        Run(sector, 3 * Minute);
        Assert.True(ship.Job.CompletedCycles >= 1);
        Assert.InRange(ship.Position.DistanceTo(yard.Position), 0, 1_000); // back on station
    }

    [Fact]
    public void A_guard_doesnt_chase_enemies_that_stay_away()
    {
        var (sector, yard, ship, faction) = Game();
        sector.StartJob(ship.Id, new DefendJob(DefendMode.Guard, yard.Id));
        Run(sector, 3 * Minute);
        var raider = Add(sector, SectorState.RaiderDesign, Near(sector, yard, 9_000), yard.Velocity, "Raider");
        raider.FactionId = faction.Id;
        Run(sector, Minute);
        Assert.InRange(ship.Position.DistanceTo(yard.Position), 0, 1_000);
        Assert.Equal(200, ship.Inventory["shells"]);
    }

    [Fact]
    public void An_escort_stays_with_a_moving_ship()
    {
        var (sector, yard, ship, _) = Game(2_000);
        var hauler = Add(sector, TestData.StarterShip(), Near(sector, yard, 2_600), yard.Velocity, "Hauler");
        var moon = sector.Bodies.First(b => b.Name == "Moon");
        Assert.True(sector.StartJob(hauler.Id, new MiningJob(moon.Id, "iron_ore", yard.Id, 0.25)).Done);
        sector.FullDetailEverywhere = true;
        Assert.True(sector.StartJob(ship.Id, new DefendJob(DefendMode.Escort, hauler.Id)).Done);
        Run(sector, 6 * Minute);
        Assert.True(hauler.Position.DistanceTo(yard.Position) > 2_000 || hauler.IsLanded || hauler.Job!.CompletedCycles > 0);
        Assert.True(ship.Position.DistanceTo(hauler.Position) < 3_000, $"{ship.Position.DistanceTo(hauler.Position):0} m, {ship.Job!.Status}");
        Assert.False(ship.IsLanded);
    }

    [Fact]
    public void A_patrol_goes_from_planet_to_planet()
    {
        var (sector, _, ship, _) = Game();
        Assert.True(sector.StartJob(ship.Id, new DefendJob(sector.Stars[0].Id)).Done);
        var seen = new HashSet<int>();
        Run(sector, 180 * Minute, () => { seen.Add(((DefendJob)ship.Job!).PatrolIndex); return seen.Count >= 3; });
        Assert.True(seen.Count >= 3, $"{string.Join(",", seen)} {ship.Job!.Status}");
        Assert.Equal(0, Hurt(ship));
    }

    [Fact]
    public void A_damaged_guard_retreats_for_repairs_and_returns()
    {
        var (sector, yard, ship, _) = Game();
        sector.StartJob(ship.Id, new DefendJob(DefendMode.Guard, yard.Id));
        Run(sector, Minute);
        sector.DebugDamagePart(ship.Id, ship.Design.Cells.First(c => c.PartId == "hull").Pos, 0.2);
        Run(sector, 20);
        Assert.Contains("repairs", ship.Job!.Status);
        Run(sector, 15 * Minute, () => ship.PartHealth.Count == 0 && !ship.IsDocked);
        Assert.Empty(ship.PartHealth);
        Run(sector, Minute);
        Assert.Contains("guarding", ship.Job.Status);
    }

    [Fact]
    public void Out_of_ammunition_it_fetches_more_and_says_when_there_is_none()
    {
        var (sector, yard, ship, _) = Game();
        ship.Inventory.TryRemove(new Dictionary<string, long> { ["shells"] = 200 });
        sector.StartJob(ship.Id, new DefendJob(DefendMode.Guard, yard.Id));
        Run(sector, 2 * Minute);
        Assert.Contains("no shells", ship.Job!.Status);
        yard.Inventory.Add("shells", 1_000);
        Run(sector, 20);
        Assert.Contains("fetching shells", ship.Job.Status);
        Run(sector, 10 * Minute, () => ship.Inventory["shells"] > 0 && !ship.IsDocked);
        Assert.Equal(SectorState.MagazineKg, ship.Inventory["shells"]);
        Assert.False(ship.IsDocked);
    }

    [Fact]
    public void A_lost_post_makes_it_wait_with_the_reason()
    {
        var (sector, yard, ship, _) = Game();
        var other = Add(sector, TestData.StarterShip(), Near(sector, yard, 3_000), yard.Velocity, "Hauler");
        sector.StartJob(ship.Id, new DefendJob(DefendMode.Escort, other.Id));
        Run(sector, 40);
        other.IsWreck = true;
        Run(sector, 40);
        Assert.Contains("is gone", ship.Job!.WaitReason);
    }

    [Fact]
    public void Guards_at_a_far_gate_fight_a_raid_off_far_from_the_player()
    {
        var (sector, yard, ship, faction) = Game();
        var fleet = new List<CraftState> { ship };
        for (var i = 0; i < 5; i++) fleet.Add(Add(sector, Gunship, Near(sector, yard, 1_800 + 300 * i), yard.Velocity, $"G{i}"));
        foreach (var g in fleet) Assert.True(sector.StartJob(g.Id, new DefendJob(DefendMode.Guard, yard.Id)).Done);
        Run(sector, 4 * Minute);
        for (var i = 0; i < 2; i++)
        {
            var raider = Add(sector, SectorState.RaiderDesign, Near(sector, yard, 6_000 + 200 * i), yard.Velocity, $"R{i}");
            raider.FactionId = faction.Id;
        }
        faction.RaidGateId = yard.Id; // what they attack (not a gate, so kept alive by hand below)
        for (var i = 0; i < 6 * Minute && sector.Crafts.Any(c => !SectorState.IsPlayers(c) && !c.IsWreck); i++)
        {
            faction.RaidGateId = yard.Id;
            faction.RaidEndTick = long.MaxValue;
            sector.Step();
        }
        Assert.True(fleet.Count(g => !g.IsWreck) >= 4, $"{fleet.Count(g => !g.IsWreck)} left");
        Assert.DoesNotContain(sector.Crafts, c => !SectorState.IsPlayers(c) && !c.IsWreck);
    }

    [Theory]
    [InlineData(DefendMode.Guard)]
    [InlineData(DefendMode.Escort)]
    [InlineData(DefendMode.Patrol)]
    public void Orders_survive_a_save_exactly(DefendMode mode)
    {
        var (sector, yard, ship, faction) = Game();
        var other = Add(sector, TestData.StarterShip(), Near(sector, yard, 3_000), yard.Velocity, "Hauler");
        var job = mode == DefendMode.Patrol ? new DefendJob(sector.Stars[0].Id) : new DefendJob(mode, mode == DefendMode.Guard ? yard.Id : other.Id);
        Assert.True(sector.StartJob(ship.Id, job).Done);
        var raider = Add(sector, SectorState.RaiderDesign, Near(sector, yard, 2_500), yard.Velocity, "Raider");
        raider.FactionId = faction.Id;
        Run(sector, 211);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Run(sector, 3 * Minute);
        Run(loaded, 3 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }
}
