using System;
using System.Linq;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class SaveLoadTests
{
    /// <summary>New game like Main builds it: seeded sector, starter ship above the primary, targeting the asteroid.</summary>
    private static (SectorState Sector, CraftId Player) NewGame(ulong seed = 20261120)
    {
        var sector = SectorGenerator.Generate(seed);
        var design = TestData.StarterShip();
        var craft = new CraftState(sector.AllocateCraftId(), "Player", design, design.ComputeStats(TestData.Catalog()),
            SectorGenerator.PlayerSpawnPoint(sector), 0);
        sector.AddCraft(craft);
        sector.SetTarget(craft.Id, sector.Bodies.First(b => b.IsMineable).Id);
        return (sector, craft.Id);
    }

    /// <summary>Deterministic "pilot": controls depend only on the tick number, so two runs get identical input.</summary>
    private static void Fly(SectorState sector, CraftId player, long untilTick)
    {
        var craft = sector.GetCraft(player);
        while (sector.Tick < untilTick)
        {
            var phase = sector.Tick / 37 % 4;
            craft.Controls = new ShipControls(phase == 0 ? 1 : 0.3, phase == 1 ? 1 : phase == 2 ? -0.5 : 0, phase == 3);
            sector.Step();
        }
    }

    private static LoadedGame RoundTrip(SectorState sector, CraftId player) =>
        SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, player), TestData.Catalog());

    /// <summary>Text of the state, written independently of the serializer so the two check each other.</summary>
    private static string Describe(SectorState s) => string.Join("\n",
        new[] { $"seed={s.Seed} tick={s.Tick}" }
            .Concat(s.Bodies.Select(b => $"{b.Id}|{b.Name}|{b.Kind}|{b.MassKg:R}|{b.DensityKgPerM3:R}|{b.IsMineable}|{b.Orbit}"))
            .Concat(s.Crafts.Select(c =>
                $"{c.Id}|{c.Name}|{c.Design.Id}|{string.Join(",", c.Design.Cells)}|{c.Position.X:R},{c.Position.Y:R}|" +
                $"{c.Velocity.X:R},{c.Velocity.Y:R}|{c.HeadingRad:R}|{c.Controls}|{c.TargetBodyId}|" +
                $"{c.LandedOnBodyId}|{c.LandedOffset.X:R},{c.LandedOffset.Y:R}")));

    [Fact]
    public void RoundTrip_PreservesTheWholeState()
    {
        var (sector, player) = NewGame();
        Fly(sector, player, 500);

        var loaded = RoundTrip(sector, player);

        Assert.Equal(Describe(sector), Describe(loaded.Sector));
        Assert.Equal(player, loaded.PlayerCraftId);
    }

    [Fact]
    public void SaveIsByteStable_AcrossSaveLoadSave()
    {
        var (sector, player) = NewGame();
        Fly(sector, player, 321);
        var first = SaveGameSerializer.ToJson(sector, player);

        var loaded = SaveGameSerializer.FromJson(first, TestData.Catalog());
        var second = SaveGameSerializer.ToJson(loaded.Sector, loaded.PlayerCraftId);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(777L)]
    public void SaveLoadContinue_MatchesNeverStopping(long saveAtTick)
    {
        // Reference run: 3000 ticks straight through.
        var (reference, refPlayer) = NewGame();
        Fly(reference, refPlayer, 3000);

        // Same game, saved and reloaded partway, then continued with the same input.
        var (sector, player) = NewGame();
        Fly(sector, player, saveAtTick);
        var loaded = RoundTrip(sector, player);
        Fly(loaded.Sector, loaded.PlayerCraftId, 3000);

        Assert.Equal(Describe(reference), Describe(loaded.Sector));
    }

    [Fact]
    public void Load_UsesSavedBodies_NotTheGenerator()
    {
        // A save is full state: if the generator is retuned, or a body has changed, the save still wins.
        var sector = new SectorState(20261120, tick: 99);
        sector.AddBody(new CelestialBodyState(new BodyId(5), "Custom", BodyKind.Planet, 1.25e12, 4000, false, null));
        sector.AddBody(new CelestialBodyState(new BodyId(9), "Moonlet", BodyKind.Asteroid, 3.5e8, 2100, true,
            new CircularOrbit(new BodyId(5), 12_345, 4321, 1.5)));

        var loaded = RoundTrip(sector, CraftId.None);

        Assert.Equal(Describe(sector), Describe(loaded.Sector));
        Assert.True(loaded.PlayerCraftId.IsNone);
    }

    [Fact]
    public void Load_KeepsIdsStable_AndNewIdsDontCollide()
    {
        var (sector, player) = NewGame();
        var loaded = RoundTrip(sector, player);

        Assert.Equal(sector.Bodies.Select(b => b.Id), loaded.Sector.Bodies.Select(b => b.Id));
        Assert.Equal(sector.Crafts.Select(c => c.Id), loaded.Sector.Crafts.Select(c => c.Id));
        Assert.DoesNotContain(loaded.Sector.AllocateCraftId(), loaded.Sector.Crafts.Select(c => c.Id));
        Assert.DoesNotContain(loaded.Sector.AllocateBodyId(), loaded.Sector.Bodies.Select(b => b.Id));
    }

    [Fact]
    public void Load_AcceptsBodiesListedChildBeforeParent()
    {
        var (sector, player) = NewGame();
        var json = SaveGameSerializer.ToJson(sector, player);
        // Swap the two body entries in the file: asteroid (child) now comes first.
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var bodies = doc["sector"]!["bodies"]!.AsArray();
        var first = bodies[0]!.DeepClone();
        bodies[0] = bodies[1]!.DeepClone();
        bodies[1] = first;

        var loaded = SaveGameSerializer.FromJson(doc.ToJsonString(), TestData.Catalog());

        Assert.Equal(Describe(sector), Describe(loaded.Sector));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "schemaVersion": 1, "game": "SomethingElse", "sector": {} }""")]
    [InlineData("""{ "schemaVersion": 0, "game": "SpaceAutomationSandbox", "sector": {} }""")]
    [InlineData("""{ "schemaVersion": 8, "game": "SpaceAutomationSandbox", "sector": {} }""")]
    public void Load_RejectsWrongOrForeignFiles(string json)
    {
        Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json, TestData.Catalog()));
    }

    [Theory]
    [InlineData("\"part\": \"cockpit\"", "\"part\": \"warp_core\"")] // design no longer valid
    [InlineData("\"targetBodyId\": 2", "\"targetBodyId\": 42")] // target missing
    [InlineData("\"playerCraftId\": 1", "\"playerCraftId\": 7")] // player missing
    [InlineData("\"parentId\": 1", "\"parentId\": 2")] // asteroid orbits itself
    [InlineData("\"tick\": 0", "\"tick\": -5")]
    public void Load_RejectsInconsistentSaves_WithAReason(string find, string replace)
    {
        var (sector, player) = NewGame();
        var json = SaveGameSerializer.ToJson(sector, player);
        Assert.Contains(find, json);

        var ex = Assert.Throws<FormatException>(() =>
            SaveGameSerializer.FromJson(json.Replace(find, replace), TestData.Catalog()));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
