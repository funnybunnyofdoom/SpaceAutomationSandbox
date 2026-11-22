using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>New body types (#16) and ice → water + fuel (#5).</summary>
public class BodiesAndIceTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    [Fact]
    public void System_HasAStarAtTheCentre_AndFourPlanets_ABelt_AndMoons()
    {
        var sector = SectorGenerator.Generate(NewGame.DefaultSeed);
        var star = sector.Bodies[0];
        Assert.Equal(BodyKind.Star, star.Kind);
        Assert.Null(star.Orbit);
        Assert.False(star.IsMineable);

        var aroundStar = sector.Bodies.Where(b => b.Orbit?.Parent == star.Id).ToList();
        Assert.Equal(new[] { "Home", "Cinder", "Giant", "Rime" }, aroundStar.Where(b => b.Kind != BodyKind.Asteroid).Select(b => b.Name));
        Assert.Equal(SectorGenerator.BeltAsteroidCount, aroundStar.Count(b => b.Name.StartsWith("Belt-")));
        Assert.Equal(SectorGenerator.HomeOrbitRadiusM, sector.Bodies.Single(b => b.Name == "Home").Orbit!.RadiusM);

        var giant = sector.Bodies.Single(b => b.Kind == BodyKind.GasGiant);
        Assert.False(giant.IsMineable);
        Assert.Equal(2, sector.Bodies.Count(b => b.Orbit?.Parent == giant.Id && b.Kind == BodyKind.Moon));

        var moon = sector.Bodies.Single(b => b.Name == "Moon");
        Assert.True(moon.Deposit("iron_ore") > 0 && moon.Deposit("silicate") > 0);
        Assert.Equal(SectorGenerator.IceIceKg, sector.Bodies.Single(b => b.Name == "Ice-1").DepositsKg["ice"]); // the deposit; its bulk is ice too (#27)
        Assert.True(sector.Bodies.Single(b => b.Name == "Rime").Deposit("ice") > 0);

        // Orbits around the same parent never overlap, and every orbit clears its parent.
        foreach (var group in sector.Bodies.Where(b => b.Orbit is not null).GroupBy(b => b.Orbit!.Parent))
        {
            var parent = sector.GetBody(group.Key);
            var rings = group.Where(b => !b.Name.StartsWith("Belt-"))
                .Select(b => (Low: b.Orbit!.RadiusM - b.RadiusM, High: b.Orbit.RadiusM + b.RadiusM)).OrderBy(r => r.Low).ToList();
            Assert.All(rings, r => Assert.True(r.Low > parent.RadiusM, $"an orbit around {parent.Name} hits it"));
            Assert.True(rings.Zip(rings.Skip(1)).All(pair => pair.First.High < pair.Second.Low), $"orbits around {parent.Name} overlap");
        }
    }

    [Fact]
    public void Planets_MoveSlowly_SoTheHomeNeighbourhoodFeelsTheSame()
    {
        var sector = SectorGenerator.Generate(NewGame.DefaultSeed);
        foreach (var planet in sector.Bodies.Where(b => b.Orbit?.Parent == sector.Bodies[0].Id))
            Assert.InRange(sector.GetVelocityAt(planet.Id, 0).Length, 5, 15);
    }

    [Fact]
    public void TheHomeNeighbourhood_IsUnchanged_ByTheStarSystem()
    {
        var sector = SectorGenerator.Generate(NewGame.DefaultSeed);
        var home = SectorGenerator.HomePlanet(sector);
        Assert.Equal(new BodyId(2), home.Id); // the star is body 1
        // Same draws as before #53 (orbit radii relative to the home planet for this seed).
        var rock1 = sector.Bodies.Single(b => b.Name == "Rock-1");
        Assert.Equal(home.Id, rock1.Orbit!.Parent);
        Assert.InRange(rock1.Orbit.RadiusM, 22_000, 30_000);
    }

    [Fact]
    public void Ice_RefinesIntoWaterAndFuel_ConservingMass()
    {
        var recipe = TestData.Recipes().Recipes.Single(r => r.Id == "process_ice");
        Assert.Equal(recipe.InputsKg!.Values.Sum(), recipe.OutputsKg!.Values.Sum());

        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var water = yard.Inventory["water"];
        var fuel = yard.Inventory["fuel"];
        yard.Inventory.Add("ice", 2_000);
        for (var i = 0; i < 20 * FixedStepClock.TicksPerSecond; i++) game.Sector.Step();

        Assert.Equal(0, yard.Inventory["ice"]);
        Assert.Equal(water + 1_200, yard.Inventory["water"]);
        Assert.Equal(fuel + 600, yard.Inventory["fuel"]);
    }

    [Fact]
    public void GasGiant_CantBeAnchoredTo_OrBuiltOn()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var giant = sector.Bodies.Single(b => b.Kind == BodyKind.GasGiant);
        var ship = sector.GetCraft(game.PlayerCraftId);
        TestData.LandAt(sector, ship, giant);
        ship.HeadingRad = ship.PreviousHeadingRad = System.Math.PI; // clamp side (rear) toward the giant, which is below
        sector.Step();

        var anchor = sector.TryAnchor(ship.Id);
        Assert.False(anchor.Done);
        Assert.Contains("gas giant", anchor.Message);
    }
}
