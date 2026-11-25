using System;
using System.Diagnostics;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Finding targets, favourites and names (#109), and the smart target keys (#110).</summary>
public sealed class FindingTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static (SectorState Sector, CraftState Player, CraftState Yard) Game()
    {
        var game = TestData.NewGame();
        return (game.Sector, game.Sector.GetCraft(game.PlayerCraftId), game.Sector.Crafts.First(c => c.IsShipyard));
    }

    private static CraftState Add(SectorState sector, string name, Vector2D at, int faction = 0, ShipDesign? design = null)
    {
        design ??= TestData.StarterShip();
        var craft = new CraftState(sector.AllocateCraftId(), name, design, design.ComputeStats(Catalog), at, 0) { FactionId = faction };
        sector.AddCraft(craft);
        return craft;
    }

    private static Vector2D Off(CraftState from, double x, double y = 0) => from.Position + new Vector2D(x, y);

    private static CraftId[] Found(SectorState sector, CraftState from, TargetQuery query) => sector.Find(from, query).Where(f => !f.Target.Craft.IsNone).Select(f => f.Target.Craft).ToArray();

    // --- Finding ---

    [Fact]
    public void Finds_by_name_and_kind_nearest_first_ties_by_id()
    {
        var (sector, player, _) = Game();
        var far = Add(sector, "Hauler Two", Off(player, 5_000));
        var left = Add(sector, "Hauler One", Off(player, -2_000));
        var right = Add(sector, "hauler three", Off(player, 2_000));
        Assert.Equal(new[] { left.Id, right.Id, far.Id }, Found(sector, player, new TargetQuery { Text = " HAULER " }));
        Assert.Equal(new[] { far.Id }, Found(sector, player, new TargetQuery { Text = far.Id.ToString() }).Where(id => id == far.Id));
        Assert.Empty(Found(sector, player, new TargetQuery { Text = "hauler", Kind = TargetKind.Stations }));
        Assert.DoesNotContain(player.Id, Found(sector, player, new TargetQuery()));
        var bodies = sector.Find(player, new TargetQuery { Kind = TargetKind.Bodies });
        Assert.Equal(sector.Bodies.Count, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(0, b.Jumps));
        left.IsWreck = true;
        Assert.Equal(new[] { left.Id }, Found(sector, player, new TargetQuery { Kind = TargetKind.Wrecks }));
    }

    [Fact]
    public void Filters_by_owner_ore_free_port_and_trouble()
    {
        var (sector, player, yard) = Game();
        var theirs = Add(sector, "Theirs", Off(player, 3_000), faction: 7);
        Assert.Equal(new[] { theirs.Id }, Found(sector, player, new TargetQuery { Owner = 7 }));
        Assert.DoesNotContain(theirs.Id, Found(sector, player, new TargetQuery { Owner = 0 }));
        Assert.DoesNotContain(sector.Find(player, new TargetQuery { Owner = 0 }), f => f.Target.Craft.IsNone); // bodies have no owner

        foreach (var site in sector.Bodies.SelectMany(b => b.Sites)) site.Surveyed = false; // test games start surveyed
        var before = sector.Find(player, new TargetQuery { Ore = "iron_ore" }).Select(f => f.Target.Body).ToList();
        var rock = sector.Bodies.First(b => !before.Contains(b.Id) && b.Sites.Any(x => x.DepositsKg.ContainsKey("iron_ore")));
        sector.MarkSiteSurveyed(rock.Id, rock.Sites.First(x => !x.DepositsKg.ContainsKey("iron_ore")).Index); // not where it is
        Assert.DoesNotContain(sector.Find(player, new TargetQuery { Ore = "iron_ore" }), f => f.Target.Body == rock.Id);
        sector.MarkSiteSurveyed(rock.Id, rock.Sites.First(x => x.DepositsKg.ContainsKey("iron_ore")).Index);
        Assert.Equal(before.Append(rock.Id).OrderBy(b => b.Value), sector.Find(player, new TargetQuery { Ore = "iron_ore" }).Select(f => f.Target.Body).OrderBy(b => b.Value));
        Assert.Contains("iron_ore", sector.SurveyedOres());

        Assert.Equal(new[] { yard.Id }, Found(sector, player, new TargetQuery { FreePort = true }));
        for (var port = 0; port < yard.Stats.DockingPorts.Count; port++) sector.RestoreDock(Add(sector, $"Docked {port}", Off(player, 9_000 + port)).Id, yard.Id, port);
        Assert.Empty(Found(sector, player, new TargetQuery { FreePort = true }));

        Assert.DoesNotContain(yard.Id, Found(sector, player, new TargetQuery { Attention = true }));
        sector.DebugDamagePart(yard.Id, yard.Design.Cells[0].Pos, 0.5);
        Assert.Contains(yard.Id, Found(sector, player, new TargetQuery { Attention = true }));
        Assert.DoesNotContain(theirs.Id, Found(sector, player, new TargetQuery { Attention = true }));
    }

    [Fact]
    public void Other_systems_are_found_with_the_way_through_the_gates()
    {
        var (sector, near, far, star, ship) = FactionTests.OpenedSystem();
        var home = sector.Stars[0];
        ship.DockedToCraftId = CraftId.None;
        ship.Position = near.Position + new Vector2D(1_000, 0);
        sector.Step();
        var body = sector.Bodies.First(b => b.Kind == BodyKind.Planet && sector.StarOf(b.Id)?.Id == star.Id);
        var all = sector.Find(ship, new TargetQuery());
        var found = all.Single(f => f.Target.Body == body.Id);
        Assert.Equal((1, (BodyId?)star.Id, near.Id), (found.Jumps, found.Star, found.FirstGate));
        var direct = ship.Position.DistanceTo(near.Position) + far.Position.DistanceTo(sector.GetPosition(body.Id)) - body.RadiusM;
        Assert.Equal(direct, found.DistanceM, 3);
        Assert.Equal(all.OrderBy(f => f.DistanceM).Select(f => f.Target), all.Select(f => f.Target));
        Assert.All(sector.Find(ship, new TargetQuery { Star = home.Id }), f => Assert.Equal(0, f.Jumps));
        Assert.Equal(near.Id, First(sector.GateTargets(ship)).Craft); // not the far one
        Assert.DoesNotContain(sector.DockTargets(ship).Targets, t => t.Craft == far.Id);

        near.GateLinkId = far.GateLinkId = CraftId.None; // no way there any more: last
        sector.Step();
        all = sector.Find(ship, new TargetQuery());
        Assert.Equal(-1, all.Single(f => f.Target.Body == body.Id).Jumps);
        Assert.Equal(-1, all[^1].Jumps);
        Assert.Contains(all.TakeWhile(f => f.Jumps == 0), f => f.Target.Body == home.Id);
    }

    [Fact]
    public void Ten_thousand_craft_are_searched_quickly()
    {
        var (sector, player, _) = Game();
        for (var i = 0; i < 10_000; i++) Add(sector, $"Ship {i}", new Vector2D(player.Position.X + 1_000 + i * 100, player.Position.Y + 5_000));
        sector.Find(player, new TargetQuery());
        var watch = Stopwatch.StartNew();
        Assert.True(sector.Find(player, new TargetQuery()).Count > 10_000);
        Assert.Single(sector.Find(player, new TargetQuery { Text = "Ship 9999" }));
        sector.Find(player, new TargetQuery { FreePort = true });
        Assert.True(watch.ElapsedMilliseconds < 1_000, $"{watch.ElapsedMilliseconds} ms");
    }

    // --- Favourites and names ---

    [Fact]
    public void Favourites_keep_one_slot_each_survive_saving_and_know_whats_gone()
    {
        var (sector, player, yard) = Game();
        sector.SetFavourite(0, TargetRef.Of(yard));
        sector.SetFavourite(8, TargetRef.Of(sector.Bodies[2]));
        sector.SetFavourite(3, TargetRef.Of(yard)); // moves
        Assert.Null(sector.Favourites[0]);
        var gone = Add(sector, "Doomed", Off(player, 4_000));
        sector.SetFavourite(1, TargetRef.Of(gone));
        sector.RemoveCraft(gone);
        sector.SetFavourite(5, new TargetRef(gone.Id, BodyId.None)); // doesn't exist: nothing kept
        Assert.Null(sector.Favourites[5]);
        Assert.Equal("Doomed", sector.Favourites[1]!.Name);
        Assert.False(sector.Exists(sector.Favourites[1]!.Target));

        var json = SaveGameSerializer.ToJson(sector, player.Id);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(sector.Favourites, loaded.Favourites);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, player.Id));
        sector.SetFavourite(3, TargetRef.None);
        Assert.Null(sector.Favourites[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => sector.SetFavourite(9, TargetRef.None));
    }

    [Fact]
    public void Bodies_and_wrecks_can_be_named_and_bodies_go_back()
    {
        var (sector, player, _) = Game();
        var rock = TestData.HomeRocks(sector)[0];
        var own = rock.Name;
        Assert.True(sector.RenameBody(rock.Id, " Iron  Base ").Done);
        Assert.Equal(("Iron Base", own), (rock.Name, rock.OriginalName));
        Assert.StartsWith("Iron Base ", rock.Sites[0].Name);
        Assert.Single(sector.Find(player, new TargetQuery { Text = own, Kind = TargetKind.Bodies }), f => f.Target.Body == rock.Id); // by either name
        Assert.False(sector.RenameBody(sector.Bodies.First(b => b != rock).Id, "iron base").Done);
        Assert.False(sector.RenameBody(sector.Bodies.First(b => b != rock).Id, own).Done); // would clash if it went back
        Assert.False(sector.RenameBody(rock.Id, new string('x', 33)).Done);

        var json = SaveGameSerializer.ToJson(sector, player.Id);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(("Iron Base", own), (loaded.GetBody(rock.Id).Name, loaded.GetBody(rock.Id).OriginalName));
        Assert.StartsWith("Iron Base ", loaded.GetBody(rock.Id).Sites[0].Name);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, player.Id));
        Assert.True(sector.RenameBody(rock.Id, "").Done);
        Assert.Equal((own, (string?)null), (rock.Name, rock.OriginalName));

        var wreck = Add(sector, "Raider 1", Off(player, 4_000), faction: 3);
        Assert.False(sector.RenameCraft(wreck.Id, "Salvage me").Done);
        wreck.IsWreck = true;
        Assert.True(sector.RenameCraft(wreck.Id, "Salvage me").Done);
    }

    // --- Smart keys ---

    private static TargetRef First(TargetChoice choice) => choice.Targets.Count > 0 ? choice.Targets[0] : TargetRef.None;

    [Fact]
    public void The_enemy_key_prefers_an_attacker_to_a_nearer_enemy()
    {
        var (sector, player, _) = Game();
        Assert.Contains("No enemies", sector.EnemyTargets(player).Why);
        var harmless = Add(sector, "Unarmed", Off(player, 150), faction: 3);
        var same = Add(sector, "Unarmed 2", Off(player, -150), faction: 3);
        Assert.Equal(new[] { harmless.Id, same.Id }, sector.EnemyTargets(player).Targets.Select(t => t.Craft)); // a tie: by id
        var raider = Add(sector, "Raider", Off(player, 0, 300), faction: 3, design: SectorState.RaiderDesign);
        raider.HeadingRad = 0; // its gun points at the player
        var choice = sector.EnemyTargets(player);
        Assert.Equal(raider.Id, First(choice).Craft);
        Assert.Contains("attacking you", choice.Why);
        Assert.Equal(raider.Id, First(sector.SmartTargets(player, null)).Craft);
        Assert.Equal(3, choice.Targets.Count);
    }

    [Fact]
    public void The_dock_key_wants_a_free_port_and_a_shipyard_when_damaged()
    {
        var (sector, player, yard) = Game();
        var depot = Add(sector, "Depot", Off(player, 120), design: new ShipDesign("d", "Depot", new[] { new DesignCell(new GridPos(0, 0), "storage_silo"), new DesignCell(new GridPos(1, 0), "docking_port") }));
        Assert.Equal(depot.Id, First(sector.DockTargets(player)).Craft);
        Assert.Contains(sector.DockTargets(player).Targets, t => t.Craft == yard.Id);
        sector.DebugDamagePart(player.Id, player.Design.Cells[0].Pos, 0.5);
        Assert.Equal(yard.Id, First(sector.DockTargets(player)).Craft);
        Assert.Equal(yard.Id, First(sector.SmartTargets(player, null)).Craft);
        sector.RestoreDock(Add(sector, "Guest", Off(player, 9_000)).Id, depot.Id, 0);
        Assert.DoesNotContain(sector.DockTargets(player).Targets, t => t.Craft == depot.Id);
        Assert.Contains("No warp gate", sector.GateTargets(player).Why);
    }

    [Fact]
    public void The_work_key_goes_by_the_tools_and_the_chosen_ore()
    {
        var (sector, player, yard) = Game();
        Assert.Contains("no drill", sector.WorkTargets(yard, null).Why);
        foreach (var rock in TestData.HomeRocks(sector)) sector.MarkSurveyed(rock.Id);
        var any = sector.WorkTargets(player, null);
        Assert.NotEmpty(any.Targets);
        var copper = sector.WorkTargets(player, "copper_ore");
        Assert.NotEmpty(copper.Targets);
        Assert.All(copper.Targets, t => Assert.Contains(sector.GetBody(t.Body).Sites, s => s.Surveyed && s.DepositsKg.ContainsKey("copper_ore")));
        Assert.True(copper.Targets.Count < any.Targets.Count);
        Assert.Contains("No surveyed exotic", sector.WorkTargets(player, "exotic_ore").Why);
        var wreck = Add(sector, "Hulk", Off(player, 200));
        wreck.IsWreck = true;
        Assert.DoesNotContain(sector.WorkTargets(player, null).Targets, t => t.Craft == wreck.Id); // no salvage arm
        var cutter = Add(sector, "Cutter", Off(player, 300), design: new ShipDesign("c", "Cutter", new[]
        {
            new DesignCell(new GridPos(0, 0), "cockpit"), new DesignCell(new GridPos(0, 1), "thruster"), new DesignCell(new GridPos(1, 0), "salvage_arm"),
        }));
        Assert.Equal(new[] { wreck.Id }, sector.WorkTargets(cutter, null).Targets.Select(t => t.Craft));
    }

    [Fact]
    public void Alerts_say_whos_in_trouble_and_the_smart_key_always_finds_something()
    {
        var (sector, player, yard) = Game();
        Assert.Empty(sector.Alerts());
        Assert.False(First(sector.SmartTargets(player, null)).IsNone);
        yard.PowerShort = true;
        var alert = Assert.Single(sector.Alerts());
        Assert.Equal((AlertKind.Power, yard.Id), (alert.Kind, alert.Target.Craft));
        Assert.Contains("no power", alert.Text);
    }

    [Fact]
    public void Choosing_targets_changes_nothing_in_the_game()
    {
        var (sector, player, _) = Game();
        var before = SaveGameSerializer.ToJson(sector, player.Id);
        sector.SmartTargets(player, null);
        sector.EnemyTargets(player);
        sector.DockTargets(player);
        sector.Find(player, new TargetQuery { FreePort = true });
        sector.Alerts();
        Assert.Equal(before, SaveGameSerializer.ToJson(sector, player.Id));
    }
}
