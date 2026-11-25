using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Contracts (#124).</summary>
public sealed class ContractTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    /// <summary>A second system with a friendly-enough civilization, and the player's ship docked at its trade station.</summary>
    private static (SectorState Sector, Faction Faction, CraftState Station, CraftState Ship) Game(WorldAmount amount = WorldAmount.Off, double baseline = 20)
    {
        var sector = TestData.NewGame().Sector;
        sector.Options[WorldOptions.Contracts] = amount;
        var (star, _) = SectorGenerator.GenerateSystem(sector, 1);
        var faction = sector.MeetCivilization(star, cultureId: "pacifist", baseline: baseline);
        Run(sector, 2 * Second);
        var station = sector.TradeStationOf(faction)!;
        var ship = sector.GetCraft(sector.PilotCraftId);
        ship.Position = ship.PreviousPosition = sector.BerthPosition(station, 0, ship.CollisionRadiusM, station.Position);
        ship.Velocity = station.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        return (sector, faction, station, ship);
    }

    private static void Run(SectorState sector, int ticks, Func<bool>? until = null)
    {
        for (var i = 0; i < ticks && until?.Invoke() != true; i++) sector.Step();
    }

    private static string Json(SectorState sector) => SaveGameSerializer.ToJson(sector, sector.PilotCraftId);

    private static SectorState Again(SectorState sector) => SaveGameSerializer.FromJson(Json(sector), Catalog).Sector;

    // --- Offers ---

    [Fact]
    public void Civilizations_offer_work_as_often_as_the_option_says_and_offers_expire_for_free()
    {
        var counts = new List<int>();
        foreach (var amount in new[] { WorldAmount.Off, WorldAmount.Rare, WorldAmount.Often })
        {
            var (sector, faction, _, _) = Game(amount);
            var before = (faction.Goodwill, faction.Grievance);
            Run(sector, 3 * 60 * Minute);
            counts.Add(sector.CivilizationContracts.Count);
            Assert.True(sector.CivilizationContracts.Count(c => c.State == ContractState.Offered) <= SectorState.OffersPerCivilization);
            Assert.All(sector.CivilizationContracts.Where(c => !c.IsOpen), c => Assert.Equal(ContractState.Expired, c.State));
            Assert.True(faction.Grievance <= before.Grievance);
        }
        Assert.Equal(0, counts[0]);
        Assert.True(counts[1] < counts[2] && counts[2] >= 5, string.Join(" ", counts));
    }

    [Fact]
    public void Offers_are_the_same_for_the_same_seed_and_saved()
    {
        string Run3()
        {
            var (sector, _, _, _) = Game(WorldAmount.Often);
            Run(sector, 90 * Minute);
            Assert.NotEmpty(sector.CivilizationContracts);
            Assert.Equal(Json(sector), Json(Again(sector)));
            return string.Join("|", sector.CivilizationContracts.Select(c => $"{c.Kind} {sector.Describe(c)} {sector.RewardText(c)}"));
        }
        Assert.Equal(Run3(), Run3());
    }

    [Fact]
    public void Hostile_ones_offer_nothing_and_break_off_what_was_taken_without_cost()
    {
        var (sector, faction, _, _) = Game();
        var contract = sector.DebugOffer(faction, ContractKind.Delivery)!;
        Assert.True(sector.AcceptContract(contract.Id).Done);
        faction.Attitude = -90;
        faction.Grievance = 0;
        Run(sector, 2 * Second);
        Assert.Equal(ContractState.Withdrawn, contract.State);
        Assert.Equal(0, faction.Grievance);
    }

    // --- Deliveries ---

    [Fact]
    public void A_delivery_counts_real_goods_and_pays_with_the_ledger_exact()
    {
        var (sector, faction, station, ship) = Game();
        var contract = sector.DebugOffer(faction, ContractKind.Delivery)!;
        Assert.Equal(ContractState.Offered, contract.State);
        Assert.Contains("only", sector.HandOver(ship.Id, contract.Id).Message + "only"); // not taken yet
        Assert.True(sector.AcceptContract(contract.Id).Done);
        var total = sector.ComputeMassLedger().TotalKg;
        var (goodwill, research) = (faction.Goodwill, sector.ResearchPoints);
        ship.Inventory.Add(contract.Material, contract.Kg / 2);
        total += contract.Kg / 2;
        Assert.True(sector.HandOver(ship.Id, contract.Id).Done);
        Assert.Equal(contract.Kg / 2, contract.DoneKg);
        Assert.Equal(ContractState.Accepted, contract.State);
        Assert.Equal(Json(sector), Json(Again(sector)));
        ship.Inventory.Add(contract.Material, contract.Kg);
        total += contract.Kg;
        Assert.True(sector.HandOver(ship.Id, contract.Id).Done);
        Assert.Equal(ContractState.Done, contract.State);
        Assert.Equal(contract.Kg - contract.Kg / 2, ship.Inventory[contract.Material]); // only what it wanted
        Assert.True(faction.Goodwill > goodwill || sector.ResearchPoints > research || contract.RewardKg > 0 || faction.MiningAccessUntilTick > sector.Tick);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg); // rewards come from outside: counted as discovered
    }

    [Fact]
    public void A_freight_job_can_make_the_delivery_and_stops_when_it_is_done()
    {
        var (sector, faction, station, ship) = Game();
        sector.TryUndock(ship.Id);
        var contract = sector.DebugOffer(faction, ContractKind.Delivery)!;
        var design = TestData.StarterShip();
        // A depot of yours beside their station, with the goods.
        var depot = new CraftState(sector.AllocateCraftId(), "Depot", TestData.StarterShipyard(), TestData.StarterShipyard().ComputeStats(Catalog), station.Position + new Vector2D(0, -2_000), 0);
        sector.AddCraft(depot);
        sector.PutOnRails(depot.Id, station.RailOrbit! with { RadiusM = station.RailOrbit!.RadiusM + 1_500 });
        depot.Inventory.Add(contract.Material, contract.Kg * 2);
        var freighter = new CraftState(sector.AllocateCraftId(), "Freighter", design, design.ComputeStats(Catalog), depot.Position + new Vector2D(0, -300), 0) { Velocity = depot.Velocity };
        sector.AddCraft(freighter);
        freighter.Inventory.Add("fuel", 100);
        var refused = sector.StartJob(freighter.Id, new HaulJob(depot.Id, contract.Material, station.Id, 100, 0));
        Assert.False(refused.Done); // no contract taken yet
        Assert.True(sector.AcceptContract(contract.Id).Done);
        var started = sector.StartJob(freighter.Id, new HaulJob(depot.Id, contract.Material, station.Id, 100, 0));
        Assert.True(started.Done, started.Message);
        Run(sector, 60 * Minute, () => contract.State == ContractState.Done);
        Assert.True(contract.State == ContractState.Done, $"{freighter.Job!.Status} {contract.DoneKg}");
        Assert.Equal(contract.Kg, contract.DoneKg);
        Run(sector, 5 * Second);
        Assert.False(freighter.Job!.Active);
    }

    [Fact]
    public void Missing_the_deadline_or_giving_up_costs_goodwill()
    {
        var (sector, faction, _, ship) = Game();
        faction.Goodwill = 10;
        var late = sector.DebugOffer(faction, ContractKind.Delivery)!;
        Assert.True(sector.AcceptContract(late.Id).Done);
        var json = Json(sector).Replace($"\"deadlineTick\": {late.DeadlineTick}", $"\"deadlineTick\": {sector.Tick + 5}");
        var later = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Run(later, 2 * Second);
        Assert.Equal(ContractState.Failed, later.ContractById(late.Id)!.State);
        Assert.Equal(10 - SectorState.FailGoodwill, later.FactionById(faction.Id)!.Goodwill, 1);
        Assert.True(sector.GiveUpContract(late.Id).Done);
        Assert.Equal(ContractState.Failed, late.State);
        faction.Goodwill = 0;
        var again = sector.DebugOffer(faction, ContractKind.Delivery)!;
        sector.AcceptContract(again.Id);
        var grievance = faction.Grievance;
        sector.GiveUpContract(again.Id);
        Assert.Equal(grievance + SectorState.FailGoodwill, faction.Grievance, 3); // without goodwill to lose, it's a grievance
    }

    // --- Surveys ---

    [Fact]
    public void A_survey_is_done_however_the_bodies_get_surveyed()
    {
        var (sector, faction, _, _) = Game();
        var contract = sector.DebugOffer(faction, ContractKind.Survey)!;
        Assert.NotEmpty(contract.Bodies);
        Assert.All(contract.Bodies, b => Assert.Equal(faction.HomeStarId, sector.StarOf(b)!.Id));
        Assert.True(sector.AcceptContract(contract.Id).Done);
        foreach (var body in contract.Bodies.Skip(1)) sector.MarkSurveyed(body);
        Run(sector, 2 * Second);
        Assert.Equal(ContractState.Accepted, contract.State);
        sector.MarkSurveyed(contract.Bodies[0]);
        Run(sector, 2 * Second);
        Assert.Equal(ContractState.Done, contract.State);
    }

    // --- Rescues ---

    [Fact]
    public void A_rescue_is_a_real_ship_that_goes_home_with_the_fuel()
    {
        var (sector, faction, _, ship) = Game();
        var contract = sector.DebugOffer(faction, ContractKind.Rescue)!;
        var total = sector.ComputeMassLedger().TotalKg;
        Assert.True(sector.AcceptContract(contract.Id).Done);
        var stranded = sector.GetCraft(contract.Stranded);
        Assert.True(stranded.IsStranded && !sector.IsEnemy(stranded) && stranded.FactionId == faction.Id);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        Run(sector, 30 * Second);
        Assert.True(sector.Crafts.Contains(stranded)); // not taken for a raider and sent home
        Assert.Equal(Json(sector), Json(Again(sector)));
        sector.TryUndock(ship.Id);
        Assert.Contains("within", sector.HandOver(ship.Id, contract.Id).Message);
        ship.Position = ship.PreviousPosition = stranded.Position + new Vector2D(0, -40);
        ship.Velocity = stranded.Velocity;
        Assert.Contains("need", sector.HandOver(ship.Id, contract.Id).Message);
        ship.Inventory.Add("fuel", SectorState.RescueFuelKg);
        total += SectorState.RescueFuelKg;
        var done = sector.HandOver(ship.Id, contract.Id);
        Assert.True(done.Done, done.Message);
        Assert.Equal(ContractState.Done, contract.State);
        Assert.DoesNotContain(stranded, sector.Crafts);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg); // the fuel left with it, as discovered mass going out
    }

    [Fact]
    public void A_lost_rescue_ship_fails_the_contract()
    {
        var (sector, faction, _, _) = Game();
        var contract = sector.DebugOffer(faction, ContractKind.Rescue)!;
        sector.AcceptContract(contract.Id);
        sector.DebugHit(contract.Stranded, 0, 100);
        var json = Json(sector);
        Assert.Contains("isStranded", json);
        // Gone altogether (salvaged, say): failed, once.
        sector.RemoveCraftForTest(contract.Stranded);
        Run(sector, 2 * Second);
        Assert.Equal(ContractState.Failed, contract.State);
    }

    // --- Rewards ---

    [Fact]
    public void Access_lets_you_mine_their_system_without_offence()
    {
        var (sector, faction, _, _) = Game();
        var rock = sector.Bodies.First(b => b.IsMineable && sector.StarOf(b.Id)?.Id == faction.HomeStarId);
        sector.NoteMiningForTest(rock, 10_000);
        var grievance = faction.Grievance;
        Assert.True(grievance > 0);
        faction.MiningAccessUntilTick = sector.Tick + Minute;
        sector.NoteMiningForTest(rock, 10_000);
        Assert.Equal(grievance, faction.Grievance);
        Assert.Equal(Json(sector), Json(Again(sector)));
    }

    [Fact]
    public void Every_kind_of_reward_turns_up()
    {
        var (sector, faction, _, _) = Game();
        var seen = new HashSet<string>();
        for (var i = 0; i < 80 && seen.Count < 4; i++)
        {
            sector.Step();
            if (sector.DebugOffer(faction, (ContractKind)(i % 3)) is not { } c) continue;
            if (c.RewardKg > 0) seen.Add("goods");
            if (c.Goodwill > 0) seen.Add("goodwill");
            if (c.Research > 0) seen.Add("research");
            if (c.AccessHours > 0) seen.Add("access");
            sector.RefuseContract(c.Id);
        }
        Assert.Equal(4, seen.Count);
    }
}
