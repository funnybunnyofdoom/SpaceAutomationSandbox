using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Milestones (#38) and raids through warp gates (#95).</summary>
public sealed class MilestoneAndInvasionTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    // --- Milestones ---

    [Fact]
    public void A_milestone_is_reached_once_kept_with_its_time_and_announced()
    {
        var sector = TestData.NewGame().Sector;
        Run(sector, 3 * Second);
        Assert.DoesNotContain("life", sector.MilestonesReached.Keys);
        var world = sector.Bodies.First(b => b.Name == "Cinder");
        sector.DebugAddLife(world.Id, FloraHazard.None);
        foreach (var s in world.SiteList) s.Flora = 1;
        Run(sector, 15 * Second);
        Assert.True(sector.MilestonesReached["life"] > 0);
        Assert.Contains("thriving", sector.MilestonesReached.Keys);
        Assert.Single(sector.Events, e => e.Text == "Milestone: Life!.");
        var at = sector.MilestonesReached["life"];
        foreach (var s in world.SiteList) s.Flora = 0;
        Run(sector, Minute);
        Assert.Equal(at, sector.MilestonesReached["life"]); // kept
    }

    [Fact]
    public void Ids_are_unique_and_what_an_older_game_has_isnt_announced()
    {
        Assert.Equal(Milestones.All.Count, Milestones.All.Select(m => m.Id).Distinct().Count());
        var sector = TestData.NewGame().Sector;
        sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: 60);
        Run(sector, 2 * Second);
        Assert.Equal(0, sector.MilestonesReached["contact"]);
        Assert.DoesNotContain(sector.Events, e => e.Text.StartsWith("Milestone"));
    }

    // --- Raids through gates ---

    /// <summary>A linked pair of gates, the far one in a very hostile civilization's system, after the first hour.</summary>
    private static (SectorState Sector, CraftState Near, CraftState Far, Faction Faction) Gates(bool locked = false, double attitude = -90, bool late = true)
    {
        var sector = TestData.NewGame().Sector;
        var (star, planet) = SectorGenerator.GenerateSystem(sector, 1);
        var cells = new List<DesignCell>
        {
            new(new GridPos(0, 0), "warp_gate"), new(new GridPos(0, -1), "docking_port"), new(new GridPos(1, 0), "storage_silo"),
            new(new GridPos(-1, 0), "solar_panel_mk2"), new(new GridPos(-1, -1), "battery_mk2"),
        };
        if (locked) cells.Add(new DesignCell(new GridPos(0, 1), "gate_lock"));
        var design = new ShipDesign("gate", "Gate", cells);
        CraftState Place(string name, CelestialBodyState at)
        {
            var gate = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), sector.GetPosition(at.Id), 0);
            sector.AddCraft(gate);
            sector.PutOnRails(gate.Id, new CircularOrbit(at.Id, at.RadiusM + 5_000, 2_000, 0));
            return gate;
        }
        var near = Place("Near", sector.Bodies.First(b => b.Name == "Cinder"));
        var far = Place("Far", planet);
        near.GateLinkId = far.Id;
        far.GateLinkId = near.Id;
        if (late)
        {
            // Past the first hour without simulating it: load it as a later tick.
            var json = SaveGameSerializer.ToJson(sector, CraftId.None).Replace("\"tick\": 0,", $"\"tick\": {(long)(SectorState.InvadeAfterS * Second) + 20},");
            sector = SaveGameSerializer.FromJson(json, Catalog).Sector;
        }
        var faction = sector.MeetCivilization(sector.Stars.Last(), cultureId: "pacifist", baseline: attitude);
        sector.PilotCraftId = CraftId.None;
        Run(sector, 2);
        return (sector, sector.GetCraft(near.Id), sector.GetCraft(far.Id), faction);
    }

    private static List<CraftState> Raiders(SectorState sector) =>
        sector.Crafts.Where(c => !SectorState.IsPlayers(c) && !c.IsPatrol && !c.IsTradeStation && !c.IsWreck).ToList();

    [Fact]
    public void Very_hostile_raiders_spare_the_gate_jump_through_attack_and_leave()
    {
        var (sector, near, far, faction) = Gates();
        sector.StartRaid(faction, far, 2);
        faction.Invading = true;
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, 5 * Minute, () => faction.RaidGateId == near.Id);
        Assert.Equal(near.Id, faction.RaidGateId);
        Assert.Equal(0, far.PartsLost);
        Assert.Contains(sector.Events, e => e.Text.StartsWith("DANGER") && e.Text.Contains("come through"));
        Assert.All(Raiders(sector), r => Assert.Equal(sector.Stars[0].Id, sector.StarAt(r.Position)!.Id));
        Run(sector, 2 * Minute, () => near.PartsLost > 0);
        Assert.True(near.PartsLost > 0); // they attack the other end
        Run(sector, 12 * Minute);
        Assert.Empty(Raiders(sector)); // gone again, whatever happened
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Theory]
    [InlineData(-50, true)]  // hostile, not enough
    [InlineData(-90, false)] // the first hour
    public void Others_and_early_raids_never_come_through(double attitude, bool late)
    {
        var (sector, near, far, faction) = Gates(attitude: attitude, late: late);
        for (var i = 0; i < 10; i++)
        {
            sector.StartRaid(faction, far, 2);
            Assert.False(faction.Invading);
        }
        Run(sector, 4 * Minute);
        Assert.NotEqual(near.Id, faction.RaidGateId);
        Assert.Equal(0, near.PartsLost);
    }

    [Fact]
    public void A_closed_gate_lets_nothing_through_either_way()
    {
        var (sector, near, far, faction) = Gates(locked: true);
        Assert.True(sector.SetGateClosed(near.Id, true).Done);
        sector.StartRaid(faction, far, 2);
        faction.Invading = true;
        Run(sector, 2 * Second);
        Assert.False(faction.Invading); // an ordinary raid after all
        Run(sector, 4 * Minute);
        Assert.Equal(0, near.PartsLost);
        Assert.True(far.PartsLost > 0);

        // Nor the player's own ships.
        var (other, home, _, _) = Gates(locked: true, attitude: 0);
        var design = TestData.StarterShip();
        var ship = new CraftState(other.AllocateCraftId(), "Ship", design, design.ComputeStats(Catalog), home.Position, 0);
        other.AddCraft(ship);
        ship.Position = other.BerthPosition(home, 0, ship.CollisionRadiusM, home.Position);
        ship.Velocity = home.Velocity;
        other.SetTargetCraft(ship.Id, home.Id);
        Assert.True(other.TryDock(ship.Id).Done);
        home.StoredEnergyKJ = home.Stats.EnergyCapacityKJ;
        other.SetGateClosed(home.Id, true);
        Assert.True(other.RequestJump(ship.Id).Done);
        Run(other, 10 * Second);
        Assert.Contains("closed", ship.GateStatus);
        Assert.Equal(home.Id, ship.DockedToCraftId);
        other.SetGateClosed(home.Id, false);
        Run(other, 5 * Minute, () => ship.Jumps > 0);
        Assert.Equal(1, ship.Jumps);
    }

    [Fact]
    public void Closing_needs_a_gate_lock()
    {
        var (sector, near, _, _) = Gates();
        Assert.Contains("gate lock", sector.SetGateClosed(near.Id, true).Message);
        Assert.False(near.GateClosed);
    }

    [Fact]
    public void Milestones_invasions_and_closed_gates_survive_a_save_exactly()
    {
        var (sector, near, far, faction) = Gates(locked: true);
        sector.StartRaid(faction, far, 2);
        faction.Invading = true;
        Run(sector, 30 * Second);
        sector.SetGateClosed(near.Id, true);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Assert.True(loaded.GetCraft(near.Id).GateClosed);
        Run(sector, 2 * Minute);
        Run(loaded, 2 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }
}
