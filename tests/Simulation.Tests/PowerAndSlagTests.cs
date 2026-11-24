using System.Linq;
using System.Text.Json.Nodes;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Power: solar, batteries, machine drain, docked recharge (#10). Slag ejection and its ledger (#7).</summary>
public class PowerAndSlagTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;

    private static CraftState AddCraft(SectorState sector, string name, params (int X, int Y, string Part)[] cells)
    {
        var design = new ShipDesign(name.ToLowerInvariant(), name, cells.Select(c => new DesignCell(new GridPos(c.X, c.Y), c.Part)).ToArray());
        // Above the home planet (full sunlight), moving with it, clear of its surface and the Moon's orbit.
        var craft = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog),
            SectorGenerator.PlayerSpawnPoint(sector) + new Vector2D(0, -3_000 - 400 * sector.Crafts.Count), 0)
        {
            Velocity = SectorGenerator.PlayerSpawnVelocity(sector),
        };
        sector.AddCraft(craft);
        return craft;
    }

    [Fact]
    public void Thrust_DrainsTheBattery_AndStopsWhenItIsEmpty()
    {
        var sector = TestData.NewGame().Sector;
        var ship = AddCraft(sector, "Tug", (0, 0, "cockpit"), (0, 1, "battery"), (0, 2, "thruster"));
        var use = ship.Stats.ThrustPowerKW;
        Assert.True(use > 0);
        ship.Controls = new ShipControls(1, 0, false);

        var seconds = ship.Stats.EnergyCapacityKJ / use;  // the battery lasts exactly this long at full throttle
        for (var i = 0; i < (int)(seconds * TicksPerSecond) - 1; i++) sector.Step();
        Assert.False(ship.PowerShort);
        var speed = ship.Velocity.Length;
        Assert.True(speed > 0);

        for (var i = 0; i < 5; i++) sector.Step();
        Assert.True(ship.PowerShort);
        Assert.Contains("NO POWER", SectorState.PowerStatus(ship));
        Assert.True(ship.StoredEnergyKJ < use * FixedStepClock.TickSeconds);
        var coasting = ship.Velocity.Length;
        sector.Step();
        Assert.Equal(coasting, ship.Velocity.Length, 6); // no power: no thrust, it coasts
    }

    [Fact]
    public void Solar_RechargesTheBattery_UpToItsCapacity()
    {
        var sector = TestData.NewGame().Sector;
        var station = AddCraft(sector, "Panel", (0, 0, "battery"), (1, 0, "solar_panel"));
        var home = SectorGenerator.HomePlanet(sector);
        var radius = home.RadiusM + 2_000; // a parked orbit: without thrusters it would fall under gravity (#32)
        // Starting on the sunny side, so Home's shadow (#76) stays out of the way for the first part of the test.
        var period = SectorState.CircularOrbitPeriodS(home, radius, 1_800);
        var phase = TestData.SunnyPhase(sector, 2_000, period);
        sector.PutOnRails(station.Id, new CircularOrbit(home.Id, radius, period, phase));
        station.StoredEnergyKJ = 0;

        for (var i = 0; i < 10 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(10 * station.Stats.PowerGenerationKW, station.StoredEnergyKJ, 6);

        for (var i = 0; i < 1000 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(station.Stats.EnergyCapacityKJ, station.StoredEnergyKJ);
    }

    [Fact]
    public void DockedShips_ShareOnePowerPoolWithTheirStation()
    {
        // #75: everything docked together shares one pool; batteries end each tick at the same charge level.
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);
        sector.RestoreDock(ship.Id, yard.Id, 0);
        ship.StoredEnergyKJ = 0;
        var before = yard.StoredEnergyKJ + ship.StoredEnergyKJ;

        for (var i = 0; i < 20 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(yard.StoredEnergyKJ / yard.Stats.EnergyCapacityKJ, ship.StoredEnergyKJ / ship.Stats.EnergyCapacityKJ, 6);
        // Both craft's solar went in; the yard's habitat (#11) took its share out.
        var expected = before + 20 * (yard.Stats.PowerGenerationKW - yard.Stats.HabitatPowerKW + ship.Stats.PowerGenerationKW);
        Assert.Equal(System.Math.Min(expected, yard.Stats.EnergyCapacityKJ + ship.Stats.EnergyCapacityKJ), yard.StoredEnergyKJ + ship.StoredEnergyKJ, 3);
    }

    [Fact]
    public void Fabricator_MakesNoProgress_WithoutPower()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        Assert.True(sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog).Accepted);
        // The starter yard's solar covers its fabricator; a depot with only an empty battery can't build.
        Assert.True(yard.Stats.FabricatorPowerKW <= yard.Stats.PowerGenerationKW, "starter yard can always fabricate on solar");

        var depot = AddCraft(sector, "Depot", (0, 0, "fabricator"), (1, 0, "storage_silo"), (2, 0, "battery"));
        depot.StoredEnergyKJ = 0;
        foreach (var (m, kg) in TestData.StarterShip().ComputeBillOfMaterials(Catalog).ToWholeKg()) depot.Inventory.Add(m, kg);
        Assert.True(sector.OrderBuild(depot.Id, TestData.StarterShip(), Catalog).Accepted);
        for (var i = 0; i < 10 * TicksPerSecond; i++) sector.Step();

        Assert.Equal(0, depot.BuildQueue[0].ProgressKg);
        Assert.True(depot.PowerShort);
        Assert.True(yard.BuildQueue[0].ProgressKg > 0);
    }

    [Fact]
    public void Ejector_KeepsTheSlagReserve_AndRecordsWhatItThrewOut()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        Assert.True(yard.Stats.EjectionRateKgPerS > 0);
        yard.Inventory.Add("slag", SectorState.SlagReserveKg); // well above the reserve now
        var before = yard.Inventory["slag"];

        for (var i = 0; i < 10 * TicksPerSecond; i++) sector.Step();
        var ejected = sector.EjectedKg["slag"];
        Assert.Equal((long)(10 * yard.Stats.EjectionRateKgPerS), ejected);
        Assert.Equal(before, yard.Inventory["slag"] + ejected); // nothing lost: the ledger holds the rest

        for (var i = 0; i < 1000 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(SectorState.SlagReserveKg, yard.Inventory["slag"]);
        Assert.Equal(before - SectorState.SlagReserveKg, sector.EjectedKg["slag"]);
    }

    [Fact]
    public void PowerAndEjection_SurviveSaveLoad()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);
        yard.Inventory.Add("slag", 30_000);
        ship.Controls = new ShipControls(1, 0, false);
        for (var i = 0; i < 3 * TicksPerSecond + 7; i++) sector.Step();

        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(ship.StoredEnergyKJ, loaded.GetCraft(ship.Id).StoredEnergyKJ);
        Assert.Equal(sector.EjectedKg["slag"], loaded.EjectedKg["slag"]);
        Assert.Equal(yard.EjectionRemainderKg, loaded.GetCraft(yard.Id).EjectionRemainderKg);
        Assert.False(loaded.GetCraft(ship.Id).LegacyFreePower);
        Assert.DoesNotContain("legacyFreePower", json);

        for (var i = 0; i < 100; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
    }

    [Fact]
    public void Version9Save_GetsFreePower_AndFullBatteries()
    {
        var game = TestData.NewGame();
        var ship = game.Sector.GetCraft(game.PlayerCraftId);
        var root = JsonNode.Parse(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId))!.AsObject();
        root["schemaVersion"] = 9;
        foreach (var craft in root["sector"]!["crafts"]!.AsArray().OfType<JsonObject>()) craft.Remove("storedEnergyKJ");

        var loaded = SaveGameSerializer.FromJson(root.ToJsonString(), Catalog).Sector;
        var old = loaded.GetCraft(ship.Id);
        Assert.True(old.LegacyFreePower);
        Assert.Equal(old.Stats.EnergyCapacityKJ, old.StoredEnergyKJ);

        old.StoredEnergyKJ = 0;
        old.Controls = new ShipControls(1, 0, false);
        loaded.Step();
        Assert.False(old.PowerShort);
        Assert.Contains("unlimited", SectorState.PowerStatus(old));
    }

    [Fact]
    public void StarterParts_HaveTheAgreedPowerAndSlagNumbers()
    {
        Assert.Equal(100, Catalog.Get("solar_panel").PowerGenerationKW);
        Assert.Equal(100, Catalog.Get("slag_ejector").EjectionRateKgPerS);
        Assert.Equal(200, Catalog.Get("hull").MaterialsKg["slag"]);
        Assert.Equal(250, Catalog.Get("foundation").MaterialsKg["slag"]);
        foreach (var id in new[] { "thruster", "drill", "refinery", "fabricator", "slag_ejector" })
            Assert.True(Catalog.Get(id).PowerUseKW > 0, id);
    }
}
