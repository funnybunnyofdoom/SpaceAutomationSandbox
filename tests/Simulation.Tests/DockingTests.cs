using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class DockingTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static void RunSeconds(SectorState sector, double seconds)
    {
        for (var i = 0; i < (int)Math.Ceiling(seconds * FixedStepClock.TicksPerSecond); i++) sector.Step();
    }

    /// <summary>New game with the player moved next to the yard's first free berth, matched to the yard.</summary>
    private static (SectorState Sector, CraftState Ship, CraftState Yard) BesideYard(double offsetM = 20, double relativeSpeed = 0)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);
        var berth = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
        ship.Position = berth + new Vector2D(offsetM, 0);
        ship.Velocity = yard.Velocity + new Vector2D(relativeSpeed, 0);
        sector.SetTargetCraft(ship.Id, yard.Id);
        return (sector, ship, yard);
    }

    [Fact]
    public void StarterShipyard_HasTwoPorts_OpeningForwardAndBack()
    {
        var ports = TestData.StarterShipyard().ComputeStats(Catalog).DockingPorts;
        Assert.Equal(new[] { 0, 2 }, ports.Select(p => p.Rotation));
    }

    [Fact]
    public void TargetingAStation_BrakeHoldsPositionRelativeToIt()
    {
        var (sector, ship, yard) = BesideYard(offsetM: 400, relativeSpeed: 30);
        ship.Controls = new ShipControls(0, 0, true);
        RunSeconds(sector, 5);
        var gap = ship.Position.DistanceTo(yard.Position);

        // Close to a station on an orbit the brake turns with that orbit (#55), so the craft keeps its
        // place beside the station as it goes round (matching only its velocity would drift away).
        RunSeconds(sector, 120);
        Assert.InRange(ship.Position.DistanceTo(yard.Position), gap - 5, gap + 5);
        Assert.True(sector.GetRelativeMotionToCraft(ship.Id, yard.Id).ClosingSpeedMps is > -0.5 and < 0.5);
    }

    [Fact]
    public void Dock_WhenCloseAndMatched_HoldsTheShipAtTheBerth_NoseIn()
    {
        var (sector, ship, yard) = BesideYard();

        var result = sector.TryDock(ship.Id);

        Assert.True(result.Done, result.Message);
        Assert.Equal(yard.Id, ship.DockedToCraftId);
        RunSeconds(sector, 120); // two minutes of orbit
        Assert.True(ship.IsDocked);
        Assert.True(sector.BerthPosition(yard, ship.DockPortIndex, ship.CollisionRadiusM, yard.Position).DistanceTo(ship.Position) < 1e-6);
        Assert.Equal(SectorState.DockedHeading(yard, ship.DockPortIndex), ship.HeadingRad, 9);
        // Nose points at the yard.
        var toYard = yard.Position - ship.Position;
        var forward = ship.Forward;
        Assert.True((toYard.X * forward.X + toYard.Y * forward.Y) / toYard.Length > 0.99);
    }

    [Theory]
    [InlineData(150, 0, "Too far")]
    [InlineData(20, 5, "Too fast")]
    public void Dock_IsRefused_WithTheReason(double offset, double relativeSpeed, string reason)
    {
        var (sector, ship, _) = BesideYard(offset, relativeSpeed);

        var result = sector.TryDock(ship.Id);

        Assert.False(result.Done);
        Assert.Contains(reason, result.Message);
        Assert.False(ship.IsDocked);
    }

    [Fact]
    public void Undock_ByKey_OrByThrottle_ButNotByBrake()
    {
        var (sector, ship, yard) = BesideYard();
        sector.TryDock(ship.Id);

        ship.Controls = new ShipControls(0, 1, true);
        RunSeconds(sector, 2);
        Assert.True(ship.IsDocked);

        ship.Controls = new ShipControls(1, 0, false);
        sector.Step();
        Assert.False(ship.IsDocked);

        ship.Controls = ShipControls.Idle;
        ship.Position = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        Assert.True(sector.TryUndock(ship.Id).Done);
        Assert.False(ship.IsDocked);
    }

    [Fact]
    public void BuiltShips_FillFreePortsFirst()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        RunSeconds(sector, 2 * TestData.StarterShip().ComputeStats(Catalog).TotalMassKg / yard.Stats.FabricationRateKgPerS + 2);

        var built = sector.Crafts.Where(c => c.Id != game.PlayerCraftId && c.Id != game.ShipyardId).ToList();
        Assert.All(built, c => Assert.Equal(yard.Id, c.DockedToCraftId));
        Assert.Equal(new[] { 0, 1 }, built.Select(c => c.DockPortIndex).OrderBy(i => i));
        Assert.Empty(sector.FreePorts(yard));
    }

    [Fact]
    public void Docked_SavesAndLoads_AndContinuesIdentically()
    {
        var (sector, ship, _) = BesideYard();
        sector.TryDock(ship.Id);

        var json = SaveGameSerializer.ToJson(sector, ship.Id);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        RunSeconds(sector, 30);
        RunSeconds(loaded, 30);

        Assert.True(loaded.GetCraft(ship.Id).IsDocked);
        Assert.Equal(SaveGameSerializer.ToJson(sector, ship.Id), SaveGameSerializer.ToJson(loaded, ship.Id));
    }

    [Fact]
    public void Load_RejectsTwoShipsInOnePort()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        RunSeconds(sector, 80);
        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        Assert.Contains("\"dockPort\": 1", json);

        Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json.Replace("\"dockPort\": 1", "\"dockPort\": 0"), Catalog));
    }

    [Fact]
    public void TargetCraft_AndTargetBody_AreExclusive()
    {
        var (sector, ship, yard) = BesideYard();
        Assert.Equal(yard.Id, ship.TargetCraftId);
        Assert.True(ship.TargetBodyId.IsNone);

        sector.SetTarget(ship.Id, sector.Bodies[0].Id);
        Assert.True(ship.TargetCraftId.IsNone);
        Assert.Throws<ArgumentException>(() => sector.SetTargetCraft(ship.Id, ship.Id));
    }
}
