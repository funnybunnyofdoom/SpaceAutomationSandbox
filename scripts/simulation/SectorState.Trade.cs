using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Trade and diplomacy with civilizations (#89; the creator's choices). Each has a trade station in its own system, where goods are
/// <b>bartered</b> by value: every civilization values materials in its own way, takes a margin (smaller for friends), and
/// pays less for what it already has plenty of. Neutral and friendly ones trade. <b>Gifts</b> raise goodwill and clear grievances; a
/// <b>peace offer</b> pays a hostile civilization in goods to end a war. Whole kg change hands; their stock comes from and goes to
/// outside the sector, with the mass ledger kept exact.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>What a civilization keeps of each thing it sells, and how fast it restocks (share of that per minute).</summary>
    public const long TradeStockKg = 30_000;
    public const double TradeRestockPerMinute = 0.05;

    /// <summary>How fast it uses up what it has bought, share per minute.</summary>
    public const double TradeUsePerMinute = 0.02;

    /// <summary>Different materials each civilization sells.</summary>
    public const int TradeGoods = 5;

    public const double NeutralMargin = 0.25, FriendlyMargin = 0.1;

    /// <summary>Value that earns a point of goodwill, by trading and by gift, and that clears a point of grievance.</summary>
    public const double TradeValuePerGoodwill = 20_000, GiftValuePerGoodwill = 4_000, GiftValuePerGrievance = 2_000;

    public const double TruceS = 3_600, TruceAttitude = -25;

    public static ShipDesign TradeStationDesign { get; } = new("trade_station", "Trade station", new[]
    {
        new DesignCell(new GridPos(0, 0), "hull"), new DesignCell(new GridPos(0, -1), "docking_port"), new DesignCell(new GridPos(0, 1), "docking_port", 2),
        new DesignCell(new GridPos(-1, 0), "storage_silo"), new DesignCell(new GridPos(1, 0), "storage_silo"),
        new DesignCell(new GridPos(-2, 0), "docking_port", 3), new DesignCell(new GridPos(2, 0), "docking_port", 1),
        new DesignCell(new GridPos(-1, 1), "solar_panel"), new DesignCell(new GridPos(1, 1), "solar_panel"),
    });

    public CraftState? TradeStationOf(Faction faction)
    {
        foreach (var craft in _crafts) if (craft.IsTradeStation && craft.FactionId == faction.Id && !craft.IsWreck) return craft;
        return null;
    }

    /// <summary>The civilization whose trade station a craft is docked at, or null.</summary>
    public Faction? DockedTrader(CraftState craft) =>
        craft.IsDocked && _craftsById.TryGetValue(craft.DockedToCraftId, out var station) && station.IsTradeStation ? FactionById(station.FactionId) : null;

    public static bool WillTrade(Faction faction) => faction.Disposition is Disposition.Neutral or Disposition.Friendly;

    // --- Prices ---

    /// <summary>What a kg of a material is worth to a civilization, before margins; 0 if nobody wants it.</summary>
    public double ValueTo(Faction faction, string material)
    {
        if (Parts is null || !Parts.Materials.Contains(material)) return 0;
        var taste = new DeterministicRandom(((ulong)faction.Id * 0x9E3779B97F4A7C15UL) ^ NewGame.SeedFromText(material) ^ Seed).NextDouble();
        return Parts.Materials.Get(material).Value * (0.6 + 0.9 * taste);
    }

    /// <summary>The materials a civilization sells (fixed by its id): not worthless things, never what only weapons use.</summary>
    public IReadOnlyList<string> SellsOf(Faction faction)
    {
        if (Parts is null) return Array.Empty<string>();
        var rng = new DeterministicRandom(Seed ^ ((ulong)faction.Id * 0xA24BAED4963EE407UL));
        return Parts.Materials.Materials.Where(m => m.Value >= 1 && m.Id is not (ShellMaterial or MissileMaterial))
            .Select(m => (m.Id, Roll: rng.NextUInt64())).OrderBy(x => x.Roll).Take(TradeGoods).Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static double Margin(Faction faction) => faction.Disposition == Disposition.Friendly ? FriendlyMargin : NeutralMargin;

    /// <summary>Value per kg they give for a material: less the more of it they already hold.</summary>
    public double BuyPrice(Faction faction, CraftState station, string material) =>
        ValueTo(faction, material) * (1 - Margin(faction)) * Math.Max(0.3, 1 - station.Inventory[material] / (2.0 * TradeStockKg));

    /// <summary>Value per kg they ask for a material.</summary>
    public double SellPrice(Faction faction, string material) => ValueTo(faction, material) * (1 + Margin(faction));

    /// <summary>How much of one material they give for so much of another, now (whole kg, limited by their stock).</summary>
    public long BarterQuote(Faction faction, CraftState station, string give, long giveKg, string get)
    {
        var ask = SellPrice(faction, get);
        if (ask <= 0 || give == get) return 0;
        return Math.Min((long)Math.Floor(giveKg * BuyPrice(faction, station, give) / ask), SellsOf(faction).Contains(get) ? station.Inventory[get] : 0);
    }

    // --- Dealing ---

    /// <summary>Swaps goods at the trade station the ship is docked at; all or nothing.</summary>
    public ActionResult Barter(CraftId shipId, string give, long giveKg, string get)
    {
        var ship = GetCraft(shipId);
        if (DockedTrader(ship) is not { } faction) return new(false, $"{ship.Name} must be docked at a civilization's trade station.");
        if (!WillTrade(faction)) return new(false, $"The {faction.Name} won't trade with you ({faction.Disposition.ToString().ToLowerInvariant()}).");
        var station = _craftsById[ship.DockedToCraftId];
        if (giveKg <= 0 || ship.Inventory[give] < giveKg) return new(false, $"{ship.Name} has only {ship.Inventory[give]} kg of {give}.");
        if (ValueTo(faction, give) <= 0) return new(false, $"The {faction.Name} have no use for {give}.");
        if (!SellsOf(faction).Contains(get)) return new(false, $"The {faction.Name} don't sell {get}.");
        var getKg = BarterQuote(faction, station, give, giveKg, get);
        if (getKg <= 0) return new(false, station.Inventory[get] <= 0 ? $"They have no {get} left for now." : $"That's worth less than 1 kg of {get}.");
        // Don't hand over more than what they can still give is worth.
        var fair = (long)Math.Ceiling(getKg * SellPrice(faction, get) / BuyPrice(faction, station, give));
        giveKg = Math.Min(giveKg, Math.Max(1, fair));
        if (ship.HasCargoLimit && ship.FreeCargoKg + giveKg < getKg) return new(false, $"{ship.Name} has no room for {getKg} kg.");
        if (station.FreeCargoKg + getKg < giveKg) return new(false, $"{station.Name} has no room for {giveKg} kg.");
        var worth = giveKg * ValueTo(faction, give);
        ship.Inventory.TryRemove(new Dictionary<string, long> { [give] = giveKg });
        station.Inventory.TryRemove(new Dictionary<string, long> { [get] = getKg });
        ship.Inventory.Add(get, getKg);
        station.Inventory.Add(give, giveKg);
        AddGoodwill(faction, worth / TradeValuePerGoodwill);
        return new(true, $"Traded {giveKg / 1000.0:0.0##} t of {give} for {getKg / 1000.0:0.0##} t of {get}.");
    }

    private static void AddGoodwill(Faction faction, double points) => faction.Goodwill = Math.Min(Faction.MaxGoodwill, faction.Goodwill + points);

    /// <summary>Gives goods away at the trade station the ship is docked at.</summary>
    public ActionResult Gift(CraftId shipId, string material, long kg)
    {
        var ship = GetCraft(shipId);
        if (DockedTrader(ship) is not { } faction) return new(false, $"{ship.Name} must be docked at a civilization's trade station.");
        if (kg <= 0 || ship.Inventory[material] < kg) return new(false, $"{ship.Name} has only {ship.Inventory[material]} kg of {material}.");
        var worth = kg * ValueTo(faction, material);
        if (worth <= 0) return new(false, $"The {faction.Name} have no use for {material}.");
        var station = _craftsById[ship.DockedToCraftId];
        ship.Inventory.TryRemove(new Dictionary<string, long> { [material] = kg });
        var kept = Math.Min(kg, station.FreeCargoKg); // what doesn't fit goes to their planet
        station.Inventory.Add(material, kept);
        Eject(material, kg - kept);
        AddGoodwill(faction, worth / GiftValuePerGoodwill);
        faction.Grievance = Math.Max(0, faction.Grievance - worth / GiftValuePerGrievance);
        AddEvent($"You gave the {faction.Name} {kg / 1000.0:0.0##} t of {material}.", EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
        return new(true, $"The {faction.Name} accept your gift of {kg / 1000.0:0.0##} t of {material}.");
    }

    private void Eject(string material, long kg)
    {
        if (kg > 0) _ejectedKg[material] = _ejectedKg.GetValueOrDefault(material) + kg;
    }

    /// <summary>Value a hostile civilization wants for peace: more the angrier it is.</summary>
    public static double PeacePrice(Faction faction) => 20_000 + 400 * faction.Grievance + 300 * Math.Max(0, -faction.Attitude);

    /// <summary>
    /// Pays a hostile civilization for peace, from the hold of a ship of yours in its system: grievances are dropped, raiders leave and
    /// they aren't hostile for <see cref="TruceS"/>. What they think of your laws can turn them hostile again after that.
    /// </summary>
    public ActionResult OfferPeace(CraftId shipId, int factionId, string material)
    {
        var ship = GetCraft(shipId);
        if (FactionById(factionId) is not { } faction) return new(false, "No such civilization.");
        if (faction.Disposition != Disposition.Hostile) return new(false, $"The {faction.Name} aren't hostile.");
        if (StarAt(ship.Position)?.Id != faction.HomeStarId) return new(false, $"Bring a ship with the goods to the {GetBody(faction.HomeStarId).Name} system.");
        var value = ValueTo(faction, material);
        if (value <= 0) return new(false, $"The {faction.Name} have no use for {material}.");
        var kg = (long)Math.Ceiling(PeacePrice(faction) / value);
        if (ship.Inventory[material] < kg) return new(false, $"They want {kg / 1000.0:0.0##} t of {material}; {ship.Name} has {ship.Inventory[material] / 1000.0:0.0##} t.");
        ship.Inventory.TryRemove(new Dictionary<string, long> { [material] = kg });
        Eject(material, kg); // sent down to their planet
        faction.Grievance = 0;
        faction.Attitude = Math.Max(faction.Attitude, TruceAttitude);
        faction.TruceUntilTick = Tick + (long)(TruceS * FixedStepClock.TicksPerSecond);
        faction.RaidEndTick = Math.Min(faction.RaidEndTick, Tick); // raiders withdraw
        AddGoodwill(faction, 10);
        _lastDisposition[faction.Id] = faction.Disposition;
        AddEvent($"Peace with the {faction.Name}, for {kg / 1000.0:0.0##} t of {material}.", EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
        return new(true, $"The {faction.Name} agree to peace for {kg / 1000.0:0.0##} t of {material}.");
    }

    // --- Their stations ---

    /// <summary>Once a second: builds trade stations, restocks what they sell and uses up what they bought.</summary>
    private void StepTrade()
    {
        if (Parts is null || _stars.Count == 0) return;
        foreach (var faction in _factions)
        {
            faction.Goodwill = Math.Max(0, faction.Goodwill - Faction.GoodwillFadePerMinute / 60);
            if (faction.HomeStarId == _stars[0].Id) continue;
            if (TradeStationOf(faction) is not { } station)
            {
                if (Tick < faction.NextStationTick || faction.Disposition == Disposition.Hostile && faction.NextStationTick > 0) continue;
                if (BuildTradeStation(faction) is not { } built) continue;
                station = built;
            }
            faction.NextStationTick = Tick + 30 * 60 * FixedStepClock.TicksPerSecond; // if it's lost: half an hour
            // A civilization that turns hostile closes its ports.
            if (faction.Disposition == Disposition.Hostile)
                foreach (var ship in _crafts) if (ship.DockedToCraftId == station.Id) TryUndock(ship.Id);
            if (Tick % (60 * FixedStepClock.TicksPerSecond) != 0) continue;
            var sells = SellsOf(faction);
            foreach (var material in sells) Stock(station, material, Math.Min((long)(TradeStockKg * TradeRestockPerMinute), TradeStockKg - station.Inventory[material]));
            foreach (var (material, kg) in station.Inventory.Kg.ToList())
            {
                var spare = sells.Contains(material) ? Math.Max(0, kg - TradeStockKg) : kg;
                var used = Math.Min(spare, (long)Math.Ceiling(spare * TradeUsePerMinute));
                if (used <= 0) continue;
                station.Inventory.TryRemove(new Dictionary<string, long> { [material] = used });
                Eject(material, used);
            }
        }
    }

    /// <summary>Goods from outside the sector: the mass ledger stays balanced.</summary>
    private void Stock(CraftState station, string material, long kg)
    {
        kg = Math.Min(kg, station.FreeCargoKg);
        if (kg <= 0) return;
        station.Inventory.Add(material, kg);
        DiscoveredKg += kg;
    }

    private CraftState? BuildTradeStation(Faction faction)
    {
        var stops = PatrolStops(faction.HomeStarId);
        if (stops.Count == 0) return null;
        var planet = stops[^1]; // the outermost: away from your gate
        var stats = TradeStationDesign.ComputeStats(Parts!);
        var station = new CraftState(AllocateCraftId(), $"{faction.Name} trade station", TradeStationDesign, stats, GetPosition(planet.Id), 0)
        {
            FactionId = faction.Id, IsTradeStation = true,
        };
        AddCraft(station);
        var radius = planet.RadiusM + stats.BoundingRadiusM + 2_000;
        PutOnRails(station.Id, new CircularOrbit(planet.Id, radius, CircularOrbitPeriodS(planet, radius, DeployOrbitPeriodS), faction.Id));
        DiscoveredKg += (long)Math.Round(stats.TotalMassKg);
        foreach (var material in SellsOf(faction)) Stock(station, material, TradeStockKg);
        return station;
    }

    // --- Trade routes ---

    public ActionResult StartJob(CraftId craftId, TradeJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.IsWreck || craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        if (craft.Stats.CargoCapacityKg <= 0) return new(false, $"{craft.Name} has no cargo bay.");
        if (!_craftsById.TryGetValue(job.HomeId, out var home) || home.Id == craft.Id || !IsPlayers(home) || home.IsWreck || home.Stats.DockingPorts.Count == 0)
            return new(false, "Home must be a station of yours with a docking port.");
        if (!_craftsById.TryGetValue(job.TraderId, out var trader) || !trader.IsTradeStation || FactionById(trader.FactionId) is not { } faction)
            return new(false, "Pick a civilization's trade station.");
        if (!WillTrade(faction)) return new(false, $"The {faction.Name} won't trade with you ({faction.Disposition.ToString().ToLowerInvariant()}).");
        if (ValueTo(faction, job.GiveMaterial) <= 0) return new(false, $"The {faction.Name} have no use for {job.GiveMaterial}.");
        if (!SellsOf(faction).Contains(job.GetMaterial) || job.GiveMaterial == job.GetMaterial) return new(false, $"The {faction.Name} don't sell {job.GetMaterial} for {job.GiveMaterial}.");
        job.Active = true;
        job.WaitReason = null;
        job.State = craft.Inventory[job.GiveMaterial] > 0 ? TradeJobState.ToTrader : TradeJobState.ToHome;
        craft.Job = job;
        return new(true, $"{craft.Name}: trading {job.GiveMaterial} for {job.GetMaterial} with the {faction.Name}.");
    }

    private void WaitTrade(CraftState craft, TradeJob job, TradeJobState resume, string reason, double retryS)
    {
        if (craft.IsDocked) TryUndock(craft.Id); // don't hold the port while blocked
        job.State = TradeJobState.Waiting;
        job.ResumeState = resume;
        job.WaitReason = reason;
        job.RetryAtTick = Tick + (long)(retryS * FixedStepClock.TicksPerSecond);
        craft.Controls = new ShipControls(0, 0, true);
    }

    private void StepTradeJob(CraftState craft, TradeJob job)
    {
        if (job.State == TradeJobState.Waiting)
        {
            if (Tick < job.RetryAtTick) { craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded); return; }
            job.State = job.ResumeState;
            job.WaitReason = null;
        }
        if (!_craftsById.TryGetValue(job.HomeId, out var home) || home.IsWreck) { WaitTrade(craft, job, job.State, StationGone("home station"), JobStationRetryS); return; }
        switch (job.State)
        {
            case TradeJobState.ToHome:
                switch (FlyToDock(craft, home))
                {
                    case DockApproach.Docked: job.State = TradeJobState.AtHome; break;
                    case DockApproach.NoFreePort: WaitTrade(craft, job, TradeJobState.ToHome, $"queued for a berth at {home.Name}", 0); break;
                }
                return;

            case TradeJobState.AtHome:
                craft.Controls = ShipControls.Idle;
                if (craft.DockedToCraftId != home.Id) { job.State = TradeJobState.ToHome; return; }
                if (craft.Inventory[job.GetMaterial] > 0)
                {
                    var unload = MaxTransferKg(craft.Id, home.Id, job.GetMaterial);
                    if (unload <= 0) { WaitTrade(craft, job, TradeJobState.ToHome, $"{home.Name} is full", JobStationRetryS); return; }
                    if (TryTransfer(craft.Id, home.Id, job.GetMaterial, unload).Done) job.DeliveredKg += unload;
                    if (craft.Inventory[job.GetMaterial] > 0) return;
                    job.CompletedCycles++;
                }
                var spare = Math.Max(0, home.Inventory[job.GiveMaterial] - WantedKg(home, job.GiveMaterial)); // not what home asks to keep
                var load = Math.Min(spare, MaxTransferKg(home.Id, craft.Id, job.GiveMaterial));
                if (load >= 1_000) TryTransfer(home.Id, craft.Id, job.GiveMaterial, load);
                if (craft.Inventory[job.GiveMaterial] > 0) job.State = TradeJobState.ToTrader;
                else WaitTrade(craft, job, TradeJobState.ToHome, $"{home.Name} has no {job.GiveMaterial} to spare", JobStationRetryS);
                return;

            case TradeJobState.ToTrader:
            case TradeJobState.Trading:
                if (!_craftsById.TryGetValue(job.TraderId, out var trader) || trader.IsWreck || FactionById(trader.FactionId) is not { } faction)
                {
                    WaitTrade(craft, job, TradeJobState.ToTrader, "the trade station is gone", JobStationRetryS);
                    return;
                }
                if (!WillTrade(faction))
                {
                    WaitTrade(craft, job, TradeJobState.ToTrader, $"the {faction.Name} won't trade with you ({faction.Disposition.ToString().ToLowerInvariant()})", JobStationRetryS);
                    return;
                }
                if (job.State == TradeJobState.ToTrader)
                {
                    switch (FlyToDock(craft, trader))
                    {
                        case DockApproach.Docked: job.State = TradeJobState.Trading; break;
                        case DockApproach.NoFreePort: WaitTrade(craft, job, TradeJobState.ToTrader, $"queued for a berth at {trader.Name}", 0); break;
                    }
                    return;
                }
                craft.Controls = ShipControls.Idle;
                if (craft.DockedToCraftId != trader.Id) { job.State = TradeJobState.ToTrader; return; }
                var deal = Barter(craft.Id, job.GiveMaterial, craft.Inventory[job.GiveMaterial], job.GetMaterial);
                if (deal.Done || craft.Inventory[job.GetMaterial] > 0) { job.State = TradeJobState.ToHome; return; }
                WaitTrade(craft, job, TradeJobState.ToTrader, deal.Message, JobStationRetryS * 2);
                return;
        }
    }
}
