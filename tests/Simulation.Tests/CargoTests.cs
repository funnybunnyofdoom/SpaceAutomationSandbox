using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class CargoTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    /// <summary>New game with the player ship docked at the shipyard.</summary>
    private static (SectorState Sector, CraftState Ship, CraftState Yard) Docked()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);
        ship.Position = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        return (sector, ship, yard);
    }

    [Fact]
    public void Transfer_MovesExactlyThatMuch_BothWays()
    {
        var (sector, ship, yard) = Docked();
        var total = yard.Inventory["metal"];

        Assert.True(sector.TryTransfer(yard.Id, ship.Id, "metal", 5_000).Done);
        Assert.Equal(5_000, ship.Inventory["metal"]);
        Assert.Equal(total - 5_000, yard.Inventory["metal"]);

        Assert.True(sector.TryTransfer(ship.Id, yard.Id, "metal", 2_000).Done);
        Assert.Equal(total, yard.Inventory["metal"] + ship.Inventory["metal"]);
    }

    [Fact]
    public void Transfer_RespectsCargoCapacity_AndStock_AndNeedsADock()
    {
        var (sector, ship, yard) = Docked();
        yard.Inventory.Add("metal", 100_000);
        var capacity = ship.CargoCapacityKg; // 40 t on the starter ship

        var tooMuch = sector.TryTransfer(yard.Id, ship.Id, "metal", capacity + 1);
        Assert.False(tooMuch.Done);
        Assert.Contains("room", tooMuch.Message);
        Assert.Equal(capacity, sector.MaxTransferKg(yard.Id, ship.Id, "metal"));
        Assert.True(sector.TryTransfer(yard.Id, ship.Id, "metal", capacity).Done);
        Assert.Equal(0, ship.FreeCargoKg);

        Assert.False(sector.TryTransfer(ship.Id, yard.Id, "electronics", 1).Done); // ship has none

        sector.TryUndock(ship.Id);
        var undocked = sector.TryTransfer(ship.Id, yard.Id, "metal", 1);
        Assert.False(undocked.Done);
        Assert.Contains("docked", undocked.Message);
    }

    [Fact]
    public void Cargo_SlowsTheShip()
    {
        var (sector, ship, yard) = Docked();
        var empty = ship.Stats.TotalThrustN / ship.MassKg;
        sector.TryTransfer(yard.Id, ship.Id, "metal", 20_000);

        Assert.Equal(ship.Stats.TotalMassKg + 20_000, ship.MassKg);
        Assert.True(ship.Stats.TotalThrustN / ship.MassKg < empty * 0.5);

        sector.TryUndock(ship.Id);
        var before = ship.Velocity;
        ship.Controls = new ShipControls(1, 0, false);
        for (var i = 0; i < FixedStepClock.TicksPerSecond; i++) sector.Step();
        Assert.Equal(ship.Stats.TotalThrustN / ship.MassKg, (ship.Velocity - before).Length, 1);
    }

    [Fact]
    public void Transfers_ConserveEveryMaterial()
    {
        var (sector, ship, yard) = Docked();
        long Total(string m) => yard.Inventory[m] + ship.Inventory[m];
        var start = NewGame.StartingStockKg.Keys.ToDictionary(m => m, Total);

        var rng = new DeterministicRandom(5);
        for (var i = 0; i < 500; i++)
        {
            var material = NewGame.StartingStockKg.Keys.ElementAt((int)(rng.NextUInt64() % 3));
            var kg = (long)rng.Range(1, 3_000);
            if (rng.NextDouble() < 0.5) sector.TryTransfer(yard.Id, ship.Id, material, kg);
            else sector.TryTransfer(ship.Id, yard.Id, material, kg);
        }

        foreach (var (m, kg) in start) Assert.Equal(kg, Total(m));
        Assert.True(ship.Inventory.TotalKg <= ship.CargoCapacityKg);
    }

    [Fact]
    public void ShipCargo_SurvivesSaveAndLoad()
    {
        var (sector, ship, yard) = Docked();
        sector.TryTransfer(yard.Id, ship.Id, "electronics", 1_234);

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, ship.Id), Catalog).Sector;

        Assert.Equal(1_234, loaded.GetCraft(ship.Id).Inventory["electronics"]);
    }
}
