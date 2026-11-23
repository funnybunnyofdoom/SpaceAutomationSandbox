using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#29: ships docked to a structure push it (tugs), and everything docked moves with it.</summary>
public class TugTests
{
    private static (SectorState Sector, CraftState Yard, CraftState Tug) YardWithTug()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var tug = sector.GetCraft(game.PlayerCraftId);
        tug.Position = sector.BerthPosition(yard, 0, tug.CollisionRadiusM, yard.Position);
        tug.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(tug.Id).Done);
        sector.Step(); // the tug settles at its berth heading
        return (sector, yard, tug);
    }

    /// <summary>Controls for the structure that push along a direction: throttle and strafe from its forward and right.</summary>
    private static ShipControls PushAlong(CraftState structure, Vector2D direction)
    {
        var forward = structure.Forward;
        var right = FlightModel.RightFromHeading(structure.HeadingRad);
        return new ShipControls(forward.X * direction.X + forward.Y * direction.Y, 0, false, right.X * direction.X + right.Y * direction.Y);
    }

    [Fact]
    public void AStationWithoutThrusters_IsPushedByItsTug_AndLeavesItsParkingOrbit()
    {
        var (sector, yard, tug) = YardWithTug();
        Assert.Equal(0, yard.Stats.TotalThrustN);
        Assert.True(yard.IsOnRails);
        var push = tug.Forward;
        var energy = tug.StoredEnergyKJ;
        var mass = sector.MovingMassKg(yard);
        Assert.Equal(yard.MassKg + tug.MassKg, mass, 6);

        var velocity = yard.Velocity;
        for (var i = 0; i < 20; i++)
        {
            yard.Controls = PushAlong(yard, push);
            sector.Step();
        }

        Assert.False(yard.IsOnRails);
        Assert.True(yard.TugThrustN > 0);
        Assert.Equal(yard.Id, tug.DockedToCraftId); // it rode along
        var gained = (yard.Velocity - velocity).X * push.X + (yard.Velocity - velocity).Y * push.Y;
        var expected = tug.Stats.TotalThrustN / mass * 1.0; // 20 ticks = 1 s at full thrust
        Assert.True(gained > expected * 0.5, $"gained {gained:0.00} m/s, expected about {expected:0.00}");
        Assert.True(tug.StoredEnergyKJ < energy || tug.Stats.PowerGenerationKW > 0); // the tug paid for it
    }

    [Fact]
    public void ATugFacingAway_DoesntHelp()
    {
        var (sector, yard, tug) = YardWithTug();
        var away = tug.Forward * -1;
        for (var i = 0; i < 20; i++)
        {
            yard.Controls = PushAlong(yard, away);
            sector.Step();
        }
        Assert.True(yard.IsOnRails);
        Assert.Equal(0, yard.TugThrustN);
    }

    [Fact]
    public void AShipOnAJob_IsNotATug()
    {
        var (sector, yard, tug) = YardWithTug();
        Assert.True(SectorState.IsTug(tug));
        var rock = TestData.HomeRocks(sector)[0];
        sector.StartJob(tug.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25));
        Assert.False(SectorState.IsTug(tug));
    }
}
