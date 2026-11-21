using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class AnchoringTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static CraftState AddCraft(SectorState sector, ShipDesign design, Vector2D position) =>
        AddCraft(sector, design, position, design.ComputeStats(Catalog));

    private static CraftState AddCraft(SectorState sector, ShipDesign design, Vector2D position, ShipStats stats)
    {
        var craft = new CraftState(sector.AllocateCraftId(), design.Name, design, stats, position, 0);
        sector.AddCraft(craft);
        return craft;
    }

    private static void Run(SectorState sector, CraftState craft, ShipControls controls, int ticks)
    {
        craft.Controls = controls;
        for (var i = 0; i < ticks; i++) sector.Step();
    }

    /// <summary>Puts a craft 1 m above the asteroid, drifting down onto it, and runs until it lands.</summary>
    private static (SectorState Sector, CraftState Craft, CelestialBodyState Rock) LandOnRock(ShipDesign design)
    {
        var sector = SectorGenerator.Generate(NewGame.DefaultSeed);
        var rock = sector.Bodies.First(b => b.IsMineable);
        var stats = design.ComputeStats(Catalog);
        var craft = AddCraft(sector, design,
            sector.GetPosition(rock.Id) + new Vector2D(0, -(rock.RadiusM + stats.BoundingRadiusM + 1)), stats);
        craft.Velocity = sector.GetVelocityAt(rock.Id, sector.TimeS) + new Vector2D(0, 3);
        Run(sector, craft, ShipControls.Idle, 20);
        Assert.True(craft.IsLanded);
        return (sector, craft, rock);
    }

    private static ShipDesign Outpost() => new("outpost", "Outpost", new[]
    {
        new DesignCell(new GridPos(0, 0), "foundation"),
        new DesignCell(new GridPos(1, 0), "fabricator"),
    });

    [Fact]
    public void StarterShip_HasAClamp_AndCanAnchor()
    {
        var stats = TestData.StarterShip().ComputeStats(Catalog);
        Assert.Equal(1, stats.AnchorClampCount);
        Assert.True(stats.CanAnchor);
        Assert.False(stats.IsFixedStructure);
    }

    [Fact]
    public void AnchoredShip_OnTheMovingAsteroid_CannotLiftOff_UntilReleased()
    {
        // The Phase 2 proof: a ship with a clamp attaches to the mineable body.
        var (sector, craft, rock) = LandOnRock(TestData.StarterShip());
        Assert.True(sector.TryAnchor(craft.Id).Done);
        var offset = craft.LandedOffset;

        craft.HeadingRad = 0; // nose straight away from the rock (it landed on the rock's −Y side)
        Run(sector, craft, new ShipControls(1, 0, false), 600); // 30 s of full thrust

        Assert.True(craft.IsLanded && craft.IsAnchored);
        Assert.True((craft.Position - sector.GetPosition(rock.Id) - offset).Length < 1e-6);

        Assert.True(sector.TryRelease(craft.Id).Done);
        Run(sector, craft, new ShipControls(1, 0, false), 20);
        Assert.False(craft.IsLanded);
    }

    [Fact]
    public void Anchor_IsRefused_WithAReason_WhenNotLandedOrWithoutAClamp()
    {
        var sector = SectorGenerator.Generate(NewGame.DefaultSeed);
        var flying = AddCraft(sector, TestData.StarterShip(), SectorGenerator.PlayerSpawnPoint(sector));
        var refusal = sector.TryAnchor(flying.Id);
        Assert.False(refusal.Done);
        Assert.Contains("landed", refusal.Message);

        var noClamp = new ShipDesign("plain", "Plain", TestData.StarterShip().Cells.Where(c => c.PartId != "anchor_clamp"));
        var (sector2, plain, _) = LandOnRock(noClamp);
        var noClampRefusal = sector2.TryAnchor(plain.Id);
        Assert.False(noClampRefusal.Done);
        Assert.Contains("no anchor clamp", noClampRefusal.Message);
        Assert.False(sector2.TryRelease(plain.Id).Done);
    }

    [Fact]
    public void Foundation_AnchorsOnLanding_AndCanNeverBeReleased()
    {
        var (sector, outpost, _) = LandOnRock(Outpost());

        Assert.True(outpost.IsAnchored);
        var release = sector.TryRelease(outpost.Id);
        Assert.False(release.Done);
        Assert.Contains("permanently", release.Message);
    }

    [Fact]
    public void Foundation_IsCheaperThanAClamp_AndAShipCantHaveOne()
    {
        var foundation = Catalog.Get("foundation");
        var clamp = Catalog.Get("anchor_clamp");
        Assert.True(foundation.MassKg < clamp.MassKg);
        Assert.Equal(0, foundation.Bill["electronics"]); // no mechanism

        var shipWithFoundation = new ShipDesign("x", "x", TestData.StarterShip().Cells.Append(new DesignCell(new GridPos(0, 3), "foundation")));
        Assert.Contains(shipWithFoundation.Validate(Catalog, DesignRole.Ship), e => e.Contains("Foundation"));
        Assert.Empty(Outpost().Validate(Catalog, DesignRole.Station));
    }

    [Fact]
    public void AnchoringAndReleasing_ChangesNothingButTheFlag()
    {
        var (sector, craft, _) = LandOnRock(TestData.StarterShip());
        var before = SaveGameSerializer.ToJson(sector, craft.Id);

        for (var i = 0; i < 50; i++)
        {
            sector.TryAnchor(craft.Id);
            sector.TryRelease(craft.Id);
        }

        Assert.Equal(before, SaveGameSerializer.ToJson(sector, craft.Id));
        Assert.Single(sector.Crafts);
    }

    [Fact]
    public void SaveWhileAnchored_LoadsAnchored_AndStaysPut()
    {
        var (sector, craft, rock) = LandOnRock(TestData.StarterShip());
        sector.TryAnchor(craft.Id);

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, craft.Id), Catalog).Sector;
        var copy = loaded.GetCraft(craft.Id);
        Assert.True(copy.IsAnchored);

        copy.HeadingRad = 0;
        Run(loaded, copy, new ShipControls(1, 0, false), 100);
        Assert.True(copy.IsLanded);
    }

    [Fact]
    public void Load_RejectsImpossibleAnchorStates()
    {
        var (sector, craft, _) = LandOnRock(TestData.StarterShip());
        sector.TryAnchor(craft.Id);
        var json = SaveGameSerializer.ToJson(sector, craft.Id);
        Assert.Contains("\"anchored\": true", json);

        // Anchored but flying.
        var flying = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var c = flying["sector"]!["crafts"]![0]!.AsObject();
        c["landedOnBodyId"] = 0;
        c["landedOffset"] = null;
        Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(flying.ToJsonString(), Catalog));
    }

    [Fact]
    public void OlderSave_LandedFoundation_LoadsAnchored()
    {
        var (sector, outpost, _) = LandOnRock(Outpost());
        var node = System.Text.Json.Nodes.JsonNode.Parse(SaveGameSerializer.ToJson(sector, CraftId.None))!;
        node["schemaVersion"] = 3;
        node["sector"]!["crafts"]![0]!.AsObject().Remove("anchored");

        var loaded = SaveGameSerializer.FromJson(node.ToJsonString(), Catalog).Sector;

        Assert.True(loaded.GetCraft(outpost.Id).IsAnchored);
    }
}
