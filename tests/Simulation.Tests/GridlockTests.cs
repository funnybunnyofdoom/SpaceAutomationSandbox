using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Miners damaged in a crowd must not gridlock the yard (#3, #55).</summary>
public class GridlockTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private static readonly GridPos Drill = new(0, -3);
    private static readonly GridPos Clamp = new(0, 2);

    private static CraftState AddMiner(SectorState sector, CraftState yard, int i, BodyId source)
    {
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), $"M{i}", design, design.ComputeStats(Catalog),
            yard.Position + new Vector2D(200 + 30 * (i % 20), -300 - 30 * (i / 20)), 0) { Velocity = yard.Velocity };
        sector.AddCraft(ship);
        var started = sector.StartJob(ship.Id, new MiningJob(source, "iron_ore", yard.Id, 0.25));
        Assert.True(started.Done, started.Message);
        return ship;
    }

    private static (SectorState Sector, CraftState Yard, CelestialBodyState Moon) Setup()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        yard.LegacyUnlimitedStorage = true;
        var moon = game.Sector.Bodies.Single(b => b.Name == "Moon");
        moon.AddDeposit("iron_ore", 500_000_000);
        game.Sector.MarkSurveyed(moon.Id);
        return (game.Sector, yard, moon);
    }

    private static void RemoveAll(CraftState yard, string material) =>
        yard.Inventory.TryRemove(new Dictionary<string, long> { [material] = yard.Inventory[material] });

    [Fact]
    public void FortyMiners_KeepDeliveringInTheSecondHour()
    {
        var (sector, yard, moon) = Setup();
        var miners = Enumerable.Range(0, 40).Select(i => AddMiner(sector, yard, i, moon.Id)).ToList();

        for (var t = 0; t < 60 * TicksPerMinute; t++) sector.Step();
        var hour1 = miners.Sum(m => m.Job!.DeliveredKg);
        for (var t = 0; t < 60 * TicksPerMinute; t++) sector.Step();
        var hour2 = miners.Sum(m => m.Job!.DeliveredKg) - hour1;

        // Was 600 t in hour 1 and then nothing. Damage still thins the fleet, so this only asks for a working one.
        Assert.True(hour2 > 700_000, $"hour 1: {hour1 / 1000} t, hour 2: {hour2 / 1000} t\n"
            + string.Join("\n", miners.Select(m => $"{m.Name}: {m.Job!.Status}")));
    }

    [Fact]
    public void RepairWithoutMaterials_LeavesThePort_AndDocksAgainOnceTheyArrive()
    {
        var (sector, yard, moon) = Setup();
        var ship = AddMiner(sector, yard, 0, moon.Id);
        ship.Position = ship.PreviousPosition = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
        Assert.True(sector.TryDock(ship.Id).Done);
        RemoveAll(yard, "electronics");
        sector.DebugDamagePart(ship.Id, Drill, 0);

        for (var t = 0; t < 5 * TicksPerMinute; t++)
        {
            sector.Step();
            if (t < 2 * FixedStepClock.TicksPerSecond) continue;
            Assert.False(ship.IsDocked, ship.Job!.Status);
            Assert.True(ship.DockClaimStation.IsNone, "a blocked repair claims no berth");
        }
        Assert.StartsWith("waiting: repairs at Shipyard need", ship.Job!.Status);
        Assert.InRange(ship.Position.DistanceTo(yard.Position), 100, 400); // on the holding ring

        sector.DebugAddStock(yard.Id, "electronics", 5_000);
        for (var t = 0; t < 10 * TicksPerMinute && SectorState.NeedsRepair(ship); t++) sector.Step();
        Assert.False(SectorState.NeedsRepair(ship), ship.Job!.Status);
    }

    [Fact]
    public void DamagedWhileClimbing_FinishesTheClimbBeforeGoingForRepairs()
    {
        var (sector, yard, moon) = Setup();
        var ship = AddMiner(sector, yard, 0, moon.Id);
        var job = (MiningJob)ship.Job!;
        for (var t = 0; t < 30 * TicksPerMinute && !(job.State == MiningJobState.Lifting && !ship.IsLanded); t++) sector.Step();
        Assert.Equal(MiningJobState.Lifting, job.State);
        sector.DebugDamagePart(ship.Id, Drill, 0);

        var wentIn = false;
        for (var t = 0; t < 30 * TicksPerMinute && SectorState.NeedsRepair(ship); t++)
        {
            sector.Step();
            if (ship.ServiceAt.IsNone) continue;
            wentIn = true;
            Assert.NotEqual(MiningJobState.Lifting, job.State); // or it would climb at full throttle on leaving the yard
        }
        Assert.True(wentIn);
        Assert.False(SectorState.NeedsRepair(ship), job.Status);
    }

    [Fact]
    public void MinerThatLostItsClamp_LeavesTheSurfaceForRepairs()
    {
        var (sector, yard, moon) = Setup();
        var ship = AddMiner(sector, yard, 0, moon.Id);
        var job = (MiningJob)ship.Job!;
        for (var t = 0; t < 30 * TicksPerMinute && !(job.State == MiningJobState.Landing && !ship.IsLanded); t++) sector.Step();
        sector.DebugDamagePart(ship.Id, Clamp, 0);
        for (var t = 0; t < 40 * TicksPerMinute && SectorState.NeedsRepair(ship); t++) sector.Step();
        Assert.False(SectorState.NeedsRepair(ship), job.Status);
        Assert.True(ship.Stats.AnchorClampCount > 0);
    }

    [Fact]
    public void FlatShip_RechargesAfterUnloading_ButNeverLongerThanTheLimit()
    {
        foreach (var yardHasPower in new[] { true, false })
        {
            var (sector, yard, moon) = Setup();
            var ship = AddMiner(sector, yard, 0, moon.Id);
            var job = (MiningJob)ship.Job!;
            ship.Position = ship.PreviousPosition = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
            Assert.True(sector.TryDock(ship.Id).Done);
            ship.Inventory.Add("iron_ore", 1_000);
            Assert.True(sector.StartJob(ship.Id, job).Done); // docked with ore: unloads
            ship.StoredEnergyKJ = 0;
            yard.StoredEnergyKJ = yardHasPower ? 20_000 : 0; // shared: the ship gets 10,000 kJ, short of 12,500
            if (!yardHasPower) sector.DebugAddStock(yard.Id, "iron_ore", 100_000); // the refinery eats what little there is

            sector.Step();
            Assert.Contains("recharging at Shipyard", job.Status);
            Assert.StartsWith("recharging", job.Status); // not "waiting": it needs nobody
            Assert.Empty(sector.Alerts().Where(a => a.Target.Craft == ship.Id && a.Kind == AlertKind.Waiting));
            var ticks = 0;
            while (ship.IsDocked && ticks < 2 * TicksPerMinute) { sector.Step(); ticks++; }
            var seconds = ticks / (double)FixedStepClock.TicksPerSecond;
            Assert.InRange(seconds, 1, SectorState.RechargeMaxS + 1);
            if (yardHasPower)
            {
                Assert.True(seconds < SectorState.RechargeMaxS - 1, $"left as soon as it was charged: {seconds} s");
                Assert.True(ship.StoredEnergyKJ >= 0.24 * ship.Stats.EnergyCapacityKJ);
            }
            else Assert.True(seconds > SectorState.RechargeMaxS - 2, $"{seconds} s");
        }
    }
}
