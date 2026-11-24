using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#54 further star systems, #22 warp gates, #23 freight through them.</summary>
public class WarpTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    private static ShipDesign GateDesign(bool power = true)
    {
        var cells = new List<DesignCell>
        {
            new(new GridPos(0, 0), "warp_gate"),
            new(new GridPos(0, -1), "docking_port"),
            new(new GridPos(0, 1), "docking_port", 2),
            new(new GridPos(1, 0), "storage_silo"),
        };
        if (power)
        {
            cells.Add(new DesignCell(new GridPos(-1, 0), "solar_panel_mk2"));
            cells.Add(new DesignCell(new GridPos(-1, -1), "battery_mk2"));
            cells.Add(new DesignCell(new GridPos(-1, 1), "battery_mk2"));
        }
        return new ShipDesign("gate", "Gate", cells);
    }

    /// <summary>A gate station deployed beside the yard, its stock holding its own bill (to open a system).</summary>
    private static CraftState DeployGate(SectorState sector, CraftState yard, bool power = true, bool stocked = true)
    {
        var design = GateDesign(power);
        Assert.Equal(DesignRole.Station, design.SuggestedRole(Catalog));
        var bill = design.ComputeBillOfMaterials(Catalog).ToWholeKg();
        var kit = new ConstructionKit(new OrderId(sector.NextOrderId), "Gate", design, design.ComputeStats(Catalog), bill);
        sector.RestoreKit(yard.Id, kit);
        sector.RestoreNextOrderId(sector.NextOrderId + 1);
        var before = sector.Crafts.Select(c => c.Id).ToHashSet();
        Assert.True(sector.TryDeployKit(yard.Id, kit.Id).Done);
        var gate = sector.Crafts.Single(c => !before.Contains(c.Id));
        if (stocked) foreach (var (material, kg) in bill) sector.DebugAddStock(gate.Id, material, kg);
        gate.StoredEnergyKJ = gate.Stats.EnergyCapacityKJ;
        return gate;
    }

    private static CraftState DockedShip(SectorState sector, CraftState at, string name, int port)
    {
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), at.Position, 0);
        sector.AddCraft(ship);
        ship.Position = sector.BerthPosition(at, port, ship.CollisionRadiusM, at.Position);
        ship.Velocity = at.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        return ship;
    }

    private static bool Run(SectorState sector, Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (done()) return true;
            sector.Step();
        }
        return done();
    }

    [Fact]
    public void FirstJump_OpensANewStarSystem_AndBuildsTheFarGate_WithMassExact()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var gate = DeployGate(sector, yard);
        var ship = DockedShip(sector, gate, "Ship", 0);
        var home = sector.StarAt(ship.Position)!;
        var massBefore = sector.ComputeMassLedger().TotalKg;
        var stockBefore = gate.Inventory.TotalKg;

        Assert.True(sector.RequestJump(ship.Id).Done);
        Assert.True(Run(sector, () => sector.StarAt(ship.Position)!.Id != home.Id, TicksPerMinute), ship.GateStatus);

        Assert.Equal(2, sector.Stars.Count);
        var far = sector.GetCraft(gate.GateLinkId);
        Assert.Equal(gate.Id, far.GateLinkId);
        Assert.Equal(gate.Design.Id, far.Design.Id);
        Assert.Equal(far.Id, ship.DockedToCraftId);
        Assert.Equal(stockBefore - far.Stats.TotalMassKg, gate.Inventory.TotalKg, 0);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
        // The new system: its own star, unsurveyed bodies, power from its own sun.
        var star = sector.StarAt(far.Position)!;
        Assert.NotEqual(home.Id, star.Id);
        Assert.True(star.Orbit!.RadiusM >= SectorGenerator.SystemSpacingM);
        Assert.Contains(sector.Bodies, b => b.IsMineable && !b.Surveyed && sector.StarOf(b.Id)?.Id == star.Id);
        Assert.True(sector.SolarFactorAt(far.Position) > 0.1);
    }

    [Fact]
    public void WithoutItsBillInStock_TheFirstJumpWaits_AndSaysWhatItNeeds()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var gate = DeployGate(sector, sector.GetCraft(game.ShipyardId), stocked: false);
        var ship = DockedShip(sector, gate, "Ship", 0);
        sector.RequestJump(ship.Id);
        for (var i = 0; i < 40; i++) sector.Step();
        Assert.Equal(gate.Id, ship.DockedToCraftId);
        Assert.Contains("opens a new star system", ship.GateStatus);
    }

    [Fact]
    public void OneCraftJumpsAtATime_TheRestQueue()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var gate = DeployGate(sector, sector.GetCraft(game.ShipyardId));
        var a = DockedShip(sector, gate, "A", 0);
        var b = DockedShip(sector, gate, "B", 1);
        sector.RequestJump(a.Id);
        sector.Step();
        sector.RequestJump(b.Id);
        sector.Step(); // a jumped on the first tick it could; b waits for the gate
        Assert.NotEqual(gate.Id, a.DockedToCraftId);
        Assert.Equal(gate.Id, b.DockedToCraftId);
        var firstReady = gate.GateReadyTick;
        Assert.True(Run(sector, () => b.DockedToCraftId != gate.Id, TicksPerMinute));
        Assert.True(sector.Tick >= firstReady); // b went only once the gate was ready again
        Assert.True(gate.GateReadyTick - firstReady >= SectorState.GateCooldownS * FixedStepClock.TicksPerSecond);
    }

    [Fact]
    public void AGateWithoutPower_Charges_AndSaysSo()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var gate = DeployGate(sector, sector.GetCraft(game.ShipyardId), power: false);
        var ship = DockedShip(sector, gate, "Ship", 0);
        ship.StoredEnergyKJ = 0; // a docked ship's battery would help the gate (one shared pool, #75)
        sector.RequestJump(ship.Id);
        for (var i = 0; i < 40; i++) sector.Step();
        Assert.Equal(gate.Id, ship.DockedToCraftId);
        Assert.Contains("charging", ship.GateStatus);
    }

    [Fact]
    public void Freight_ToAStationInAnotherSystem_GoesThroughTheGate_ByItself()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var gate = DeployGate(sector, yard);
        var opener = DockedShip(sector, gate, "Opener", 0);
        sector.RequestJump(opener.Id);
        Assert.True(Run(sector, () => !gate.GateLinkId.IsNone, TicksPerMinute));
        var far = sector.GetCraft(gate.GateLinkId);
        var hauler = DockedShip(sector, yard, "Hauler", 1);
        var massBefore = sector.ComputeMassLedger().TotalKg;
        var job = new HaulJob(yard.Id, "metal", far.Id, 5_000, 0);
        Assert.True(sector.StartJob(hauler.Id, job).Done);
        var sawGate = false;
        Assert.True(Run(sector, () =>
        {
            sawGate |= job.Status.Contains(gate.Name);
            return job.DeliveredKg > 0;
        }, 30 * TicksPerMinute), job.Status);

        Assert.True(sawGate);
        Assert.True(far.Inventory["metal"] > 0);
        Assert.True(hauler.Jumps >= 1);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void ATargetInASystemWithNoGateRoute_WaitsWithTheReason()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var (_, planet) = SectorGenerator.GenerateSystem(sector, 1); // a system with no gate to it
        sector.MarkSurveyed(planet.Id);
        var yard = sector.GetCraft(game.ShipyardId);
        var design = TestData.StarterShip();
        var miner = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(400, -400), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(miner);
        var job = new MiningJob(planet.Id, planet.DepositsKg.Keys.First(), yard.Id, 0.25);
        Assert.True(sector.StartJob(miner.Id, job).Done);
        sector.Step();
        Assert.Contains("no warp gate route", job.Status);
        Assert.Null(miner.Transit);
    }

    [Fact]
    public void SaveLoad_WithASecondSystem_AndAQueue_ContinuesExactly()
    {
        SectorState Make()
        {
            var game = TestData.NewGame();
            var s = game.Sector;
            var gate = DeployGate(s, s.GetCraft(game.ShipyardId));
            var a = DockedShip(s, gate, "A", 0);
            var b = DockedShip(s, gate, "B", 1);
            s.RequestJump(a.Id);
            s.RequestJump(b.Id);
            return s;
        }
        var reference = Make();
        var sector = Make();
        for (var i = 0; i < 60; i++) { reference.Step(); sector.Step(); } // a has jumped (and opened the system), b is queued
        Assert.Equal(2, sector.Stars.Count);
        var json = SaveGameSerializer.ToJson(sector, sector.PilotCraftId);
        Assert.Contains("\"jumpRequestTick\"", json);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        for (var i = 0; i < 2 * TicksPerMinute; i++) { reference.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(reference, reference.PilotCraftId), SaveGameSerializer.ToJson(loaded, loaded.PilotCraftId));
    }
}
