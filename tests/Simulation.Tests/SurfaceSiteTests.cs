using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#70 surface sites: layout, mining by site, survey by horizon, jobs, saves and migration.</summary>
public sealed class SurfaceSiteTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static CelestialBodyState Rock(SectorState sector, string name) => sector.Bodies.Single(b => b.Name == name);

    /// <summary>Lands a craft on a body at a bearing, clamp side down (like <see cref="TestData.LandAt"/>, which uses −π/2).</summary>
    private static void LandAtBearing(SectorState sector, CraftState craft, CelestialBodyState body, double bearing)
    {
        if (craft.IsDocked) sector.TryUndock(craft.Id);
        craft.RailOrbit = null;
        var offset = new Vector2D(Math.Cos(bearing), Math.Sin(bearing)) * (body.RadiusM + craft.CollisionRadiusM + 1);
        craft.Position = craft.PreviousPosition = sector.GetPosition(body.Id) + offset;
        craft.HeadingRad = craft.PreviousHeadingRad = FlightModel.WrapAngle(bearing + Math.PI / 2);
        craft.Velocity = sector.GetVelocityAt(body.Id, sector.TimeS);
        sector.RestoreLanding(craft.Id, body.Id, offset);
    }

    [Fact]
    public void Sites_split_every_deposit_exactly_and_the_same_way_every_time()
    {
        var a = TestData.NewGame(seed: 11, surveyAll: false).Sector;
        var b = TestData.NewGame(seed: 11, surveyAll: false).Sector;
        foreach (var body in a.Bodies.Where(x => x.IsMineable))
        {
            Assert.InRange(body.Sites.Count, SurfaceSite.MinSites, SurfaceSite.MaxSites);
            foreach (var (material, kg) in body.DepositsKg)
            {
                Assert.Equal(kg, body.Sites.Sum(s => s.DepositsKg.GetValueOrDefault(material)));
                Assert.Contains(body.Sites, s => s.DepositsKg.ContainsKey(material));
            }
            var twin = b.Bodies.Single(x => x.Id == body.Id);
            Assert.Equal(body.Sites.Select(s => string.Join(",", s.DepositsKg)), twin.Sites.Select(s => string.Join(",", s.DepositsKg)));
            Assert.All(body.Sites, s => Assert.False(s.Surveyed));
        }
        Assert.All(a.Bodies.Where(x => !x.IsMineable), x => Assert.Empty(x.Sites));
    }

    [Fact]
    public void Sites_tile_the_rim_in_order()
    {
        var body = Rock(TestData.NewGame().Sector, "Rock-1");
        foreach (var site in body.Sites)
        {
            Assert.Same(site, body.SiteAt(site.CentreRad));
            Assert.Same(site, body.SiteAt(site.StartRad + 1e-6));
        }
        Assert.Equal($"Rock-1 {SurfaceSite.Letter(0)}", body.Sites[0].Name);
    }

    [Fact]
    public void Mining_takes_ore_from_the_site_under_the_drill_and_rock_elsewhere()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = Rock(sector, "Rock-1");
        var oreSite = rock.Sites.First(s => s.DepositsKg.Count > 0);
        var bareSite = rock.Sites.First(s => s.DepositsKg.Count == 0);
        var ore = oreSite.DepositsKg.Keys.First();
        var player = sector.GetCraft(game.PlayerCraftId);
        var ledger = sector.ComputeMassLedger().TotalKg;

        LandAtBearing(sector, player, rock, oreSite.CentreRad);
        Assert.True(sector.TryAnchor(player.Id).Done);
        var before = oreSite.DepositsKg[ore];
        for (var i = 0; i < 40; i++) sector.Step();
        Assert.True(player.Inventory[ore] > 0);
        Assert.Equal(before - player.Inventory[ore], oreSite.DepositsKg[ore]);
        Assert.Equal(rock.DepositsKg[ore], rock.Sites.Sum(s => s.DepositsKg.GetValueOrDefault(ore)));
        Assert.Equal(ledger, sector.ComputeMassLedger().TotalKg);

        // A site with no ore: by hand the drill takes the body's bulk, and no site's ore changes.
        sector.TryRelease(player.Id);
        player.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { [ore] = player.Inventory[ore] });
        var oreAfter = rock.DepositsKg[ore];
        LandAtBearing(sector, player, rock, bareSite.CentreRad);
        Assert.True(sector.TryAnchor(player.Id).Done);
        Assert.Equal(rock.BulkMaterialId, sector.MiningMaterial(player));
        for (var i = 0; i < 40; i++) sector.Step();
        Assert.True(player.Inventory[rock.BulkMaterialId] > 0);
        Assert.Equal(oreAfter, rock.DepositsKg[ore]);
        Assert.Same(bareSite, sector.SiteUnder(player));
    }

    [Fact]
    public void A_mining_job_waits_until_a_site_with_its_ore_is_surveyed_then_mines_there()
    {
        var game = TestData.NewGame(surveyAll: false);
        var sector = game.Sector;
        var rock = Rock(sector, "Rock-1");
        var oreSite = rock.Sites.First(s => s.DepositsKg.Count > 0);
        var bareSite = rock.Sites.First(s => s.DepositsKg.Count == 0);
        var ore = oreSite.DepositsKg.Keys.First();
        sector.MarkSiteSurveyed(rock.Id, bareSite.Index);
        sector.PilotCraftId = CraftId.None; // nobody flying: the miner mustn't survey the rock by passing it

        Assert.True(sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, ore, game.ShipyardId, 0.25)).Done);
        var miner = sector.GetCraft(game.PlayerCraftId);
        var job = (MiningJob)miner.Job!;
        // It lands on the rock and waits there, anchored: not docked (holding a berth) nor in space (in gravity).
        for (var i = 0; i < 20 * 60 * FixedStepClock.TicksPerSecond && job.State != MiningJobState.Waiting; i++) sector.Step();
        Assert.Equal(MiningJobState.Waiting, job.State);
        Assert.Contains("no surveyed site", job.WaitReason);
        Assert.Equal(rock.Id, miner.LandedOnBodyId);

        sector.MarkSiteSurveyed(rock.Id, oreSite.Index);
        for (var i = 0; i < 40 * 60 * FixedStepClock.TicksPerSecond && job.State != MiningJobState.Mining; i++) sector.Step();
        Assert.Equal(MiningJobState.Mining, job.State);
        Assert.Same(oreSite, sector.SiteUnder(miner));
        Assert.Equal(0, miner.PartsLost);
    }

    [Fact]
    public void Sites_survive_a_save_exactly_and_a_shrunken_body_keeps_its_sites()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = Rock(sector, "Rock-2");
        var site = rock.Sites.First(s => s.DepositsKg.Count > 0);
        var ore = site.DepositsKg.Keys.First();
        rock.Extract(ore, 1_234, site.CentreRad);
        rock.Extract(rock.BulkMaterialId, rock.BulkKg / 2); // much smaller now
        var count = rock.Sites.Count;
        sector.GetBody(Rock(sector, "Rock-3").Id).Sites[1].Surveyed = false;

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), Catalog).Sector;
        var twin = Rock(loaded, "Rock-2");
        Assert.Equal(count, twin.Sites.Count);
        Assert.Equal(rock.Sites.Select(s => string.Join(",", s.DepositsKg)), twin.Sites.Select(s => string.Join(",", s.DepositsKg)));
        Assert.Equal(Rock(sector, "Rock-3").Sites.Select(s => s.Surveyed), Rock(loaded, "Rock-3").Sites.Select(s => s.Surveyed));
        Assert.Equal(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
    }

    [Fact]
    public void Older_saves_without_sites_get_the_same_layout_and_their_survey()
    {
        var game = TestData.NewGame(surveyAll: false);
        var sector = game.Sector;
        sector.MarkSurveyed(Rock(sector, "Rock-1").Id);
        var root = JsonNode.Parse(SaveGameSerializer.ToJson(sector, game.PlayerCraftId))!.AsObject();
        foreach (var body in root["sector"]!["bodies"]!.AsArray()) body!.AsObject().Remove("sites");

        var loaded = SaveGameSerializer.FromJson(root.ToJsonString(), Catalog).Sector;
        foreach (var body in sector.Bodies.Where(b => b.IsMineable))
        {
            var twin = loaded.Bodies.Single(b => b.Id == body.Id);
            Assert.Equal(body.Sites.Select(s => string.Join(",", s.DepositsKg)), twin.Sites.Select(s => string.Join(",", s.DepositsKg)));
        }
        Assert.True(Rock(loaded, "Rock-1").Surveyed);
        Assert.False(Rock(loaded, "Rock-2").AnySiteSurveyed);
    }

    [Fact]
    public void Sites_that_dont_add_up_are_refused()
    {
        var game = TestData.NewGame();
        var root = JsonNode.Parse(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId))!.AsObject();
        var rock = root["sector"]!["bodies"]!.AsArray().First(b => b!["name"]!.GetValue<string>() == "Rock-1")!;
        var site = rock["sites"]!.AsArray().First(s => s!["depositsKg"] is not null)!;
        var deposits = site["depositsKg"]!.AsObject();
        var key = deposits.First().Key;
        deposits[key] = deposits[key]!.GetValue<long>() + 1;
        Assert.ThrowsAny<Exception>(() => SaveGameSerializer.FromJson(root.ToJsonString(), Catalog));
    }
}
