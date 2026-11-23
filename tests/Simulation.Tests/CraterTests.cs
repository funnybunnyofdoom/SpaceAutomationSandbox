using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#28: mining digs craters where it happens; they merge, are capped, and are saved.</summary>
public class CraterTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    [Fact]
    public void Mining_DigsACrater_WhereTheShipIs_WithTheMassItTook()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = TestData.HomeRocks(sector)[0];
        var ship = sector.GetCraft(game.PlayerCraftId);
        TestData.LandAt(sector, ship, rock);
        Assert.True(sector.TryAnchor(ship.Id).Done);
        var before = rock.DepositsKg.Values.Sum() + rock.BulkKg;

        for (var i = 0; i < TicksPerMinute; i++) sector.Step();

        var crater = Assert.Single(rock.Craters);
        var mined = before - rock.DepositsKg.Values.Sum() - rock.BulkKg;
        Assert.Equal(mined, crater.MassKg);
        Assert.Equal(ship.Inventory.TotalKg, crater.MassKg);
        Assert.Equal(Math.Atan2(ship.LandedOffset.Y, ship.LandedOffset.X), crater.BearingRad, 9);
        Assert.True(rock.CraterRadiusM(crater.MassKg) > 1);
    }

    [Fact]
    public void DiggingNearACrater_Deepens_It_FarAway_StartsAnother_AndThereAreAtMost64()
    {
        var rock = TestData.HomeRocks(TestData.NewGame().Sector)[0];
        rock.Dig(0, 1_000_000);
        rock.Dig(0.001, 1_000_000); // well inside the first one's rim
        Assert.Single(rock.Craters);
        Assert.Equal(2_000_000, rock.Craters[0].MassKg);
        rock.Dig(Math.PI, 500);
        Assert.Equal(2, rock.Craters.Count);
        for (var i = 0; i < 200; i++) rock.Dig(i * 0.031, 10);
        Assert.Equal(CelestialBodyState.MaxCraters, rock.Craters.Count);
        Assert.Equal(2_000_000 + 500 + 200 * 10, rock.Craters.Sum(c => c.MassKg)); // nothing lost when they merge
    }

    [Fact]
    public void Craters_SurviveSaveLoad()
    {
        var sector = TestData.NewGame().Sector;
        var rock = TestData.HomeRocks(sector)[0];
        rock.Dig(1.0, 12_345);
        rock.Dig(-2.0, 777);
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), Catalog).Sector;
        Assert.Equal(rock.Craters, loaded.GetBody(rock.Id).Craters);
    }
}
