using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>World options (#119) and points of interest (#122).</summary>
public sealed class PointTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static NewGameResult Game(WorldAmount amount = WorldAmount.Often, ulong seed = 5) => Create(seed, Only(amount));

    private static NewGameResult Create(ulong seed, WorldOptions? options)
    {
        var game = NewGame.Create(seed, Catalog, TestData.StarterShip(), TestData.StarterShipyard(), options: options);
        game.Sector.GetCraft(game.PlayerCraftId).Inventory.Add("fuel", 100);
        return game;
    }

    private static WorldOptions Only(WorldAmount amount)
    {
        var options = new WorldOptions();
        options[WorldOptions.PointsOfInterest] = amount;
        return options;
    }

    private static SectorState Again(SectorState sector, CraftId player) => SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, player), Catalog).Sector;

    private static string Json(NewGameResult game) => SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId);

    private static void SavesExactly(SectorState sector, CraftId player) =>
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(Again(sector, player), player));

    private static IEnumerable<PointOfInterest> All(SectorState sector, PointType type) => sector.Points.Where(p => sector.KindOf(p)!.Type == type).ToList();

    private static PointOfInterest Of(SectorState sector, PointType type) => All(sector, type).First();

    /// <summary>The ship beside a point of interest, holding still by it; landed at a ruin.</summary>
    private static void Go(SectorState sector, CraftState ship, PointOfInterest point, double offM)
    {
        var at = sector.PointPosition(point, sector.TimeS)!.Value;
        var parent = sector.GetBody(point.Parent);
        var away = at - sector.GetPosition(parent.Id);
        var up = away * (1 / away.Length);
        sector.RestoreLanding(ship.Id, BodyId.None, Vector2D.Zero);
        ship.Position = ship.PreviousPosition = at + up * offM;
        ship.Velocity = point.Orbit is { } orbit ? sector.RailVelocityAt(orbit, sector.TimeS) : sector.GetVelocityAt(parent.Id, sector.TimeS);
        if (sector.KindOf(point)!.Type != PointType.Ruin) return;
        ship.HeadingRad = ship.PreviousHeadingRad = Math.Atan2(up.X, -up.Y);
        sector.RestoreLanding(ship.Id, parent.Id, up * (parent.RadiusM + 10));
    }

    private static void Step(SectorState sector, int ticks) { for (var i = 0; i < ticks; i++) sector.Step(); }

    // --- The kinds ---

    [Fact]
    public void The_games_kinds_are_usable()
    {
        Assert.Empty(Catalog.Points.Problems);
        Assert.Equal(new[] { PointType.Derelict, PointType.Ruin, PointType.Anomaly }, Catalog.Points.Kinds.Select(k => k.Type).Order());
        Assert.Equal(5, Catalog.Points.Find("derelict")!.Designs.Count);
    }

    [Theory]
    [InlineData("{", "")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"other\"}", "folder")]
    [InlineData("{\"schemaVersion\":9,\"id\":\"x\"}", "schemaVersion")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"x\",\"name\":\"X\",\"names\":[\"X\"],\"type\":\"ruin\",\"detectM\":1,\"hintM\":1,\"useM\":1}", "studySeconds")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"x\",\"names\":null}", "empty")]
    public void A_kind_that_cant_be_used_is_left_out_with_the_reason(string json, string why)
    {
        var points = PointCatalog.Load(new[] { new PointSource("x", json, Array.Empty<string>()) }, Catalog);
        Assert.Empty(points.Kinds);
        Assert.Contains(why, Assert.Single(points.Problems));
    }

    // --- Options and placing ---

    [Fact]
    public void The_same_seed_and_options_give_the_same_world_and_off_changes_nothing()
    {
        Assert.Equal(Json(Game()), Json(Game()));
        var off = Game(WorldAmount.Off);
        Assert.Empty(off.Sector.Points);
        Assert.Equal(Json(Create(5, null)), Json(off));
        var often = Game(); // nothing but the points themselves differs
        Assert.Equal(off.Sector.Bodies.Select(b => (b.Name, b.MassKg)), often.Sector.Bodies.Select(b => (b.Name, b.MassKg)));
        Assert.Equal(off.Sector.ComputeMassLedger(), often.Sector.ComputeMassLedger());
    }

    [Fact]
    public void More_has_what_fewer_has_and_some_besides()
    {
        long rare = 0, normal = 0, often = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var (r, n, o) = (Game(WorldAmount.Rare, seed).Sector, Game(WorldAmount.Normal, seed).Sector, Game(WorldAmount.Often, seed).Sector);
            Assert.All(n.Points, p => Assert.Contains(o.Points, q => q.KindId == p.KindId && q.Parent == p.Parent));
            (rare, normal, often) = (rare + r.Points.Count, normal + n.Points.Count, often + o.Points.Count);
        }
        Assert.True(rare < normal && normal < often, $"{rare} {normal} {often}");
        Assert.InRange(normal / 30.0, 3.5, 6.5);
    }

    [Fact]
    public void Options_and_hidden_points_are_saved_and_older_games_have_none()
    {
        var game = Game(WorldAmount.Rare, 3);
        game.Sector.Options[WorldOptions.PointsOfInterest] = WorldAmount.Often;
        Assert.Equal(WorldAmount.Often, Again(game.Sector, game.PlayerCraftId).Options[WorldOptions.PointsOfInterest]);
        SavesExactly(game.Sector, game.PlayerCraftId);
        var plain = TestData.NewGame();
        var old = Again(plain.Sector, plain.PlayerCraftId);
        Assert.Equal(WorldAmount.Off, old.Options[WorldOptions.PointsOfInterest]);
        Assert.Empty(old.Points);
    }

    [Fact]
    public void The_summary_says_whats_changed()
    {
        var options = WorldOptions.Usual();
        Assert.True(options.IsUsual);
        options[WorldOptions.PointsOfInterest] = WorldAmount.Rare;
        Assert.Equal("Points of interest: rare", options.Summary());
        Assert.False(options.IsUsual);
    }

    [Fact]
    public void Finding_first_doesnt_change_systems_opened_later()
    {
        var (a, _, _) = Game();
        var (b, pilot, _) = Game();
        foreach (var point in b.Points.ToList()) { Go(b, b.GetCraft(pilot), point, 2_000); Step(b, 6); }
        Assert.Contains(b.Bodies, body => body.Id.Value >= SectorState.PointBodyIdBase);
        Assert.Equal(Open(a), Open(b));
        Assert.Contains(a.Points, p => a.StarOf(p.Parent)!.Id == a.Stars[1].Id);

        static string Open(SectorState s)
        {
            SectorGenerator.GenerateSystem(s, 1);
            s.PlacePoints(s.Stars[1], 1);
            return string.Join("|", s.Bodies.Where(x => x.Id.Value < SectorState.PointBodyIdBase).Select(x => $"{x.Id} {x.Name} {string.Join(",", x.Sites.Select(t => string.Join("/", t.DepositsKg)))}"))
                   + string.Join("|", s.Points.Select(p => $"{p.Id} {p.Name} {p.Parent} {p.Orbit} {p.Roll}"));
        }
    }

    // --- Finding ---

    [Fact]
    public void Hints_hold_what_they_hint_at_and_go_when_its_found()
    {
        var (sector, player, _) = Game();
        foreach (var point in sector.Points)
        {
            var hint = sector.PointHint(point, 0)!.Value;
            Assert.True(hint.Centre.DistanceTo(sector.PointPosition(point, 0)!.Value) < hint.RadiusM);
        }
        var found = Of(sector, PointType.Derelict);
        Go(sector, sector.GetCraft(player), found, 5_000);
        Step(sector, 6);
        Assert.True(found.Found);
        Assert.Null(sector.PointHint(found, 0));
        Assert.Contains(sector.Events, e => e.Kind == EventKind.Discovery && e.Text.Contains(found.Name) && e.Target.Craft == found.CraftId);
        Assert.Equal(found, sector.PointAt(new TargetRef(found.CraftId, BodyId.None)));
    }

    [Fact]
    public void Scouts_find_but_other_ships_dont_and_neither_decides()
    {
        var (sector, _, _) = Game();
        var design = TestData.StarterShip();
        var scout = new CraftState(sector.AllocateCraftId(), "Scout", design, design.ComputeStats(Catalog), Vector2D.Zero, 0);
        sector.AddCraft(scout);
        var point = Of(sector, PointType.Derelict);
        Go(sector, scout, point, 4_000);
        Step(sector, 10);
        Assert.False(point.Found);
        Assert.True(sector.StartJob(scout.Id, new SurveyJob()).Done);
        Step(sector, 10);
        Assert.True(point.Found);
        Assert.False(sector.ChoosePoint(scout.Id, point.Id, "claim").Done);
    }

    // --- Using ---

    [Fact]
    public void A_derelict_is_a_real_craft_and_can_be_claimed_with_the_ledger_exact()
    {
        var (sector, player, _) = Game();
        var ship = sector.GetCraft(player);
        var total = sector.ComputeMassLedger().TotalKg;
        var point = Of(sector, PointType.Derelict);
        Go(sector, ship, point, 3_000);
        Step(sector, 6);
        var derelict = sector.GetCraft(point.CraftId);
        Assert.True(derelict.IsWreck && derelict.Design.Cells.Count > 5);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        Assert.Contains("within", sector.ChoosePoint(player, point.Id, "claim").Message);
        Go(sector, ship, point, 100);
        Assert.Contains("lacks", sector.ChoosePoint(player, point.Id, "claim").Message);
        ship.Inventory.Add("metal", 400);
        ship.Inventory.Add("electronics", 40);
        total += 440;
        var claimed = sector.ChoosePoint(player, point.Id, "claim");
        Assert.True(claimed.Done, claimed.Message);
        Assert.True(!derelict.IsWreck && SectorState.IsPlayers(derelict) && point.Done);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        var again = Again(sector, player);
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(again, player));
        Step(sector, 60);
        Step(again, 60);
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(again, player));
    }

    [Fact]
    public void Every_derelict_design_saves_and_flies_again_when_claimed()
    {
        var seen = new HashSet<string>();
        for (ulong seed = 1; seed < 40 && seen.Count < 5; seed++)
        {
            var (sector, player, _) = Game(seed: seed);
            var ship = sector.GetCraft(player);
            foreach (var point in All(sector, PointType.Derelict))
            {
                Go(sector, ship, point, 3_000);
                Step(sector, 6);
                Assert.True(point.Found);
                seen.Add(sector.GetCraft(point.CraftId).Design.Id);
                SavesExactly(sector, player);
                Go(sector, ship, point, 100);
                var kg = sector.KindOf(point)!.ClaimKg;
                foreach (var (material, amount) in kg) ship.Inventory.Add(material, amount);
                Assert.True(sector.ChoosePoint(player, point.Id, "claim").Done);
                SavesExactly(sector, player);
            }
        }
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void Salvagers_leave_a_derelict_until_told()
    {
        var (sector, player, yard) = Game();
        var design = new ShipDesign("salvager", "Salvager", TestData.StarterShip().Cells.Select(c => c.PartId == "drill" ? c with { PartId = "salvage_arm" } : c));
        var salvager = new CraftState(sector.AllocateCraftId(), "Salvager", design, design.ComputeStats(Catalog), sector.GetCraft(yard).Position, 0) { Velocity = sector.GetCraft(yard).Velocity };
        sector.AddCraft(salvager);
        var point = Of(sector, PointType.Derelict);
        Go(sector, sector.GetCraft(player), point, 3_000);
        Step(sector, 6);
        Go(sector, salvager, point, 600);
        var job = new SalvageJob(yard);
        Assert.True(sector.StartJob(salvager.Id, job).Done);
        Step(sector, 40);
        Assert.True(job.WreckId.IsNone);
        Go(sector, sector.GetCraft(player), point, 100);
        Assert.True(sector.ChoosePoint(player, point.Id, "salvage").Done);
        Assert.Empty(sector.PointChoices(sector.GetCraft(player), point));
        Go(sector, salvager, point, 600);
        Step(sector, 1300); // it looks again once a minute
        Assert.True(point.CraftId == job.WreckId, $"{job.State} {job.WaitReason}");
    }

    [Theory]
    [InlineData("research")]
    [InlineData("tech")]
    public void A_ruin_is_studied_landed_and_gives_one_thing(string choice)
    {
        var (sector, player, _) = Game();
        var ship = sector.GetCraft(player);
        var point = Of(sector, PointType.Ruin);
        Go(sector, ship, point, 0);
        Assert.Empty(sector.PointChoices(ship, point));
        Step(sector, 10);
        Assert.True(point.Found && !point.Studied && point.StudyTicks > 0, sector.PointBlocker(ship, point));
        SavesExactly(sector, player); // part way
        Step(sector, 20 * FixedStepClock.TicksPerSecond + 10);
        Assert.True(point.Studied);
        var (before, techs) = (sector.ResearchPoints, sector.UnlockedTech.Count);
        Assert.True(sector.ChoosePoint(player, point.Id, choice).Done);
        Assert.Equal(choice == "research", sector.ResearchPoints > before);
        Assert.Equal(choice == "tech" ? techs + 1 : techs, sector.UnlockedTech.Count);
        Assert.False(sector.ChoosePoint(player, point.Id, choice).Done); // once
    }

    [Fact]
    public void Anomalies_are_scanned_and_are_ore_energy_or_research()
    {
        var seen = new HashSet<AnomalyFind>();
        for (ulong seed = 1; seed < 30 && seen.Count < 3; seed++)
        {
            var (sector, player, _) = Game(seed: seed);
            var ship = sector.GetCraft(player);
            foreach (var point in All(sector, PointType.Anomaly))
            {
                var total = sector.ComputeMassLedger().TotalKg;
                Go(sector, ship, point, 5_000);
                Step(sector, 6);
                var body = sector.GetBody(point.BodyId);
                Assert.True(body.Id.Value >= SectorState.PointBodyIdBase && !body.Surveyed);
                Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
                Assert.Null(sector.FoundIn(point));
                for (var i = 0; i < 16 * FixedStepClock.TicksPerSecond && !point.Studied; i++)
                {
                    if (i % 20 == 0) Go(sector, ship, point, 800);
                    sector.Step();
                }
                Assert.True(point.Studied && body.Surveyed);
                var find = sector.FoundIn(point)!;
                seen.Add(find.Find);
                if (find.Find == AnomalyFind.Ore) { Assert.True(point.Done); continue; }
                if (find.Find == AnomalyFind.Energy)
                {
                    ship.StoredEnergyKJ = 0;
                    Go(sector, ship, point, 800);
                    Step(sector, 20);
                    Assert.True(ship.StoredEnergyKJ > 300);
                    Go(sector, ship, point, 800);
                    Assert.True(sector.ChoosePoint(player, point.Id, "keep").Done);
                    Assert.Contains(Again(sector, player).EnergyFields(), f => f.Point.Id == point.Id);
                    continue;
                }
                Go(sector, ship, point, 800);
                Assert.True(sector.ChoosePoint(player, point.Id, "research").Done);
            }
            SavesExactly(sector, player);
        }
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void One_whose_kind_is_missing_stays_quietly()
    {
        var (sector, player, _) = Game();
        var bare = TestData.Catalog().WithPoints(PointCatalog.Empty);
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, player), bare).Sector;
        Step(loaded, 20);
        Assert.Equal(sector.Points.Count, loaded.Points.Count);
    }
}
