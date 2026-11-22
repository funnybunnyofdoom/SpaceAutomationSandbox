using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>New body types (#16) and ice → water + fuel (#5).</summary>
public class BodiesAndIceTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    [Fact]
    public void Sector_HasAMoon_AnIceAsteroid_AndAGasGiant_AllOrbitingThePrimary()
    {
        var sector = SectorGenerator.Generate(NewGame.DefaultSeed);
        var primary = sector.Bodies.Single(b => b.Kind == BodyKind.Planet);

        var moon = sector.Bodies.Single(b => b.Kind == BodyKind.Moon);
        Assert.True(moon.IsMineable);
        Assert.True(moon.Deposit("iron_ore") > 0 && moon.Deposit("silicate") > 0);

        var ice = sector.Bodies.Single(b => b.Name == "Ice-1");
        Assert.Equal(SectorGenerator.IceIceKg, ice.Deposit("ice"));

        var giant = sector.Bodies.Single(b => b.Kind == BodyKind.GasGiant);
        Assert.False(giant.IsMineable);
        Assert.True(giant.RadiusM > primary.RadiusM);

        foreach (var body in new[] { moon, ice, giant })
        {
            Assert.Equal(primary.Id, body.Orbit!.Parent);
            Assert.True(body.Orbit.RadiusM > primary.RadiusM + body.RadiusM, $"{body.Name} must clear the primary");
        }
        // No two orbits overlap (bodies never pass through each other).
        var ring = sector.Bodies.Where(b => b.Orbit is not null).Select(b => (Low: b.Orbit!.RadiusM - b.RadiusM, High: b.Orbit.RadiusM + b.RadiusM))
            .OrderBy(r => r.Low).ToList();
        Assert.True(ring.Zip(ring.Skip(1)).All(p => p.First.High < p.Second.Low));
    }

    [Fact]
    public void OlderBodies_AreUnchanged_ByTheNewOnes()
    {
        var sector = SectorGenerator.Generate(NewGame.DefaultSeed);
        var rock1 = sector.Bodies.Single(b => b.Name == "Rock-1");
        Assert.Equal(new BodyId(2), rock1.Id);
        Assert.Equal(new BodyId(4), sector.Bodies.Single(b => b.Name == "Rock-3").Id); // ids keep their order: new bodies come after
    }

    [Fact]
    public void Ice_RefinesIntoWaterAndFuel_ConservingMass()
    {
        var recipe = TestData.Recipes().Recipes.Single(r => r.Id == "process_ice");
        Assert.Equal(recipe.InputsKg!.Values.Sum(), recipe.OutputsKg!.Values.Sum());

        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var water = yard.Inventory["water"];
        yard.Inventory.Add("ice", 2_000);
        for (var i = 0; i < 20 * FixedStepClock.TicksPerSecond; i++) game.Sector.Step();

        Assert.Equal(0, yard.Inventory["ice"]);
        Assert.Equal(water + 1_200, yard.Inventory["water"]);
        Assert.Equal(600, yard.Inventory["fuel"]);
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
