using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Antimatter (#81), the quantum drive (#82) and nanotechnology (#83).</summary>
public sealed class FutureTechTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    private static ShipDesign With(params string[] extra)
    {
        var cells = TestData.StarterShip().Cells.Where(c => c.PartId is not ("drill" or "anchor_clamp")).ToList();
        for (var i = 0; i < extra.Length; i++) cells.Add(new DesignCell(new GridPos(2 + i, 0), extra[i]));
        return new ShipDesign("f", "F", cells);
    }

    private static (SectorState Sector, CraftState Ship) Game(params string[] extra)
    {
        var sector = TestData.NewGame().Sector;
        var ship = Add(sector, With(extra), Space(sector), "Ship");
        sector.PilotCraftId = CraftId.None;
        return (sector, ship);
    }

    private static Vector2D Space(SectorState sector) => sector.GetPosition(sector.Stars[0].Id) + new Vector2D(0, -3_000_000);

    private static CraftState Add(SectorState sector, ShipDesign design, Vector2D at, string name)
    {
        var craft = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), at, 0);
        sector.AddCraft(craft);
        return craft;
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    private static double Hurt(CraftState c) => c.PartsLost + c.PartHealth.Values.Sum(h => 1 - h);

    // --- Antimatter ---

    [Fact]
    public void Powered_containment_holds_and_antimatter_only_goes_where_it_can_be_contained()
    {
        var (sector, ship) = Game("containment");
        sector.DebugAddStock(ship.Id, "antimatter", 20);
        Run(sector, 5 * Minute);
        Assert.Equal(20, ship.Inventory["antimatter"]);
        Assert.Equal(0, ship.ContainmentFailS);
        Assert.Equal(0, SectorState.AntimatterRoomKg(ship));

        var yard = sector.Crafts.First(c => c.IsShipyard);
        ship.Position = sector.BerthPosition(yard, sector.FreePorts(yard)[0], ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        sector.SetTargetCraft(ship.Id, yard.Id);
        Assert.True(sector.TryDock(ship.Id).Done);
        Assert.Equal(0, sector.MaxTransferKg(ship.Id, yard.Id, "antimatter"));
        Assert.Contains("contain", sector.TryTransfer(ship.Id, yard.Id, "antimatter", 1).Message);
    }

    [Fact]
    public void Without_power_it_warns_then_explodes_and_hurts_everything_near()
    {
        var (sector, ship) = Game("containment");
        sector.DebugAddStock(ship.Id, "antimatter", 9);
        var near = Add(sector, With(), ship.Position + new Vector2D(200, 0), "Near");
        var far = Add(sector, With(), ship.Position + new Vector2D(3_000, 0), "Far");
        var dark = new ShipDesign("d", "D", ship.Design.Cells.Where(c => c.PartId != "solar_panel"));
        ship.Design = dark;
        ship.Stats = dark.ComputeStats(Catalog);
        ship.StoredEnergyKJ = 0;
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, Second);
        Assert.True(ship.ContainmentFailS > 0);
        Assert.Contains(sector.Events, e => e.Text.Contains("DANGER"));
        Assert.Equal(0, Hurt(near));
        Run(sector, (int)(SectorState.ContainmentGraceS * Second));
        Assert.Equal(0, ship.Inventory["antimatter"]);
        Assert.True(Hurt(ship) > 0 && Hurt(near) > 0);
        Assert.Equal(0, Hurt(far));
        Assert.Single(sector.Blasts);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Power_coming_back_in_time_saves_it()
    {
        var (sector, ship) = Game("containment");
        sector.DebugAddStock(ship.Id, "antimatter", 5);
        var whole = ship.Design;
        ship.Design = new ShipDesign("d", "D", whole.Cells.Where(c => c.PartId != "solar_panel"));
        ship.Stats = ship.Design.ComputeStats(Catalog);
        ship.StoredEnergyKJ = 0;
        Run(sector, 10 * Second);
        Assert.True(ship.ContainmentFailS > 5);
        ship.Design = whole;
        ship.Stats = whole.ComputeStats(Catalog);
        ship.StoredEnergyKJ = ship.Stats.EnergyCapacityKJ;
        Run(sector, Minute);
        Assert.Equal(5, ship.Inventory["antimatter"]);
        Assert.Equal(0, ship.ContainmentFailS);
    }

    [Fact]
    public void Shields_and_the_size_of_the_stock_decide_the_damage()
    {
        double Blast(long kg)
        {
            var (sector, ship) = Game("containment");
            var near = Add(sector, With(), ship.Position + new Vector2D(320, 0), "Near"); // out of a kilogram's reach
            sector.DebugAddStock(ship.Id, "antimatter", kg);
            ship.IsWreck = true; // a wreck has no containment
            Run(sector, (int)(SectorState.ContainmentGraceS * Second) + 5);
            return Hurt(near);
        }
        Assert.Equal(0, Blast(1));
        Assert.True(Blast(16) > 0);
    }

    [Fact]
    public void The_factory_makes_it_only_into_containment_and_the_reactor_burns_it()
    {
        var sector = TestData.NewGame().Sector;
        sector.PilotCraftId = CraftId.None;
        ShipDesign Station(params string[] parts) => new("s", "S", parts.Select((p, i) => new DesignCell(new GridPos(i, 0), p)));
        var plain = Add(sector, Station("antimatter_factory", "battery_mk2", "storage_silo"), Space(sector), "Plain");
        var full = Add(sector, Station("antimatter_factory", "battery_mk2", "storage_silo", "containment"), Space(sector) + new Vector2D(0, -5_000), "Works");
        foreach (var s in new[] { plain, full }) { sector.DebugAddStock(s.Id, "hydrogen", 100); s.ConverterProgressKJ.Clear(); }
        full.ConverterProgressKJ["antimatter_factory"] = Catalog.Get("antimatter_factory").ConverterKJPerBatch - 100;
        Run(sector, 2 * Second);
        Assert.Equal(1, full.Inventory["antimatter"]);
        Assert.Equal(0, plain.Inventory["antimatter"]);
        Assert.Empty(plain.ConverterProgressKJ); // doesn't even start
        var part = Catalog.Get("antimatter_reactor");
        Assert.True(part.GeneratorKJPerBatch < Catalog.Get("antimatter_factory").ConverterKJPerBatch); // no free energy
        Assert.True(part.GeneratorKJPerBatch > Catalog.Get("fusion_reactor").GeneratorKJPerBatch);
    }

    // --- Quantum drive ---

    [Fact]
    public void A_jump_charges_then_arrives_beside_the_target_matched_to_it()
    {
        var (sector, ship) = Game("quantum_drive", "solar_panel_mk2");
        var giant = sector.Bodies.First(b => b.Name == "Giant");
        sector.SetTarget(ship.Id, giant.Id);
        ship.LegacyFreePower = true;
        var mass = sector.ComputeMassLedger().TotalKg;
        Assert.True(sector.RequestQuantumJump(ship.Id).Done);
        Run(sector, Second);
        Assert.True(ship.QuantumChargeKJ > 0);
        Assert.True(sector.GetRelativeMotion(ship.Id, giant.Id).SurfaceDistanceM > 100_000);
        Run(sector, 5 * Minute, () => !ship.QuantumRequested);
        var motion = sector.GetRelativeMotion(ship.Id, giant.Id);
        Assert.InRange(motion.SurfaceDistanceM, 2_000, 4_000);
        Assert.InRange(motion.RelativeSpeedMps, 0, 5);
        Assert.Contains("cooling down", sector.RequestQuantumJump(ship.Id).Message);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Jumps_need_power_a_target_distance_and_stay_in_the_system()
    {
        var (sector, ship) = Game("quantum_drive");
        Assert.Contains("Target", sector.RequestQuantumJump(ship.Id).Message);
        var close = Add(sector, With(), ship.Position + new Vector2D(3_000, 0), "Close");
        sector.SetTargetCraft(ship.Id, close.Id);
        Assert.Contains("Too close", sector.RequestQuantumJump(ship.Id).Message);
        var (star, _) = SectorGenerator.GenerateSystem(sector, 1);
        sector.SetTarget(ship.Id, star.Id);
        Assert.Contains("warp gate", sector.RequestQuantumJump(ship.Id).Message);
        sector.SetTarget(ship.Id, sector.Bodies.First(b => b.Name == "Home").Id);
        ship.StoredEnergyKJ = 0;
        Assert.True(sector.RequestQuantumJump(ship.Id).Done);
        var at = ship.Position;
        Run(sector, Minute);
        Assert.True(ship.Position.DistanceTo(at) < 50_000); // a weak ship far from the star never charges in a minute
        var (other, plain) = Game();
        Assert.Contains("no quantum drive", other.RequestQuantumJump(plain.Id).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Automated_ships_jump_long_legs_by_themselves(bool watched)
    {
        var (sector, ship) = Game("quantum_drive");
        var yard = sector.Crafts.First(c => c.IsShipyard);
        var design = new ShipDesign("depot", "Depot", new[] { new DesignCell(new GridPos(0, 0), "storage_silo"), new DesignCell(new GridPos(0, -1), "docking_port") });
        var depot = Add(sector, design, Space(sector), "Depot");
        var giant = sector.Bodies.First(b => b.Name == "Giant");
        sector.PutOnRails(depot.Id, new CircularOrbit(giant.Id, giant.RadiusM + 6_000, 3_000, 0));
        sector.DebugAddStock(depot.Id, "metal", 10_000);
        ship.Position = yard.Position + new Vector2D(0, -2_000);
        ship.Velocity = yard.Velocity;
        ship.LegacyFreePower = true;
        sector.FullDetailEverywhere = watched;
        Assert.True(sector.StartJob(ship.Id, new HaulJob(depot.Id, "metal", yard.Id, 1_000, 0)).Done);
        Run(sector, 12 * Minute, () => ship.Job!.CompletedCycles >= 1);
        // 500 km and more each way: over half an hour even by cruise.
        Assert.True(ship.Job!.CompletedCycles >= 1, $"{ship.Job.Status} {ship.QuantumChargeKJ:0}/{SectorState.QuantumChargeNeededKJ(ship):0}");
        Assert.Equal(0, Hurt(ship));
    }

    // --- Nanotechnology ---

    [Fact]
    public void Nanites_mend_and_regrow_their_own_craft_from_its_stock_with_exact_mass()
    {
        var (sector, ship) = Game("nanite_bay");
        var lost = ship.Design.Cells.First(c => c.PartId == "hull").Pos;
        var worn = ship.Design.Cells.First(c => c.PartId == "cargo").Pos;
        sector.DebugDamagePart(ship.Id, lost, 0);
        sector.DebugDamagePart(ship.Id, worn, 0.5);
        Run(sector, 8 * Minute);
        Assert.Equal(1, SectorState.MissingParts(ship));
        Assert.Contains("needs", ship.RepairStatus);
        foreach (var m in new[] { "metal", "composites", "slag", "electronics" }) ship.Inventory.Add(m, 3_000);
        var mass = sector.ComputeMassLedger().TotalKg;
        Run(sector, 20 * Minute, () => !SectorState.NeedsRepair(ship));
        Assert.False(SectorState.NeedsRepair(ship), ship.RepairStatus);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void A_nano_assembler_makes_a_small_ship_a_slow_shipyard()
    {
        var (sector, ship) = Game("nano_assembler");
        Assert.Equal(0, ship.Stats.CrewRequired);
        Assert.True(ship.IsShipyard);
        foreach (var (m, kg) in TestData.StarterShip().ComputeBillOfMaterials(Catalog).ToWholeKg()) ship.Inventory.Add(m, kg);
        var mass = sector.ComputeMassLedger().TotalKg;
        var crafts = sector.Crafts.Count;
        Assert.True(sector.OrderBuild(ship.Id, TestData.StarterShip(), Catalog).Accepted);
        Run(sector, 40 * Minute, () => sector.Crafts.Count > crafts);
        Assert.Equal(crafts + 1, sector.Crafts.Count);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void All_of_it_survives_a_save_exactly()
    {
        var (sector, ship) = Game("quantum_drive", "containment", "nanite_bay");
        sector.SetTarget(ship.Id, sector.Bodies.First(b => b.Name == "Giant").Id);
        sector.RequestQuantumJump(ship.Id);
        sector.DebugAddStock(ship.Id, "antimatter", 4);
        Run(sector, Minute);
        ship.StoredEnergyKJ = 0;
        Run(sector, 30);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Run(sector, 2 * Minute);
        Run(loaded, 2 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }
}
