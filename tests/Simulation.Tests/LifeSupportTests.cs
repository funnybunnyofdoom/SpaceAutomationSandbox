using System.Linq;
using System.Text.Json.Nodes;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Crew and life support (#11).</summary>
public class LifeSupportTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;
    private const int TicksPerHour = 3600 * TicksPerSecond;

    private static long Mass(SectorState sector) => sector.ComputeMassLedger().TotalKg;

    /// <summary>A station with a habitat on the yard's orbit, big enough to need crew: <paramref name="hulls"/> extra hull plates in a row.</summary>
    private static CraftState AddCrewedStation(SectorState sector, CraftState yard, int hulls, bool farm = false)
    {
        var cells = new System.Collections.Generic.List<DesignCell>
        {
            new(new GridPos(0, -1), "docking_port"), new(new GridPos(0, 0), "habitat"), new(new GridPos(1, 0), "storage_silo"),
            new(new GridPos(-1, 0), "solar_panel"), new(new GridPos(-2, 0), "solar_panel"), new(new GridPos(0, 1), "battery"),
        };
        if (farm) cells.Add(new DesignCell(new GridPos(1, 1), "farm"));
        for (var i = 0; i < hulls; i++) cells.Add(new DesignCell(new GridPos(2 + i, 0), "hull"));
        var design = new ShipDesign("outpost", "Outpost", cells);
        var station = new CraftState(sector.AllocateCraftId(), "Outpost", design, design.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(station);
        sector.PutOnRails(station.Id, yard.RailOrbit! with { PhaseAtZeroRad = yard.RailOrbit!.PhaseAtZeroRad + 0.4 });
        return station;
    }

    [Fact]
    public void SmallCraft_NeedNoCrew_BigOnesOnePerTenParts()
    {
        Assert.Equal(0, TestData.StarterShip().ComputeStats(Catalog).CrewRequired);
        var yard = TestData.StarterShipyard().ComputeStats(Catalog);
        Assert.Equal(16, yard.CellCount);
        Assert.Equal(2, yard.CrewRequired);
        Assert.Equal(6, yard.CrewCapacity);
    }

    [Fact]
    public void NewGame_YardHasACrew_FedFromItsStock_AndKeepsWorking()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        Assert.Equal(NewGame.StartingCrew, yard.Crew);
        var food = yard.Inventory["food"];
        var water = yard.Inventory["water"];
        var mass = Mass(sector);

        for (var i = 0; i < TicksPerHour; i++) sector.Step();

        Assert.True(SectorState.Operational(yard));
        Assert.InRange(food - yard.Inventory["food"], 3 * NewGame.StartingCrew - 1, 3 * yard.Crew + 1); // ~3 kg per person per hour (the crew grows meanwhile)
        Assert.InRange(water - yard.Inventory["water"], 6 * NewGame.StartingCrew - 1, 6 * yard.Crew + 1);
        Assert.Equal(mass, Mass(sector)); // eaten and drunk = consumed, in the ledger
    }

    [Fact]
    public void SuppliedHabitat_GrowsItsCrew_UpToCapacity()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        for (var i = 0; i < 25 * 60 * TicksPerSecond; i++) sector.Step(); // two growth periods
        Assert.Equal(NewGame.StartingCrew + 2, yard.Crew);
        for (var i = 0; i < 25 * 60 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(yard.Stats.CrewCapacity, yard.Crew); // full: no more
    }

    [Fact]
    public void WithoutFood_TheCrewStopWorking_AndNobodyDies()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { ["food"] = yard.Inventory["food"] });
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);

        for (var i = 0; i < 60 * TicksPerSecond; i++) sector.Step();

        Assert.False(SectorState.Operational(yard));
        Assert.Equal("no food", yard.CrewShortReason);
        Assert.Equal(0, yard.BuildQueue[0].ProgressKg); // the fabricator stopped
        Assert.Equal(NewGame.StartingCrew, yard.Crew);
        Assert.Contains("STOPPED: no food", SectorState.CrewStatus(yard));

        yard.Inventory.Add("food", 100);
        sector.Step();
        Assert.True(SectorState.Operational(yard));
    }

    [Fact]
    public void BigCraft_WithoutCrew_CantWork_UntilCrewMoveIn()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var outpost = AddCrewedStation(sector, yard, hulls: 12); // 18 parts: needs 2
        Assert.Equal(2, outpost.Stats.CrewRequired);
        sector.Step();
        Assert.Equal("needs 2 crew (has 0)", outpost.CrewShortReason);

        // A freighter with a habitat ferries two people over (crew only move across a dock).
        var ferryDesign = new ShipDesign("ferry", "Ferry", TestData.StarterShip().Cells
            .Select(c => c.PartId == "cargo" ? c with { PartId = "habitat" } : c));
        var ferry = new CraftState(sector.AllocateCraftId(), "Ferry", ferryDesign, ferryDesign.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(ferry);
        sector.RestoreDock(ferry.Id, yard.Id, sector.FreePorts(yard)[0]);
        Assert.False(sector.TryTransferCrew(yard.Id, outpost.Id, 1).Done); // not docked together
        Assert.True(sector.TryTransferCrew(yard.Id, ferry.Id, 2).Done);
        sector.TryUndock(ferry.Id);
        sector.RestoreDock(ferry.Id, outpost.Id, 0);
        Assert.True(sector.TryTransferCrew(ferry.Id, outpost.Id, 2).Done);
        outpost.Inventory.Add("food", 200);
        outpost.Inventory.Add("water", 400);
        sector.Step();

        Assert.True(SectorState.Operational(outpost));
        Assert.Equal(NewGame.StartingCrew - 2, yard.Crew);
        Assert.False(sector.TryTransferCrew(outpost.Id, ferry.Id, 7).Done); // only 2 there
    }

    [Fact]
    public void Farm_TurnsWaterIntoFood_ConservingMass()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var outpost = AddCrewedStation(sector, yard, hulls: 0, farm: true); // small: no crew needed, so the farm runs
        outpost.Inventory.Add("water", 1_000);
        var mass = Mass(sector);

        for (var i = 0; i < TicksPerHour; i++) sector.Step();

        Assert.Equal(72, outpost.Inventory["food"]); // 0.02 kg/s for an hour
        Assert.Equal(1_000 - 72, outpost.Inventory["water"]);
        Assert.Equal(mass, Mass(sector));
    }

    [Fact]
    public void Crew_SurviveSaveLoad_AndOldSavesNeedNoCrew()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        for (var i = 0; i < 777; i++) sector.Step();
        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        for (var i = 0; i < 2_000; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));

        var root = JsonNode.Parse(json)!.AsObject();
        root["schemaVersion"] = 13;
        foreach (var craft in root["sector"]!["crafts"]!.AsArray().OfType<JsonObject>()) { craft.Remove("crew"); }
        var old = SaveGameSerializer.FromJson(root.ToJsonString(), Catalog).Sector;
        var oldYard = old.GetCraft(game.ShipyardId);
        Assert.True(oldYard.LegacyNoCrew);
        old.Step();
        Assert.True(SectorState.Operational(oldYard));
    }
}
