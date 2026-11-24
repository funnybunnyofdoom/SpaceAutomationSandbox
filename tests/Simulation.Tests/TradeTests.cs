using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Trade and diplomacy with civilizations (#89).</summary>
public sealed class TradeTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    /// <summary>A second system with a civilization, and a ship of the player's docked at its trade station.</summary>
    private static (SectorState Sector, Faction Faction, CraftState Station, CraftState Ship) Game(double baseline = 0, bool dock = true)
    {
        var sector = TestData.NewGame().Sector;
        var (star, _) = SectorGenerator.GenerateSystem(sector, 1);
        var faction = sector.MeetCivilization(star, cultureId: "pacifist", baseline: baseline);
        Run(sector, 2 * Second);
        var station = sector.TradeStationOf(faction)!;
        Assert.NotNull(station);
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), "Trader", design, design.ComputeStats(Catalog), station.Position, 0);
        sector.AddCraft(ship);
        ship.Position = sector.BerthPosition(station, 0, ship.CollisionRadiusM, station.Position);
        ship.Velocity = station.Velocity;
        if (dock) { var done = sector.TryDock(ship.Id); Assert.True(done.Done, done.Message); }
        sector.PilotCraftId = CraftId.None;
        return (sector, faction, station, ship);
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    private static string Wanted(SectorState sector, Faction faction) =>
        Catalog.Materials.Materials.Select(m => m.Id).First(m => sector.ValueTo(faction, m) > 2 && !sector.SellsOf(faction).Contains(m));

    [Fact]
    public void Trade_stations_stand_in_their_own_system_stocked_with_what_they_sell()
    {
        var (sector, faction, station, _) = Game();
        Assert.Empty(SectorState.TradeStationDesign.Validate(Catalog, DesignRole.Station));
        Assert.Equal(faction.HomeStarId, sector.StarAt(station.Position)!.Id);
        Assert.Equal(SectorState.TradeGoods, sector.SellsOf(faction).Count);
        Assert.All(sector.SellsOf(faction), m => Assert.Equal(SectorState.TradeStockKg, station.Inventory[m]));
        Run(sector, 20 * Minute);
        Assert.Empty(station.PartHealth);
        Assert.Single(sector.Crafts, c => c.IsTradeStation);
    }

    [Fact]
    public void Barter_swaps_goods_at_their_prices_with_exact_mass()
    {
        var (sector, faction, station, ship) = Game();
        var give = Wanted(sector, faction);
        var get = sector.SellsOf(faction)[0];
        sector.DebugAddStock(ship.Id, give, 10_000);
        var mass = sector.ComputeMassLedger().TotalKg;
        var quote = sector.BarterQuote(faction, station, give, 10_000, get);
        Assert.True(quote > 0);
        var deal = sector.Barter(ship.Id, give, 10_000, get);
        Assert.True(deal.Done, deal.Message);
        Assert.Equal(quote, ship.Inventory[get]);
        Assert.InRange(ship.Inventory[give], 0, 100); // rounded in their favour by less than one kg of what they gave
        Assert.True(faction.Goodwill > 0);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        // Swapping straight back loses the margin twice.
        if (sector.ValueTo(faction, get) > 0) Assert.Equal(0, sector.BarterQuote(faction, station, get, quote, give)); // they don't sell what they just bought
    }

    [Fact]
    public void Prices_fall_as_they_fill_up_and_friends_pay_more()
    {
        var (sector, faction, station, _) = Game();
        var give = Wanted(sector, faction);
        var before = sector.BuyPrice(faction, station, give);
        station.Inventory.Add(give, 30_000);
        Assert.True(sector.BuyPrice(faction, station, give) < before);
        station.Inventory.TryRemove(new Dictionary<string, long> { [give] = 30_000 });
        var (friendly, friend, theirs, _) = Game(60);
        Assert.Equal(sector.ValueTo(faction, give), friendly.ValueTo(friend, give));
        Assert.True(friendly.BuyPrice(friend, theirs, give) > before);
    }

    [Fact]
    public void Stock_runs_out_and_comes_back_and_the_ledger_balances()
    {
        var (sector, faction, station, ship) = Game();
        var get = sector.SellsOf(faction).OrderBy(m => sector.SellPrice(faction, m)).First();
        var give = Wanted(sector, faction);
        ship.LegacyUnlimitedStorage = true;
        sector.DebugAddStock(ship.Id, give, 200_000);
        var mass = sector.ComputeMassLedger().TotalKg;
        for (var i = 0; i < 20 && station.Inventory[get] > 0; i++) sector.Barter(ship.Id, give, 10_000, get);
        var low = station.Inventory[get];
        Run(sector, 5 * Minute);
        Assert.True(station.Inventory[get] > low);
        var held = station.Inventory[give];
        Run(sector, 20 * Minute);
        Assert.True(station.Inventory[give] < held); // they use what they bought
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(-80)]
    public void Wary_and_hostile_ones_dont_trade_and_hostile_ones_close_their_ports(double baseline)
    {
        var (sector, faction, station, ship) = Game(baseline, dock: false);
        var dock = sector.TryDock(ship.Id);
        Assert.Equal(baseline > -30, dock.Done);
        sector.DebugAddStock(ship.Id, "metal", 1_000);
        Assert.False(sector.Barter(ship.Id, "metal", 1_000, sector.SellsOf(faction)[0]).Done);
    }

    [Fact]
    public void Their_stock_cant_be_taken_for_free_or_used_by_jobs()
    {
        var (sector, faction, station, ship) = Game();
        var good = sector.SellsOf(faction)[0];
        Assert.Contains("trade instead", sector.TryTransfer(station.Id, ship.Id, good, 100).Message);
        Assert.Equal(0, sector.MaxTransferKg(station.Id, ship.Id, good));
        var yard = sector.Crafts.First(c => c.IsShipyard);
        Assert.Contains("your own stations", sector.StartJob(ship.Id, new HaulJob(station.Id, good, yard.Id, 1_000, 0)).Message);
        Assert.False(sector.SetRequest(station.Id, "metal", 1_000).Done);
        // Nor its power: each draws on its own pool.
        Run(sector, 2);
        Assert.NotSame(sector.PowerRootOf(station), sector.PowerRootOf(ship));
    }

    [Fact]
    public void Gifts_raise_goodwill_and_clear_grievances_even_when_wary()
    {
        var (sector, faction, _, ship) = Game(-10);
        faction.Grievance = 10;
        sector.DebugAddStock(ship.Id, "electronics", 10_000);
        var mass = sector.ComputeMassLedger().TotalKg;
        var target = faction.TargetAttitude(sector.ActiveLaws);
        var gift = sector.Gift(ship.Id, "electronics", 10_000);
        Assert.True(gift.Done, gift.Message);
        Assert.True(faction.Grievance < 10);
        Assert.True(faction.TargetAttitude(sector.ActiveLaws) > target + 5);
        Assert.InRange(faction.Goodwill, 0.1, Faction.MaxGoodwill);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        Run(sector, 10 * Minute);
        Assert.True(faction.Disposition >= Disposition.Neutral);
    }

    [Fact]
    public void Peace_ends_a_war_for_a_price_and_holds_for_a_while()
    {
        var (sector, faction, _, ship) = Game(20);
        sector.DeclareAttack(faction.Id);
        Run(sector, Second * 2);
        Assert.False(ship.IsDocked); // they closed their ports
        var far = sector.OfferPeace(sector.Crafts.First(c => c.IsShipyard).Id, faction.Id, "electronics");
        Assert.Contains("Bring a ship", far.Message);
        Assert.Contains("They want", sector.OfferPeace(ship.Id, faction.Id, "electronics").Message);
        sector.DebugAddStock(ship.Id, "electronics", 38_000);
        var mass = sector.ComputeMassLedger().TotalKg;
        var peace = sector.OfferPeace(ship.Id, faction.Id, "electronics");
        Assert.True(peace.Done, peace.Message);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
        Assert.NotEqual(Disposition.Hostile, faction.Disposition);
        Run(sector, 30 * Minute);
        Assert.NotEqual(Disposition.Hostile, faction.Disposition);
        Assert.True(faction.Attitude > 0); // their own opinion is +20
    }

    [Fact]
    public void Peace_with_a_civilization_that_hates_your_laws_is_only_a_truce()
    {
        var (sector, faction, _, ship) = Game(-80, dock: false);
        sector.DebugAddStock(ship.Id, "electronics", 38_000);
        var peace = sector.OfferPeace(ship.Id, faction.Id, "electronics");
        Assert.True(peace.Done, peace.Message);
        Run(sector, 30 * Minute);
        Assert.Equal(Disposition.Wary, faction.Disposition);
        Run(sector, 60 * Minute, () => faction.Disposition == Disposition.Hostile);
        Assert.Equal(Disposition.Hostile, faction.Disposition);
    }

    [Fact]
    public void A_trade_route_runs_on_its_own()
    {
        var (sector, faction, station, ship) = Game();
        // A depot of the player's near their station, on the same orbit.
        var design = new ShipDesign("depot", "Depot", new[] { new DesignCell(new GridPos(0, 0), "storage_silo"), new DesignCell(new GridPos(0, -1), "docking_port"), new DesignCell(new GridPos(1, 0), "solar_panel") });
        var depot = new CraftState(sector.AllocateCraftId(), "Depot", design, design.ComputeStats(Catalog), station.Position, 0);
        sector.AddCraft(depot);
        sector.PutOnRails(depot.Id, station.RailOrbit! with { PhaseAtZeroRad = station.RailOrbit!.PhaseAtZeroRad + 0.25 });
        var give = Wanted(sector, faction);
        var get = sector.SellsOf(faction)[0];
        sector.DebugAddStock(depot.Id, give, 60_000);
        var mass = sector.ComputeMassLedger().TotalKg;
        var started = sector.StartJob(ship.Id, new TradeJob(depot.Id, give, station.Id, get));
        Assert.True(started.Done, started.Message);
        Run(sector, 60 * Minute, () => ship.Job!.CompletedCycles >= 1);
        Assert.True(ship.Job!.CompletedCycles >= 1, $"{ship.Job.Status} {ship.Position.DistanceTo(station.Position):0}");
        Assert.True(depot.Inventory[get] > 0);
        Assert.Equal(depot.Inventory[get], ship.Job.DeliveredKg);
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Trade_survives_a_save_exactly()
    {
        var (sector, faction, station, ship) = Game(20);
        sector.DebugAddStock(ship.Id, "electronics", 8_000);
        sector.Gift(ship.Id, "electronics", 3_000);
        var yard = sector.Crafts.First(c => c.IsShipyard);
        var give = sector.SellsOf(faction).Contains("electronics") ? Wanted(sector, faction) : "electronics";
        Assert.True(sector.StartJob(ship.Id, new TradeJob(yard.Id, give, station.Id, sector.SellsOf(faction)[0])).Done);
        Run(sector, Minute + 13);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        Run(sector, 5 * Minute);
        Run(loaded, 5 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }
}
