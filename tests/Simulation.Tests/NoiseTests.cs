using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>What's heard (#129): the noise ring and the music's mood. Neither is saved or changes the game.</summary>
public sealed class NoiseTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond;

    [Fact]
    public void A_fight_makes_noises_and_a_combat_mood_but_saves_nothing_of_them()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        Assert.Equal(MusicMood.Calm, sector.MoodFor(sector.GetCraft(game.PlayerCraftId)));
        var at = sector.GetPosition(sector.Stars[0].Id) + new Vector2D(0, -3_000_000); // out of any gravity
        var ship = new CraftState(sector.AllocateCraftId(), "Gunship", SectorState.RaiderDesign, SectorState.RaiderDesign.ComputeStats(Catalog), at, 0);
        sector.AddCraft(ship);
        var faction = sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: 0);
        var raider = new CraftState(sector.AllocateCraftId(), "Raider", SectorState.RaiderDesign, SectorState.RaiderDesign.ComputeStats(Catalog), at + new Vector2D(0, -500), System.Math.PI) { FactionId = faction.Id };
        sector.AddCraft(raider);
        foreach (var craft in new[] { ship, raider }) { craft.Inventory.Add(SectorState.ShellMaterial, 200); craft.StoredEnergyKJ = craft.Stats.EnergyCapacityKJ; }
        sector.PilotCraftId = ship.Id;
        sector.Step();
        Assert.Equal(MusicMood.Combat, sector.MoodFor(ship));
        for (var i = 0; i < 20 * Second; i++) sector.Step();
        var noises = Enumerable.Range(0, (int)System.Math.Min(sector.NoiseCount, SectorState.NoisesKept)).Select(i => sector.NoiseAt(sector.NoiseCount - 1 - i).Kind).ToHashSet();
        Assert.Contains("gun_fire", noises);
        Assert.Contains("hit", noises);
        Assert.DoesNotContain("noise", SaveGameSerializer.ToJson(sector, ship.Id).ToLowerInvariant());
    }

    [Fact]
    public void Hazards_make_a_tension_mood_in_their_system()
    {
        var game = TestData.NewGame();
        var ship = game.Sector.GetCraft(game.PlayerCraftId);
        game.Sector.DebugHazard(HazardKind.SolarFlare, SectorState.ForecastTicks);
        Assert.Equal(MusicMood.Tension, game.Sector.MoodFor(ship));
    }

    [Fact]
    public void The_ring_keeps_the_latest()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        for (var i = 0; i < 300; i++) sector.DebugNoise("hit", Vector2D.Zero);
        Assert.Equal(300, sector.NoiseCount);
        Assert.Equal("hit", sector.NoiseAt(299).Kind);
    }
}
