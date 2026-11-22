using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class RotationTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static ShipDesign WithThrusterRotations(int left, int right) => new("r", "r",
        TestData.StarterShip().Cells.Select(c => c.Pos == new GridPos(-1, 1) ? c with { Rotation = left }
                                               : c.Pos == new GridPos(1, 1) ? c with { Rotation = right } : c));

    [Fact]
    public void Facing_IsForwardAtZero_AndTurnsClockwise()
    {
        Assert.Equal((0, -1), DesignCell.Facing(0));
        Assert.Equal((1, 0), DesignCell.Facing(1));
        Assert.Equal((0, 1), DesignCell.Facing(2));
        Assert.Equal((-1, 0), DesignCell.Facing(3));
    }

    [Fact]
    public void Thrusters_PushTheWayTheyFace()
    {
        var thrust = Catalog.Get("thruster").ThrustN;
        var power = Catalog.Get("thruster").PowerUseKW;

        var oneTurned = WithThrusterRotations(0, 1).ComputeStats(Catalog);
        Assert.Equal(thrust, oneTurned.TotalThrustN);
        Assert.Equal(thrust, oneTurned.RightThrustN);
        Assert.Equal(thrust, oneTurned.NonForwardThrustN);
        Assert.Equal(power, oneTurned.RightPowerKW);
        Assert.Empty(WithThrusterRotations(0, 1).Warnings(Catalog));

        var bothTurned = WithThrusterRotations(2, 3);
        var stats = bothTurned.ComputeStats(Catalog);
        Assert.Equal(0, stats.TotalThrustN);
        Assert.Equal(thrust, stats.ReverseThrustN);
        Assert.Equal(thrust, stats.LeftThrustN);
        Assert.Contains(bothTurned.Validate(Catalog, DesignRole.Ship), e => e.Contains("rear-facing thruster")); // still needs forward thrust
    }

    [Fact]
    public void ReverseAndStrafe_PushBackwardAndSideways()
    {
        var design = WithThrusterRotations(2, 1); // left thruster reversed, right one pushing right
        var stats = design.ComputeStats(Catalog);
        var thrust = Catalog.Get("thruster").ThrustN;

        var craft = new CraftState(new CraftId(1), "t", design, stats, Vector2D.Zero, 0) { Controls = new ShipControls(-1, 0, false) };
        for (var i = 0; i < FixedStepClock.TicksPerSecond; i++) FlightModel.Step(craft, FixedStepClock.TickSeconds);
        Assert.Equal(thrust / stats.TotalMassKg, craft.Velocity.Y, 9); // heading 0: backward is +Y on screen
        Assert.Equal(0, craft.Velocity.X, 9);

        craft = new CraftState(new CraftId(1), "t", design, stats, Vector2D.Zero, 0) { Controls = new ShipControls(0, 0, false, Strafe: 1) };
        for (var i = 0; i < FixedStepClock.TicksPerSecond; i++) FlightModel.Step(craft, FixedStepClock.TickSeconds);
        Assert.Equal(thrust / stats.TotalMassKg, craft.Velocity.X, 9); // right is +X
        Assert.Equal(0, craft.Velocity.Y, 9);

        craft = new CraftState(new CraftId(1), "t", design, stats, Vector2D.Zero, 0) { Controls = new ShipControls(0, 0, false, Strafe: -1) };
        FlightModel.Step(craft, FixedStepClock.TickSeconds);
        Assert.Equal(0, craft.SpeedMps); // no left-facing thruster: strafing left does nothing
    }

    [Fact]
    public void Reverse_DrawsTheReverseThrustersPower_AndUndocks()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var design = WithThrusterRotations(2, 0);
        var ship = new CraftState(sector.AllocateCraftId(), "R", design, design.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(ship);
        sector.RestoreDock(ship.Id, yard.Id, sector.FreePorts(yard)[0]);
        ship.StoredEnergyKJ = 1_000; // part charged, so the battery cap doesn't hide what was used
        var energy = ship.StoredEnergyKJ;

        ship.Controls = new ShipControls(-1, 0, false);
        sector.Step();
        Assert.False(ship.IsDocked);
        var used = energy + ship.Stats.PowerGenerationKW * FixedStepClock.TickSeconds - ship.StoredEnergyKJ;
        Assert.Equal(ship.Stats.ReversePowerKW * FixedStepClock.TickSeconds, used, 6);
    }

    [Fact]
    public void ForwardThrottle_UsesOnlyForwardThrusters()
    {
        var design = WithThrusterRotations(0, 1);
        var stats = design.ComputeStats(Catalog);
        var craft = new CraftState(new CraftId(1), "t", design, stats, Vector2D.Zero, 0)
        {
            Controls = new ShipControls(1, 0, false),
        };
        for (var i = 0; i < FixedStepClock.TicksPerSecond; i++) FlightModel.Step(craft, FixedStepClock.TickSeconds);

        Assert.Equal(stats.TotalThrustN / stats.TotalMassKg, craft.SpeedMps, 9);
        Assert.Equal(0, craft.Velocity.X, 9); // the sideways thruster only fires when strafing
    }

    [Fact]
    public void Rotation_RoundTripsThroughDesignJson_AndIsOmittedWhenZero()
    {
        var design = WithThrusterRotations(3, 0);
        var json = design.ToJson();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"rot\""));
        var reloaded = ShipDesign.FromJson(json);
        Assert.Equal(design.Cells, reloaded.Cells);
        Assert.Equal(json, reloaded.ToJson());
    }

    [Fact]
    public void Version1DesignFiles_StillLoad_WithNoRotation()
    {
        var v1 = """{ "schemaVersion": 1, "id": "old", "cells": [ { "x": 0, "y": 0, "part": "hull" } ] }""";
        Assert.Equal(0, ShipDesign.FromJson(v1).Cells[0].Rotation);
        Assert.Throws<FormatException>(() => ShipDesign.FromJson(
            """{ "schemaVersion": 2, "id": "bad", "cells": [ { "x": 0, "y": 0, "part": "hull", "rot": 4 } ] }"""));
    }

    [Fact]
    public void Editor_PlacesWithRotation_AndReplacingRotationIsAnEdit()
    {
        var editor = new BlueprintEditor(Catalog);
        Assert.True(editor.Place(new GridPos(0, 0), "thruster", 2));
        Assert.Equal(2, editor.Cells[new GridPos(0, 0)].Rotation);

        Assert.False(editor.Place(new GridPos(0, 0), "thruster", 2)); // same part, same rotation: no change
        Assert.True(editor.Place(new GridPos(0, 0), "thruster", 0));
        Assert.False(editor.Place(new GridPos(1, 0), "hull", 4));
        Assert.True(editor.Undo());
        Assert.Equal(2, editor.Cells[new GridPos(0, 0)].Rotation);
    }

    [Fact]
    public void Editor_TreatsAFabricatorLayoutAsAStation()
    {
        var editor = new BlueprintEditor(Catalog);
        editor.Load(TestData.StarterShipyard());

        Assert.Equal(DesignRole.Station, editor.Role);
        Assert.Empty(editor.Errors);
    }
}
