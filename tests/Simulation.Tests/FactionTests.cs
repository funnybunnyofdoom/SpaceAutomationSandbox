using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#36 civilizations and laws: encounters, attitude, law effects, raids on gates, rebuilding, saves.</summary>
public sealed class FactionTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    private static ShipDesign GateDesign(int defenses = 0)
    {
        var cells = new List<DesignCell>
        {
            new(new GridPos(0, 0), "warp_gate"),
            new(new GridPos(0, -1), "docking_port"),
            new(new GridPos(0, 1), "docking_port", 2),
            new(new GridPos(1, 0), "storage_silo"),
            new(new GridPos(-1, 0), "solar_panel_mk2"),
            new(new GridPos(-1, -1), "battery_mk2"),
            new(new GridPos(-1, 1), "battery_mk2"),
        };
        for (var i = 0; i < defenses; i++) cells.Add(new DesignCell(new GridPos(2 + i, 0), "gate_defense"));
        return new ShipDesign("gate", "Gate", cells);
    }

    private static CraftState DeployGate(SectorState sector, CraftState yard, int defenses = 0)
    {
        var design = GateDesign(defenses);
        var bill = design.ComputeBillOfMaterials(Catalog).ToWholeKg();
        var kit = new ConstructionKit(new OrderId(sector.NextOrderId), "Gate", design, design.ComputeStats(Catalog), bill);
        sector.RestoreKit(yard.Id, kit);
        sector.RestoreNextOrderId(sector.NextOrderId + 1);
        var before = sector.Crafts.Select(c => c.Id).ToHashSet();
        Assert.True(sector.TryDeployKit(yard.Id, kit.Id).Done);
        var gate = sector.Crafts.Single(c => !before.Contains(c.Id));
        foreach (var (material, kg) in bill) sector.DebugAddStock(gate.Id, material, kg);
        gate.StoredEnergyKJ = gate.Stats.EnergyCapacityKJ;
        return gate;
    }

    private static CraftState DockedShip(SectorState sector, CraftState at, int port)
    {
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), $"Ship{port}", design, design.ComputeStats(Catalog), at.Position, 0);
        sector.AddCraft(ship);
        ship.Position = sector.BerthPosition(at, port, ship.CollisionRadiusM, at.Position);
        ship.Velocity = at.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        return ship;
    }

    private static void Run(SectorState sector, Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks && !done(); i++) sector.Step();
    }

    /// <summary>A gate with a far end in a second system, and a ship sent through (docked at the far gate).</summary>
    private static (SectorState Sector, CraftState Near, CraftState Far, CelestialBodyState Star, CraftState Ship) OpenedSystem(int farDefenses = 0)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var near = DeployGate(sector, sector.GetCraft(game.ShipyardId), farDefenses);
        var ship = DockedShip(sector, near, 0);
        var home = sector.StarAt(ship.Position)!.Id;
        Assert.True(sector.RequestJump(ship.Id).Done);
        Run(sector, () => sector.StarAt(ship.Position)!.Id != home, TicksPerMinute);
        var far = sector.GetCraft(near.GateLinkId);
        far.StoredEnergyKJ = far.Stats.EnergyCapacityKJ;
        return (sector, near, far, sector.StarAt(far.Position)!, ship);
    }

    [Fact]
    public void About_one_new_system_in_four_is_claimed_and_never_home()
    {
        var claimed = Enumerable.Range(1, 400).Count(i => SectorState.CivilizationAt(20261120, i, out _));
        Assert.InRange(claimed, 70, 130);
        Assert.False(SectorState.CivilizationAt(20261120, 0, out _));
        Assert.Equal(SectorState.CivilizationAt(5, 3, out _), SectorState.CivilizationAt(5, 3, out _));
    }

    [Fact]
    public void Attitude_drifts_towards_what_the_culture_thinks_of_the_laws()
    {
        var (sector, _, _, star, _) = OpenedSystem();
        var faction = sector.MeetCivilization(star, cultureId: "territorial", baseline: 0);
        Assert.Contains(sector.Events, e => e.Text.Contains(faction.Name) && e.Text.Contains("Respect claimed systems"));
        Assert.Equal(Disposition.Neutral, faction.Disposition);

        Assert.True(sector.SetLaw(Laws.RespectClaims, true).Done);
        Assert.Equal(2 * Faction.PointsPerStance, faction.TargetAttitude(sector.ActiveLaws));
        Run(sector, () => faction.Disposition == Disposition.Friendly, 20 * TicksPerMinute);
        Assert.Equal(Disposition.Friendly, faction.Disposition);
        Assert.Contains(sector.Events, e => e.Text.Contains("now friendly"));

        Assert.True(sector.SetLaw(Laws.RespectClaims, false).Done);
        Assert.True(sector.SetLaw(Laws.ArmedGates, true).Done); // territorial: dislikes armed gates
        Assert.Equal(-Faction.PointsPerStance, faction.TargetAttitude(sector.ActiveLaws));
        Assert.False(sector.SetLaw(Laws.ArmedGates, true).Done); // already in force
    }

    [Fact]
    public void Respect_claimed_systems_stops_mining_there()
    {
        var (sector, _, _, star, ship) = OpenedSystem();
        sector.MeetCivilization(star, cultureId: "territorial", baseline: 0);
        var body = sector.Bodies.First(b => b.IsMineable && sector.StarOf(b.Id)?.Id == star.Id && b.DepositsKg.Count > 0);
        sector.MarkSurveyed(body.Id);
        var material = body.DepositsKg.Keys.First();
        Assert.True(sector.StartJob(ship.Id, new MiningJob(body.Id, material, ship.DockedToCraftId, 0.25)).Done);
        sector.StopJob(ship.Id);

        sector.SetLaw(Laws.RespectClaims, true);
        var refusal = sector.StartJob(ship.Id, new MiningJob(body.Id, material, ship.DockedToCraftId, 0.25));
        Assert.False(refusal.Done);
        Assert.Contains("Respect claimed systems", refusal.Message);
        Assert.Equal(0, sector.MineableSurveyed(body, material));
    }

    [Fact]
    public void Mining_in_a_claimed_system_is_a_grievance()
    {
        var (sector, _, _, star, ship) = OpenedSystem();
        var faction = sector.MeetCivilization(star, cultureId: "purist", baseline: 0);
        var body = sector.Bodies.First(b => b.IsMineable && sector.StarOf(b.Id)?.Id == star.Id && b.DepositsKg.Count > 0);
        var site = body.Sites.First(s => s.DepositsKg.Count > 0);
        sector.TryUndock(ship.Id);
        ship.RailOrbit = null;
        var offset = new Vector2D(Math.Cos(site.CentreRad), Math.Sin(site.CentreRad)) * (body.RadiusM + ship.CollisionRadiusM + 1);
        ship.Position = ship.PreviousPosition = sector.GetPosition(body.Id) + offset;
        ship.HeadingRad = ship.PreviousHeadingRad = FlightModel.WrapAngle(site.CentreRad + Math.PI / 2);
        ship.Velocity = sector.GetVelocityAt(body.Id, sector.TimeS);
        sector.RestoreLanding(ship.Id, body.Id, offset);
        Assert.True(sector.TryAnchor(ship.Id).Done);
        Run(sector, () => ship.CargoKg >= 20_000 || ship.FreeCargoKg == 0, 10 * TicksPerMinute);
        Assert.True(faction.Grievance > 1);
        Assert.True(faction.TargetAttitude(sector.ActiveLaws) < 0);
    }

    [Fact]
    public void No_planet_dismantling_stops_bulk_mining()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = sector.Bodies.Single(b => b.Name == "Rock-1");
        var bare = rock.Sites.First(s => s.DepositsKg.Count == 0);
        Assert.True(sector.MineableAt(rock, rock.BulkMaterialId, bare.CentreRad) > 0);
        sector.SetLaw(Laws.NoDismantling, true);
        Assert.Equal(0, sector.MineableAt(rock, rock.BulkMaterialId, bare.CentreRad));
        var ore = rock.Sites.First(s => s.DepositsKg.Count > 0);
        Assert.True(sector.MineableAt(rock, ore.DepositsKg.Keys.First(), ore.CentreRad) > 0); // ore deposits are still fine
    }

    [Fact]
    public void Organic_crews_only_takes_robots_off_the_crew()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var cells = new List<DesignCell> { new(new GridPos(0, 0), "robot_bay"), new(new GridPos(1, 0), "solar_panel_mk2") };
        for (var i = 0; i < 16; i++) cells.Add(new DesignCell(new GridPos(i, 1), "storage_silo"));
        var design = new ShipDesign("robot_depot", "Robot depot", cells);
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = new CraftState(sector.AllocateCraftId(), "Depot", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(0, -3000), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(depot);
        depot.StoredEnergyKJ = depot.Stats.EnergyCapacityKJ;
        sector.Step();
        Assert.Null(depot.CrewShortReason);
        sector.SetLaw(Laws.OrganicCrews, true);
        sector.Step();
        Assert.Contains("Organic crews only", depot.CrewShortReason);
    }

    [Fact]
    public void A_hostile_civilization_destroys_its_end_of_the_gate_with_the_mass_ledger_exact()
    {
        var (sector, near, far, star, ship) = OpenedSystem();
        var faction = sector.MeetCivilization(star, cultureId: "pacifist", baseline: -80);
        Assert.Equal(Disposition.Hostile, faction.Disposition);
        var mass = sector.ComputeMassLedger().TotalKg;

        Run(sector, () => far.IsWreck, 90 * TicksPerMinute);
        Assert.True(far.IsWreck);
        Assert.Contains(far, sector.Crafts); // what is left of it drifts there (#84)
        Assert.Equal(star.Id, near.GateLostStarId);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        Assert.Contains(sector.Events, e => e.Text.Contains($"{faction.Name} destroyed"));
        Assert.Contains(ship, sector.Crafts); // the ship that was docked there is left floating, not destroyed
        Assert.False(ship.IsDocked);
        // They never come through the gate: nothing of theirs appears at home.
        var home = sector.StarAt(near.Position)!.Id;
        Assert.All(sector.Crafts.Where(c => sector.StarAt(c.Position)!.Id == home), c => Assert.True(SectorState.IsPlayers(c)));
    }

    [Fact]
    public void The_near_gate_sends_a_new_far_gate_to_the_same_system()
    {
        var (sector, near, far, star, _) = OpenedSystem();
        sector.MeetCivilization(star, cultureId: "pacifist", baseline: -80);
        Run(sector, () => far.IsWreck, 90 * TicksPerMinute);
        var stars = sector.Stars.Count;

        // Without its bill in stock it waits and says so.
        var traveller = DockedShip(sector, near, 1);
        sector.RequestJump(traveller.Id);
        for (var i = 0; i < 40; i++) sector.Step();
        Assert.Contains("destroyed", traveller.GateStatus);

        foreach (var (material, kg) in near.Design.ComputeBillOfMaterials(Catalog).ToWholeKg()) sector.DebugAddStock(near.Id, material, kg);
        near.StoredEnergyKJ = near.Stats.EnergyCapacityKJ;
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, () => sector.StarAt(traveller.Position)?.Id == star.Id, 5 * TicksPerMinute);
        Assert.Equal(star.Id, sector.StarAt(traveller.Position)!.Id);
        Assert.Equal(stars, sector.Stars.Count); // the same system, not a new one
        Assert.True(near.GateLostStarId.IsNone);
        Assert.Equal(near.Id, sector.GetCraft(near.GateLinkId).GateLinkId);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Gate_defenses_only_work_under_armed_gates_and_can_drive_a_raid_off()
    {
        var (sector, _, far, star, _) = OpenedSystem(farDefenses: 8);
        Assert.Equal(0, sector.GateDefenses(far));
        sector.SetLaw(Laws.ArmedGates, true);
        Assert.Equal(8, sector.GateDefenses(far));

        var faction = sector.MeetCivilization(star, cultureId: "militarist", baseline: -90); // likes armed gates, but starts far below
        Run(sector, () => sector.Events.Any(e => e.Text.Contains("drove them off")) || far.IsWreck, 4 * 60 * TicksPerMinute);
        Assert.Contains(sector.Events, e => e.Text.Contains("drove them off"));
        Assert.True(faction.Attitude > -90); // drifting up towards what it thinks of armed gates
    }

    [Fact]
    public void Civilizations_laws_events_and_a_lost_gate_survive_a_save_exactly()
    {
        var (sector, near, far, star, ship) = OpenedSystem();
        sector.MeetCivilization(star, cultureId: "pacifist", baseline: -80);
        sector.SetLaw(Laws.NoDismantling, true);
        Run(sector, () => far.IsWreck, 90 * TicksPerMinute);

        var player = sector.PilotCraftId;
        var json = SaveGameSerializer.ToJson(sector, player);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, player));
        Assert.Equal(star.Id, loaded.GetCraft(near.Id).GateLostStarId);
        Assert.True(loaded.LawActive(Laws.NoDismantling));
        Assert.Equal(sector.Events.Count, loaded.Events.Count);

        for (var i = 0; i < 10 * TicksPerMinute; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(loaded, player));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Raiders_destroy_an_undefended_gate_whether_or_not_you_watch(bool watched)
    {
        var (sector, _, far, star, ship) = OpenedSystem();
        var faction = sector.MeetCivilization(star, cultureId: "pacifist", baseline: -80);
        sector.PilotCraftId = watched ? ship.Id : CraftId.None;
        sector.StartRaid(faction, far, 3);
        var mass = sector.ComputeMassLedger().TotalKg;
        var log = new List<string>();
        for (var i = 0; i < 10 * TicksPerMinute && !far.IsWreck; i++)
        {
            sector.Step();
            if (i % 1200 == 0) log.Add(string.Join("; ", sector.Crafts.Where(c => !SectorState.IsPlayers(c)).Select(c => $"{c.Position.DistanceTo(far.Position):0}m v{(c.Velocity - far.Velocity).Length:0} {(c.IsWreck ? "wreck" : "")} {c.Inventory["shells"]}kg {c.StoredEnergyKJ:0}kJ shots{sector.Shots.Count}")) + $" gate {far.PartHealth.Count}/{far.PartsLost}");
        }
        Assert.True(far.IsWreck, string.Join(" | ", log));
        for (var i = 0; i < 100; i++) sector.Step();
        Assert.DoesNotContain(sector.Crafts, c => !SectorState.IsPlayers(c) && !c.IsPatrol && !c.IsTradeStation && !c.IsWreck); // they leave; their patrol stays (#89)
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }
}
