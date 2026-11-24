using System;
using System.Diagnostics;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Targets by kind (#107) and flying to a target (#108).</summary>
public sealed class TargetingTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    private static CelestialBodyState Body(SectorState s, string name) => s.Bodies.Single(b => b.Name == name);

    private static CraftState AddShip(SectorState sector, Vector2D at)
    {
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), "Ship", design, design.ComputeStats(Catalog), at, 0);
        sector.AddCraft(ship);
        return ship;
    }

    private static (SectorState Sector, CraftState Player) New()
    {
        var game = TestData.NewGame();
        return (game.Sector, game.Sector.GetCraft(game.PlayerCraftId));
    }

    [Fact]
    public void Kinds_are_kept_apart_nearest_first_in_this_system_only()
    {
        var (sector, player) = New();
        var far = AddShip(sector, player.Position + new Vector2D(0, -900));
        var near = AddShip(sector, player.Position + new Vector2D(0, -300));
        var (star, planet) = SectorGenerator.GenerateSystem(sector, 1);
        var away = AddShip(sector, sector.GetPosition(planet.Id) + new Vector2D(planet.RadiusM + 5_000, 0));
        var wreck = AddShip(sector, player.Position + new Vector2D(0, -600));
        wreck.IsWreck = true;

        var ships = sector.TargetsOf(player, TargetKind.Ships, 50);
        Assert.Equal(new[] { near.Id, far.Id }, ships.Targets.Select(t => t.Craft));
        Assert.DoesNotContain(ships.Targets, t => t.Craft == away.Id || t.Craft == player.Id);
        Assert.Equal(wreck.Id, sector.TargetsOf(player, TargetKind.Wrecks, 50).Targets.Single().Craft);
        Assert.Contains(sector.TargetsOf(player, TargetKind.Stations, 50).Targets, t => sector.GetCraft(t.Craft).IsShipyard);
        var bodies = sector.TargetsOf(player, TargetKind.Bodies, 50).Targets;
        Assert.Equal(sector.NearestBody(player.Position).Body.Id, bodies[0].Body);
        Assert.DoesNotContain(bodies, t => t.Body == star.Id || t.Body == planet.Id);
        Assert.Empty(sector.TargetsOf(player, TargetKind.Enemies, 50).Targets);
    }

    [Fact]
    public void The_limit_keeps_the_nearest_and_tells_the_total()
    {
        var (sector, player) = New();
        for (var i = 0; i < 8; i++) AddShip(sector, player.Position + new Vector2D(0, -300 - 100 * i));
        var all = sector.TargetsOf(player, TargetKind.Ships, 50);
        var few = sector.TargetsOf(player, TargetKind.Ships, 3);
        Assert.Equal(8, few.Total);
        Assert.Equal(all.Targets.Take(3), few.Targets);
    }

    [Fact]
    public void Stepping_wraps_both_ways_and_skips_whats_gone()
    {
        var (sector, player) = New();
        var ships = Enumerable.Range(0, 3).Select(i => AddShip(sector, player.Position + new Vector2D(0, -300 - 100 * i))).ToList();
        var list = sector.TargetsOf(player, TargetKind.Ships, 50).Targets.ToList();
        Assert.Equal(list[0], sector.NextTarget(list, TargetRef.None, 1));
        Assert.Equal(list[2], sector.NextTarget(list, TargetRef.None, -1));
        Assert.Equal(list[0], sector.NextTarget(list, list[2], 1));
        Assert.Equal(list[2], sector.NextTarget(list, list[0], -1));
        list[1] = new TargetRef(new CraftId(99_999), BodyId.None);
        Assert.Equal(list[2], sector.NextTarget(list, list[0], 1));
        Assert.True(sector.NextTarget(Array.Empty<TargetRef>(), list[0], 1).IsNone);
        sector.SetTarget(player.Id, list[0]);
        Assert.Equal(list[0].Craft, player.TargetCraftId);
        sector.SetTarget(player.Id, TargetRef.None);
        Assert.True(player.TargetCraftId.IsNone && player.TargetBodyId.IsNone);
    }

    [Fact]
    public void Ten_thousand_listings_worth_of_craft_are_quick()
    {
        var (sector, player) = New();
        var design = TestData.StarterShip();
        var stats = design.ComputeStats(Catalog);
        var json = Stopwatch.StartNew();
        for (var i = 0; i < 2_000; i++) // adding sorts each time: 2,000 craft, listed five times
            sector.AddCraft(new CraftState(sector.AllocateCraftId(), "S", design, stats, player.Position + new Vector2D(100 + i, 1_000), 0));
        json.Restart();
        for (var i = 0; i < 5; i++) Assert.Equal(2_000, sector.TargetsOf(player, TargetKind.Ships, 50).Total);
        Assert.True(json.ElapsedMilliseconds < 500, $"{json.ElapsedMilliseconds} ms");
    }

    // --- Flying to it ---

    /// <summary>Between Home and Cinder, 100 km from Home, facing away from it, with fuel.</summary>
    private static (SectorState Sector, CraftState Ship) Out(long fuelKg = 500)
    {
        var (sector, ship) = New();
        var home = sector.GetPosition(SectorGenerator.HomePlanet(sector).Id);
        var to = sector.GetPosition(Body(sector, "Cinder").Id) - home;
        ship.Position = ship.PreviousPosition = home + to * (100_000 / to.Length);
        ship.Velocity = SectorGenerator.PlayerSpawnVelocity(sector);
        ship.HeadingRad = ship.PreviousHeadingRad = Math.Atan2(-to.X, to.Y);
        if (fuelKg > 0) ship.Inventory.Add(SectorState.FuelMaterial, fuelKg);
        return (sector, ship);
    }

    private static int Fly(SectorState sector, CraftState ship, int ticks)
    {
        var i = 0;
        for (; i < ticks && ship.FlyToPhase > 0; i++) sector.Step();
        return i;
    }

    [Fact]
    public void It_turns_cruises_and_arrives_near_a_body_matched()
    {
        var (sector, ship) = Out();
        var mass = sector.ComputeMassLedger().TotalKg;
        var cinder = Body(sector, "Cinder");
        Assert.True(sector.FlyTo(ship.Id, TargetRef.Of(cinder)).Done);
        Fly(sector, ship, 15 * Minute);
        Assert.Equal(0, ship.FlyToPhase);
        Assert.False(ship.Cruising);
        Assert.InRange(sector.GetRelativeMotion(ship.Id, cinder.Id).SurfaceDistanceM, 1_000, 4_500);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        Assert.Equal(ShipControls.Idle, ship.Controls); // given back
    }

    [Fact]
    public void It_stops_short_of_a_craft()
    {
        var (sector, ship) = Out();
        var other = AddShip(sector, ship.Position + new Vector2D(30_000, 0));
        other.Velocity = ship.Velocity;
        Assert.True(sector.FlyTo(ship.Id, TargetRef.Of(other)).Done);
        Fly(sector, ship, 3 * Minute);
        Assert.InRange(ship.Position.DistanceTo(other.Position), 2_500, 3_500);
        Assert.Contains("arrived near", ship.CruiseStopReason);
    }

    [Fact]
    public void A_flight_control_or_another_target_takes_over()
    {
        var (sector, ship) = Out();
        sector.FlyTo(ship.Id, TargetRef.Of(Body(sector, "Cinder")));
        for (var i = 0; i < Second; i++) sector.Step();
        Assert.True(ship.FlyToPhase > 0);
        ship.Controls = new ShipControls(0, 1, false);
        sector.Step();
        Assert.Equal(0, ship.FlyToPhase);
        ship.Controls = ShipControls.Idle;
        sector.FlyTo(ship.Id, TargetRef.Of(Body(sector, "Cinder")));
        sector.SetTarget(ship.Id, TargetRef.Of(Body(sector, "Rime")));
        Assert.Equal(0, ship.FlyToPhase);
    }

    [Fact]
    public void It_says_why_not()
    {
        var (sector, ship) = Out(fuelKg: 0);
        ship.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { ["fuel"] = ship.Inventory["fuel"] });
        Assert.Contains("fuel", sector.FlyTo(ship.Id, TargetRef.Of(Body(sector, "Cinder"))).Message);
        ship.Inventory.Add(SectorState.FuelMaterial, 500);
        Assert.Contains("target", sector.FlyTo(ship.Id, TargetRef.None).Message);
        Assert.Contains("close", sector.FlyTo(ship.Id, TargetRef.Of(AddShip(sector, ship.Position + new Vector2D(1_000, 0)))).Message);
        var home = SectorGenerator.HomePlanet(sector);
        var away = sector.GetPosition(home.Id) - ship.Position;
        var behind = AddShip(sector, ship.Position + away * (1 + 60_000 / away.Length));
        Assert.Contains("in the way", sector.FlyTo(ship.Id, TargetRef.Of(behind)).Message);
        var (_, planet) = SectorGenerator.GenerateSystem(sector, 1);
        Assert.Contains("another system", sector.FlyTo(ship.Id, TargetRef.Of(planet)).Message);
        Assert.Equal(0, ship.FlyToPhase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void It_survives_a_save_exactly(int seconds)
    {
        var (sector, ship) = Out();
        sector.FlyTo(ship.Id, TargetRef.Of(Body(sector, "Cinder")));
        for (var i = 0; i < seconds * Second; i++) sector.Step();
        Assert.True(ship.FlyToPhase > 0);
        var json = SaveGameSerializer.ToJson(sector, ship.Id);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, ship.Id));
        for (var i = 0; i < 2 * Minute; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, ship.Id), SaveGameSerializer.ToJson(loaded, ship.Id));
    }
}
