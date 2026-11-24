using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#80 propellant drives: chemical rockets and ion drives burn propellant while they push; dry drives stop.</summary>
public sealed class DriveTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;

    /// <summary>A small ship: cockpit, battery, solar, a storage silo, and the given drive facing back (pushes forward).</summary>
    private static (SectorState Sector, CraftState Ship) ShipWith(string drive)
    {
        var sector = TestData.NewGame().Sector;
        var design = new ShipDesign("d", "D", new[]
        {
            new DesignCell(new GridPos(0, -1), "cockpit"), new DesignCell(new GridPos(0, 0), "storage_silo"),
            new DesignCell(new GridPos(1, 0), "battery_mk2"), new DesignCell(new GridPos(-1, 0), "solar_panel_mk3"),
            new DesignCell(new GridPos(0, 1), drive),
        });
        var ship = new CraftState(sector.AllocateCraftId(), "D", design, design.ComputeStats(Catalog), TestData.SunnySideOfHome(sector, 200_000), 0);
        sector.AddCraft(ship);
        return (sector, ship);
    }

    [Fact]
    public void Drive_parts_carry_their_propellant_and_add_thrust()
    {
        var (_, ship) = ShipWith("rocket");
        var drive = Assert.Single(ship.Stats.DriveList);
        Assert.Equal(0, drive.Direction);
        Assert.Equal(Catalog.Get("rocket").ThrustN, ship.Stats.TotalThrustN);
        Assert.Equal(new[] { "hydrogen", "oxygen" }, drive.KgPerS.Keys.OrderBy(k => k));
    }

    [Fact]
    public void A_rocket_burns_hydrogen_and_oxygen_only_while_it_pushes_with_mass_exact()
    {
        var (sector, ship) = ShipWith("rocket");
        sector.DebugAddStock(ship.Id, "hydrogen", 100);
        sector.DebugAddStock(ship.Id, "oxygen", 800);
        var mass = sector.ComputeMassLedger().TotalKg;

        ship.Controls = new ShipControls(0, 0, false);
        for (var i = 0; i < 5 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(100, ship.Inventory["hydrogen"]); // coasting: nothing burned

        ship.Controls = new ShipControls(1, 0, false);
        for (var i = 0; i < 20 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(98, ship.Inventory["hydrogen"]); // 0.1 kg/s for 20 s
        Assert.Equal(784, ship.Inventory["oxygen"]); // 0.8 kg/s
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg); // burned propellant is exhaust (ejected)
    }

    [Fact]
    public void A_dry_rocket_gives_no_thrust()
    {
        var (sector, ship) = ShipWith("rocket");
        ship.Controls = new ShipControls(1, 0, false);
        var speed = ship.Velocity.Length;
        sector.Step();
        Assert.Contains("rocket", ship.DryDrives);
        Assert.True(Math.Abs(ship.Velocity.Length - speed) < 0.05); // only gravity: no 600 kN push
        Assert.Contains("DRY", SectorState.PropellantStatus(ship));

        sector.DebugAddStock(ship.Id, "hydrogen", 10);
        sector.DebugAddStock(ship.Id, "oxygen", 80);
        var before = ship.Velocity.Length;
        sector.Step();
        Assert.Empty(ship.DryDrives);
        Assert.True(ship.Velocity.Length - before > 1); // full thrust again
    }

    [Fact]
    public void An_ion_drive_sips_propellant_and_needs_electric_propulsion()
    {
        var (sector, ship) = ShipWith("ion_drive");
        Assert.False(sector.IsPartUnlocked("ion_drive"));
        Assert.True(sector.IsPartUnlocked("rocket"));
        sector.DebugAddStock(ship.Id, "hydrogen", 5);
        ship.Controls = new ShipControls(1, 0, false);
        for (var i = 0; i < 600 * TicksPerSecond; i++) sector.Step();
        Assert.Equal(4, ship.Inventory["hydrogen"]); // 0.002 kg/s for 10 minutes: 1.2 kg
        Assert.True(ship.Stats.TotalThrustN < 30_000); // gentle
    }

    [Fact]
    public void Propellant_remainders_survive_a_save_exactly()
    {
        var (sector, ship) = ShipWith("rocket");
        sector.DebugAddStock(ship.Id, "hydrogen", 100);
        sector.DebugAddStock(ship.Id, "oxygen", 800);
        ship.Controls = new ShipControls(1, 0, false);
        for (var i = 0; i < 7; i++) sector.Step();
        var player = sector.PilotCraftId;
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, player), Catalog).Sector;
        loaded.GetCraft(ship.Id).Controls = ship.Controls;
        for (var i = 0; i < 300; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(loaded, player));
    }
}
