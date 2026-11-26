using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#148 seek repair; #144 cleaning up defunct craft in bulk.</summary>
public class SeekRepairTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private static readonly GridPos LeftThruster = new(-1, 1);

    /// <summary>A second starter ship, not the one you fly, a little way off the yard and matched to it.</summary>
    private static (SectorState Sector, CraftState Ship, CraftState Yard) Nearby()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.Inventory.Add(NewGame.StartingStockKg);
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), "Hauler", design, design.ComputeStats(Catalog),
            yard.Position + new Vector2D(600, -400), 0) { Velocity = yard.Velocity };
        sector.AddCraft(ship);
        return (sector, ship, yard);
    }

    private static bool RunUntil(SectorState sector, Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (done()) return true;
            sector.Step();
        }
        return done();
    }

    [Fact]
    public void A_ship_with_no_job_and_light_damage_flies_in_gets_mended_and_lets_go()
    {
        var (sector, ship, yard) = Nearby();
        sector.DebugDamagePart(ship.Id, LeftThruster, 0.9); // light: servicing alone would never send it
        Assert.False(SectorState.NeedsService(ship));
        var massBefore = sector.ComputeMassLedger().TotalKg;

        var ordered = sector.OrderSeekRepair(ship.Id);
        Assert.True(ordered.Done, ordered.Message);
        Assert.False(sector.OrderSeekRepair(ship.Id).Done); // on its way already
        sector.Step();
        Assert.Contains(yard.Name, ship.SeekRepairStatus);

        Assert.True(RunUntil(sector, () => !ship.SeekRepair, 20 * TicksPerMinute), ship.SeekRepairStatus);
        Assert.False(SectorState.NeedsRepair(ship));
        Assert.False(ship.IsDocked); // undocked again
        Assert.True(ship.ServiceAt.IsNone);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
        Assert.Contains(sector.Events, e => e.Text.Contains("Hauler is repaired"));
    }

    [Fact]
    public void Seek_repair_is_refused_with_a_reason_and_can_be_called_off_and_saved()
    {
        var (sector, ship, yard) = Nearby();
        Assert.Contains("nothing to repair", sector.OrderSeekRepair(ship.Id).Message);
        Assert.NotNull(sector.SeekRepairBlocker(yard));
        sector.DebugDamagePart(ship.Id, LeftThruster, 0.5);
        Assert.True(sector.OrderSeekRepair(ship.Id).Done);

        var json = SaveGameSerializer.ToJson(sector, ship.Id == sector.PilotCraftId ? ship.Id : sector.PilotCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.True(loaded.GetCraft(ship.Id).SeekRepair);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, loaded.PilotCraftId));

        Assert.True(sector.CancelSeekRepair(ship.Id).Done);
        Assert.False(ship.SeekRepair);
        Assert.DoesNotContain("seekRepair", SaveGameSerializer.ToJson(sector, sector.PilotCraftId));
    }

    [Fact]
    public void A_miner_sent_for_repairs_goes_back_to_its_job()
    {
        var (sector, ship, yard) = Nearby();
        var moon = sector.Bodies.Single(b => b.Name == "Moon");
        moon.AddDeposit("iron_ore", 500_000_000);
        sector.MarkSurveyed(moon.Id);
        yard.LegacyUnlimitedStorage = true;
        Assert.True(sector.StartJob(ship.Id, new MiningJob(moon.Id, "iron_ore", yard.Id, 0.25)).Done);
        sector.DebugDamagePart(ship.Id, LeftThruster, 0.8);
        Assert.True(sector.OrderSeekRepair(ship.Id).Done);
        Assert.True(RunUntil(sector, () => !ship.SeekRepair, 20 * TicksPerMinute), ship.SeekRepairStatus);
        Assert.False(SectorState.NeedsRepair(ship));
        Assert.True(ship.Job!.Active);
        var delivered = ship.Job.DeliveredKg;
        Assert.True(RunUntil(sector, () => ship.Job.DeliveredKg > delivered, 30 * TicksPerMinute), ship.Job.Status);
    }

    [Fact]
    public void Defunct_craft_are_found_and_marked_as_wrecks_together_and_salvagers_take_yours_first()
    {
        var (sector, ship, yard) = Nearby();
        var design = TestData.StarterShip();
        var fine = new CraftState(sector.AllocateCraftId(), "Fine", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(-900, 500), 0) { Velocity = yard.Velocity };
        sector.AddCraft(fine);
        sector.DebugDamagePart(fine.Id, LeftThruster, 0.3); // damaged, still flies: not defunct
        foreach (var cell in ship.Design.Cells.Where(c => Catalog.Get(c.PartId).ThrustN > 0).ToList()) sector.DebugDamagePart(ship.Id, cell.Pos, 0); // every engine gone
        Assert.Equal(0, ship.Stats.TotalThrustN);

        var defunct = sector.DefunctCrafts();
        Assert.Equal(new[] { ship.Id }, defunct.Select(c => c.Id));
        var result = sector.ScrapDefunct();
        Assert.True(result.Done, result.Message);
        Assert.True(ship.IsWreck && ship.Scrapped);
        Assert.False(fine.IsWreck);
        Assert.Empty(sector.DefunctCrafts());
        Assert.False(sector.ScrapDefunct().Done);
    }

    [Fact]
    public void The_recap_puts_craft_in_trouble_first_with_links()
    {
        var (sector, ship, _) = Nearby();
        Assert.DoesNotContain(sector.Recap(), l => l.Urgent);
        sector.DebugDamagePart(ship.Id, LeftThruster, 0.2);
        var recap = sector.Recap();
        var first = recap[0];
        Assert.True(first.Urgent);
        Assert.Contains("Hauler is badly damaged", first.Text);
        Assert.Equal(ship.Id, first.Target.Craft);
        Assert.True(sector.OrderSeekRepair(ship.Id).Done);
        Assert.DoesNotContain(sector.Recap(), l => l.Text.Contains("Hauler is badly damaged")); // on its way
    }
}
