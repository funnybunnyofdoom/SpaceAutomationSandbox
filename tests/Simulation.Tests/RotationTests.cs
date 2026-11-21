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
    public void OnlyRearFacingThrusters_CountAsForwardThrust()
    {
        var thrust = Catalog.Get("thruster").ThrustN;

        var oneTurned = WithThrusterRotations(0, 1).ComputeStats(Catalog);
        Assert.Equal(thrust, oneTurned.TotalThrustN);
        Assert.Equal(thrust, oneTurned.NonForwardThrustN);
        Assert.Contains(WithThrusterRotations(0, 1).Warnings(Catalog), w => w.Contains("1 thruster"));

        var bothTurned = WithThrusterRotations(2, 3);
        Assert.Equal(0, bothTurned.ComputeStats(Catalog).TotalThrustN);
        Assert.Contains(bothTurned.Validate(Catalog, DesignRole.Ship), e => e.Contains("rear-facing thruster"));
    }

    [Fact]
    public void RotatedThrusterShip_AcceleratesWithItsForwardThrustOnly()
    {
        var design = WithThrusterRotations(0, 1);
        var stats = design.ComputeStats(Catalog);
        var craft = new CraftState(new CraftId(1), "t", design, stats, Vector2D.Zero, 0)
        {
            Controls = new ShipControls(1, 0, false),
        };
        for (var i = 0; i < FixedStepClock.TicksPerSecond; i++) FlightModel.Step(craft, FixedStepClock.TickSeconds);

        Assert.Equal(stats.TotalThrustN / stats.TotalMassKg, craft.SpeedMps, 9);
        Assert.Equal(0, craft.Velocity.X, 9); // the sideways thruster doesn't push yet
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
