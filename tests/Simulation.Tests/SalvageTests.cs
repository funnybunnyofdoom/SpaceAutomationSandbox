using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Salvage and field repairs (#86).</summary>
public sealed class SalvageTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Minute = 60 * FixedStepClock.TicksPerSecond;

    private static ShipDesign Salvager(params string[] extra)
    {
        var cells = TestData.StarterShip().Cells.Select(c => c.PartId == "drill" ? c with { PartId = "salvage_arm" } : c).ToList();
        for (var i = 0; i < extra.Length; i++) cells.Add(new DesignCell(new GridPos(2 + i, 0), extra[i]));
        return new ShipDesign("salvager", "Salvager", cells);
    }

    /// <summary>A raider wreck with shells and a salvager, beside each other in empty space (or the salvager near the yard).</summary>
    private static (SectorState Sector, CraftState Ship, CraftState Wreck, CraftState Yard) Game(bool atYard = false, Vector2D? wreckOffset = null)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.LegacyUnlimitedStorage = true;
        var outward = yard.Position - sector.GetPosition(SectorGenerator.HomePlanet(sector).Id);
        var space = atYard ? yard.Position + outward * (3_000 / outward.Length) : sector.GetPosition(sector.Stars[0].Id) + new Vector2D(0, -3_000_000);
        var faction = sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: 0);
        var wreck = new CraftState(sector.AllocateCraftId(), "Raider", SectorState.RaiderDesign, SectorState.RaiderDesign.ComputeStats(Catalog), space, 0)
        {
            FactionId = faction.Id, IsWreck = true, Velocity = atYard ? yard.Velocity : Vector2D.Zero,
        };
        sector.AddCraft(wreck);
        wreck.Inventory.Add("shells", 150);
        var design = Salvager();
        Assert.Empty(design.Validate(Catalog));
        var ship = new CraftState(sector.AllocateCraftId(), "Salvager", design, design.ComputeStats(Catalog), space + (wreckOffset ?? new Vector2D(90, 0)), 0) { Velocity = wreck.Velocity };
        sector.AddCraft(ship);
        sector.PilotCraftId = CraftId.None;
        return (sector, ship, wreck, yard);
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    [Fact]
    public void By_hand_the_targeted_wreck_is_cut_up_completely_with_exact_mass()
    {
        var (sector, ship, wreck, _) = Game();
        var mass = sector.ComputeMassLedger().TotalKg;
        var worth = wreck.Stats.TotalMassKg + 150;
        sector.SetTargetCraft(ship.Id, wreck.Id);
        for (var i = 0; i < 10 * Minute && sector.Crafts.Contains(wreck); i++)
        {
            sector.Step();
            if (i % 50 == 0) Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        }
        Assert.DoesNotContain(wreck, sector.Crafts);
        Assert.Equal(worth, ship.CargoKg);
        Assert.Equal(150, ship.Inventory["shells"]);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Too_far_untargeted_unpowered_or_full_does_nothing()
    {
        var (sector, ship, wreck, _) = Game();
        Run(sector, 200);
        Assert.Equal(0, ship.CargoKg); // no target

        (sector, ship, wreck, _) = Game(wreckOffset: new Vector2D(1_000, 0));
        sector.SetTargetCraft(ship.Id, wreck.Id);
        Run(sector, 200);
        Assert.Equal(0, ship.CargoKg);
        Assert.Contains("come within", ship.SalvageStatus);

        (sector, ship, wreck, _) = Game();
        sector.SetTargetCraft(ship.Id, wreck.Id);
        ship.Inventory.Add("metal", ship.FreeCargoKg);
        Run(sector, 200);
        Assert.Contains("full", ship.SalvageStatus);
        Assert.Equal(150, wreck.Inventory["shells"]);
    }

    [Fact]
    public void Kits_orders_and_batches_in_a_wreck_are_recovered()
    {
        var (sector, ship, wreck, _) = Game();
        var kitDesign = TestData.SurfaceDepot();
        var bill = kitDesign.ComputeBillOfMaterials(Catalog).ToWholeKg();
        sector.RestoreKit(wreck.Id, new ConstructionKit(new OrderId(900), "Kit", kitDesign, kitDesign.ComputeStats(Catalog), bill));
        wreck.RefiningBatch = new RefiningBatch("smelt_iron", new Dictionary<string, long> { ["iron_ore"] = 1000 });
        var mass = sector.ComputeMassLedger().TotalKg;
        sector.SetTargetCraft(ship.Id, wreck.Id);
        Run(sector, 3 * Minute, () => ship.Inventory["iron_ore"] == 1000);
        Assert.Equal(1000, ship.Inventory["iron_ore"]);
        Assert.Empty(wreck.Kits);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void The_job_salvages_delivers_and_then_waits_for_more()
    {
        var (sector, ship, wreck, yard) = Game(atYard: true, wreckOffset: new Vector2D(600, 0));
        var mass = sector.ComputeMassLedger().TotalKg;
        var started = sector.StartJob(ship.Id, new SalvageJob(yard.Id));
        Assert.True(started.Done, started.Message);
        Run(sector, 30 * Minute, () => ship.Job!.CompletedCycles > 0 && ship.Job.WaitReason is not null);
        Assert.True(ship.Job!.CompletedCycles > 0, $"{ship.Job.Status} {ship.SalvageStatus}");
        Assert.DoesNotContain(wreck, sector.Crafts);
        Assert.Equal(SectorState.RaiderDesign.ComputeStats(Catalog).TotalMassKg + 150, ship.Job.DeliveredKg);
        Assert.True(yard.Inventory["shells"] >= 150);
        Assert.Contains("no wrecks", ship.Job.WaitReason);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void The_job_is_refused_with_reasons()
    {
        var (sector, _, _, yard) = Game();
        var plain = sector.Crafts.First(c => c.Name == "Player" || c.Design.Id == "starter_ship");
        Assert.Contains("no salvage arm", sector.StartJob(plain.Id, new SalvageJob(yard.Id)).Message);
    }

    [Fact]
    public void Salvage_survives_a_save_exactly()
    {
        var (sector, ship, _, yard) = Game(atYard: true, wreckOffset: new Vector2D(600, 0));
        sector.StartJob(ship.Id, new SalvageJob(yard.Id));
        Run(sector, 2 * Minute + 7, () => ship.SalvageProgressKg > 300);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Run(sector, 5 * Minute);
        Run(loaded, 5 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }

    [Theory]
    [InlineData("repair_drones")]
    [InlineData("robot_bay")]
    public void Drones_and_robots_mend_damage_in_the_field_from_their_own_stock(string part)
    {
        var (sector, _, _, _) = Game();
        var design = Salvager(part);
        var ship = new CraftState(sector.AllocateCraftId(), "Mender", design, design.ComputeStats(Catalog), sector.GetPosition(sector.Stars[0].Id) + new Vector2D(0, -3_100_000), 0);
        sector.AddCraft(ship);
        var pos = design.Cells.First(c => c.PartId == "hull").Pos;
        sector.DebugDamagePart(ship.Id, pos, 0.5);
        Run(sector, 3 * Minute);
        Assert.True(ship.PartHealth.ContainsKey(pos));
        if (part == "repair_drones") Assert.Contains("needs", ship.RepairStatus);
        ship.Inventory.Add("metal", 2_000);
        ship.Inventory.Add("composites", 500);
        ship.Inventory.Add("slag", 500);
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, 30 * Minute, () => ship.PartHealth.Count == 0);
        Assert.Empty(ship.PartHealth);
        Assert.True(ship.Inventory["metal"] < 2_000);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Field_repairs_dont_rebuild_missing_parts()
    {
        var (sector, _, _, _) = Game();
        var design = Salvager("repair_drones");
        var ship = new CraftState(sector.AllocateCraftId(), "Mender", design, design.ComputeStats(Catalog), sector.GetPosition(sector.Stars[0].Id) + new Vector2D(0, -3_100_000), 0);
        sector.AddCraft(ship);
        ship.Inventory.Add("metal", 5_000);
        sector.DebugDamagePart(ship.Id, design.Cells.First(c => c.PartId == "anchor_clamp").Pos, 0);
        Run(sector, 5 * Minute);
        Assert.Equal(1, SectorState.MissingParts(ship));
        Assert.Equal(5_000, ship.Inventory["metal"]);
    }
}
