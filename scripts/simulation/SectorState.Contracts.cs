using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

public enum ContractKind { Delivery, Survey, Rescue }

public enum ContractState { Offered, Accepted, Done, Failed, Expired, Withdrawn }

/// <summary>Work a civilization asks for (#124): what, by when, and what it pays.</summary>
public sealed class Contract
{
    public Contract(int id, int factionId, ContractKind kind, long offeredTick, long offerUntilTick, long durationTicks, ulong roll) =>
        (Id, FactionId, Kind, OfferedTick, OfferUntilTick, DurationTicks, Roll) = (id, factionId, kind, offeredTick, offerUntilTick, durationTicks, roll);

    public int Id { get; }
    public int FactionId { get; }
    public ContractKind Kind { get; }
    public long OfferedTick { get; }
    public long OfferUntilTick { get; }
    public long DurationTicks { get; }
    public ulong Roll { get; }
    public ContractState State { get; internal set; }

    /// <summary>Set when accepted.</summary>
    public long DeadlineTick { get; internal set; }

    /// <summary>A delivery's (or a rescue's) material and amount, and how much has been handed over.</summary>
    public string Material { get; internal set; } = "";
    public long Kg { get; internal set; }
    public long DoneKg { get; internal set; }

    /// <summary>A survey's bodies.</summary>
    public List<BodyId> Bodies { get; internal set; } = new();

    /// <summary>A rescue: where the stranded ship waits (a body it orbits) and, once accepted, the ship.</summary>
    public BodyId Place { get; internal set; } = BodyId.None;
    public CraftId Stranded { get; internal set; } = CraftId.None;

    // What it pays: any of these.
    public string RewardMaterial { get; internal set; } = "";
    public long RewardKg { get; internal set; }
    public double Goodwill { get; internal set; }
    public double Research { get; internal set; }
    public double AccessHours { get; internal set; }

    /// <summary>How it ended.</summary>
    public string Result { get; internal set; } = "";

    public bool IsOpen => State is ContractState.Offered or ContractState.Accepted;
}

/// <summary>
/// Contracts (#124). Known civilizations that aren't hostile offer work now and then: deliveries to their trade station, surveys, and
/// rescues of a stranded ship of theirs. The creator's choices: offers left alone just expire; one accepted and missed, or given up,
/// costs a little goodwill. Rewards are goods, goodwill, research, and mining rights in their system. Deliveries can be handed to a
/// freight job; surveys count however they're done; rescues are flown in person. Goods come from outside the sector and leave it
/// with the ledger exact.
/// </summary>
public sealed partial class SectorState
{
    public const long OfferTicks = 30 * 60 * FixedStepClock.TicksPerSecond;
    public const int OffersPerCivilization = 3;
    public const double FailGoodwill = 4, RescueRangeM = 150;
    public const long RescueFuelKg = 400;

    private readonly List<Contract> _contracts = new();
    private readonly SortedDictionary<int, long> _nextOffer = new();
    private int _nextContractId = 1;

    /// <summary>Every contract, closed ones too (the latest few), in id order.</summary>
    public IReadOnlyList<Contract> CivilizationContracts => _contracts;
    public IReadOnlyDictionary<int, long> NextOffers => _nextOffer;
    public int NextContractId => _nextContractId;

    public Contract? ContractById(int id) => _contracts.FirstOrDefault(c => c.Id == id);

    internal void RestoreContract(Contract contract)
    {
        if (contract.Id >= _nextContractId || _contracts.Any(c => c.Id == contract.Id)) throw new FormatException($"Contract {contract.Id} has an invalid number.");
        _contracts.Add(contract);
    }

    internal void RestoreContractState(int nextId, IEnumerable<KeyValuePair<int, long>> next)
    {
        _nextContractId = Math.Max(1, nextId);
        foreach (var (faction, tick) in next) _nextOffer[faction] = tick;
    }

    /// <summary>Minutes between a civilization's offers, on average.</summary>
    public static double OfferMinutes(WorldAmount amount) => amount switch { WorldAmount.Rare => 120, WorldAmount.Often => 20, _ => 45 };

    private static bool Offers(Faction faction) => faction.Disposition is Disposition.Neutral or Disposition.Friendly;

    // --- Offers ---

    private void StepContracts()
    {
        if (Tick % FixedStepClock.TicksPerSecond != 0) return;
        var amount = Options[WorldOptions.Contracts];
        foreach (var faction in _factions)
        {
            if (amount == WorldAmount.Off || !Offers(faction)) { _nextOffer.Remove(faction.Id); continue; }
            var mean = OfferMinutes(amount) * 60 * FixedStepClock.TicksPerSecond;
            var rng = new DeterministicRandom(Seed ^ ((ulong)faction.Id * 0x9E3779B97F4A7C15UL) ^ ((ulong)Tick * 0xD1B54A32D192ED03UL));
            if (!_nextOffer.TryGetValue(faction.Id, out var due) || due - Tick > 1.5 * mean + 1) { _nextOffer[faction.Id] = Tick + (long)(mean * rng.Range(0.5, 1.5)); continue; }
            if (Tick < due) continue;
            _nextOffer[faction.Id] = Tick + (long)(mean * rng.Range(0.5, 1.5));
            if (_contracts.Count(c => c.FactionId == faction.Id && c.State == ContractState.Offered) < OffersPerCivilization) Offer(faction, rng);
        }
        foreach (var contract in _contracts) CheckContract(contract);
        if (_contracts.Count(c => !c.IsOpen) > 40) _contracts.RemoveAt(_contracts.FindIndex(c => !c.IsOpen)); // the record of old ones stays short
    }

    /// <summary>DEBUG and tests: an offer of this kind from a civilization now, if it can make one.</summary>
    public Contract? DebugOffer(Faction faction, ContractKind kind) => Offer(faction, new DeterministicRandom(Seed ^ (ulong)Tick ^ (ulong)kind), kind);

    private Contract? Offer(Faction faction, DeterministicRandom rng, ContractKind? only = null)
    {
        if (Parts is null || TradeStationOf(faction) is null && only is null) return null;
        var kinds = new List<ContractKind>();
        foreach (var kind in only is { } k ? new[] { k } : Enum.GetValues<ContractKind>())
            if (kind != ContractKind.Survey || UnsurveyedFor(faction).Count > 0) kinds.Add(kind);
        if (kinds.Count == 0) return null;
        // Culture and race tilt what they ask: deliveries most, rescues least.
        var weights = kinds.Select(k => k switch { ContractKind.Delivery => 5.0, ContractKind.Survey => faction.Culture.Id is "conservationist" or "pacifist" ? 4.0 : 2.5, _ => faction.Culture.Id == "militarist" ? 1.0 : 2.0 }).ToList();
        var pick = rng.NextDouble() * weights.Sum();
        var chosen = kinds[^1];
        for (var i = 0; i < kinds.Count; i++) if ((pick -= weights[i]) < 0) { chosen = kinds[i]; break; }

        var hours = chosen switch { ContractKind.Delivery => 2.0, ContractKind.Survey => 3.0, _ => 1.5 };
        var contract = new Contract(_nextContractId, faction.Id, chosen, Tick, Tick + OfferTicks, (long)(hours * 3600 * FixedStepClock.TicksPerSecond), rng.NextUInt64());
        double worth;
        switch (chosen)
        {
            case ContractKind.Delivery:
                var wanted = Parts.Materials.Materials.Where(m => m.Value >= 1 && m.Id is not (ShellMaterial or MissileMaterial or AntimatterMaterial) && !SellsOf(faction).Contains(m.Id)
                    && (faction.Race?.Supplies != false || m.Id is not (FoodMaterial or WaterMaterial))).ToList();
                if (wanted.Count == 0) return null;
                var material = wanted.OrderBy(m => m.Id, StringComparer.Ordinal).ElementAt((int)(rng.NextDouble() * wanted.Count));
                worth = rng.Range(20_000, 60_000);
                contract.Material = material.Id;
                contract.Kg = Math.Clamp((long)Math.Round(worth / ValueTo(faction, material.Id) / 500) * 500, 500, 40_000);
                worth = contract.Kg * ValueTo(faction, material.Id);
                break;
            case ContractKind.Survey:
                var bodies = UnsurveyedFor(faction);
                contract.Bodies = bodies.OrderBy(_ => rng.NextUInt64()).Take(3).Select(b => b.Id).OrderBy(b => b.Value).ToList();
                worth = 15_000 * contract.Bodies.Count;
                break;
            default:
                var places = _bodies.Where(b => StarOf(b.Id)?.Id == faction.HomeStarId && b.Kind is BodyKind.Planet or BodyKind.Moon or BodyKind.GasGiant && b.Id.Value < PointBodyIdBase).ToList();
                if (places.Count == 0) return null;
                contract.Place = places[(int)(rng.NextDouble() * places.Count)].Id;
                contract.Material = FuelMaterial;
                contract.Kg = RescueFuelKg;
                worth = 45_000;
                break;
        }
        // One or two rewards, worth more than the work; the culture leans to one.
        var leaning = faction.Culture.Id switch { "pacifist" => 2, "militarist" => 0, "territorial" => 3, _ => 1 };
        var first = rng.NextDouble() < 0.5 ? leaning : (int)(rng.NextDouble() * 4);
        var second = rng.NextDouble() < 0.4 ? (first + 1 + (int)(rng.NextDouble() * 3)) % 4 : -1;
        var share = second < 0 ? 1.5 : 0.8;
        foreach (var reward in second < 0 ? new[] { first } : new[] { first, second })
            switch (reward)
            {
                case 0:
                    var goods = SellsOf(faction);
                    if (goods.Count == 0) { contract.Goodwill += worth * share / TradeValuePerGoodwill * 3; break; }
                    contract.RewardMaterial = goods[(int)(rng.NextDouble() * goods.Count)];
                    contract.RewardKg = Math.Max(100, (long)Math.Round(worth * share / ValueTo(faction, contract.RewardMaterial) / 100) * 100);
                    break;
                case 1: contract.Goodwill += Math.Round(worth * share / TradeValuePerGoodwill * 3, 1); break;
                case 2: contract.Research += Math.Round(worth * share / 1_000 / 5) * 5; break;
                default: contract.AccessHours += Math.Round(worth * share / 15_000 * 2) / 2; break;
            }
        _nextContractId++;
        _contracts.Add(contract);
        AddEvent($"The {faction.Name} offer a contract: {Describe(contract)}.", EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
        return contract;
    }

    /// <summary>Unsurveyed mineable bodies a civilization wants surveyed: in its own system first, else where you've been.</summary>
    private List<CelestialBodyState> UnsurveyedFor(Faction faction)
    {
        var bodies = _bodies.Where(b => b.IsMineable && !b.Surveyed && !b.Unscanned && b.Id.Value < PointBodyIdBase).ToList();
        var theirs = bodies.Where(b => StarOf(b.Id)?.Id == faction.HomeStarId).ToList();
        return theirs.Count > 0 ? theirs : bodies.Where(b => StarOf(b.Id) is { } star && star.Id != _stars[0].Id).ToList();
    }

    // --- Words ---

    public string Describe(Contract contract) => contract.Kind switch
    {
        ContractKind.Delivery => $"deliver {contract.Kg / 1000.0:0.#} t of {Named(contract.Material)} to {StationName(contract)}",
        ContractKind.Survey => $"survey {string.Join(", ", contract.Bodies.Select(b => _byId.TryGetValue(b, out var body) ? body.Name : "?"))}",
        _ => $"bring {contract.Kg} kg of {Named(contract.Material)} to a ship of theirs stranded near {(_byId.TryGetValue(contract.Place, out var place) ? place.Name : "?")}",
    };

    public string RewardText(Contract contract)
    {
        var parts = new List<string>();
        if (contract.RewardKg > 0) parts.Add($"{contract.RewardKg / 1000.0:0.#} t of {Named(contract.RewardMaterial)}");
        if (contract.Goodwill > 0) parts.Add($"{contract.Goodwill:0.#} goodwill");
        if (contract.Research > 0) parts.Add($"{contract.Research:0} research points");
        if (contract.AccessHours > 0) parts.Add($"mining in their system without offence for {contract.AccessHours:0.#} h");
        return string.Join(", ", parts);
    }

    private string Named(string material) => Parts is { } p && p.Materials.Contains(material) ? p.Materials.Get(material).Name.ToLowerInvariant() : material.Replace('_', ' ');

    private string StationName(Contract contract) => FactionById(contract.FactionId) is { } f && TradeStationOf(f) is { } station ? station.Name : "their trade station";

    // --- Accepting, giving up ---

    public ActionResult AcceptContract(int id)
    {
        if (ContractById(id) is not { State: ContractState.Offered } contract || FactionById(contract.FactionId) is not { } faction) return new(false, "That offer isn't open.");
        if (contract.Kind == ContractKind.Rescue && MakeStranded(contract, faction) is { } why) return new(false, why);
        contract.State = ContractState.Accepted;
        contract.DeadlineTick = Tick + contract.DurationTicks;
        return new(true, $"Accepted: {Describe(contract)}, within {contract.DurationTicks / (3600.0 * FixedStepClock.TicksPerSecond):0.#} h.");
    }

    public ActionResult RefuseContract(int id)
    {
        if (ContractById(id) is not { State: ContractState.Offered } contract) return new(false, "That offer isn't open.");
        Close(contract, ContractState.Withdrawn, "Refused.");
        return new(true, "Refused: no hard feelings.");
    }

    public ActionResult GiveUpContract(int id)
    {
        if (ContractById(id) is not { State: ContractState.Accepted } contract || FactionById(contract.FactionId) is not { } faction) return new(false, "That contract isn't under way.");
        Fail(contract, faction, "Given up.");
        return new(true, $"Given up: the {faction.Name} think less of you.");
    }

    private void Fail(Contract contract, Faction faction, string why)
    {
        var fromGoodwill = Math.Min(faction.Goodwill, FailGoodwill);
        faction.Goodwill -= fromGoodwill;
        faction.Grievance = Math.Min(100, faction.Grievance + FailGoodwill - fromGoodwill);
        Close(contract, ContractState.Failed, why);
        AddEvent($"The {faction.Name}'s contract failed: {why.TrimEnd('.').ToLowerInvariant()}.", EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
    }

    private void Close(Contract contract, ContractState state, string result)
    {
        contract.State = state;
        contract.Result = result;
        if (!contract.Stranded.IsNone && _craftsById.TryGetValue(contract.Stranded, out var ship)) // it goes home, or is lost
        {
            DiscoveredKg -= (long)Math.Round(ship.Stats.TotalMassKg) + ship.CargoKg;
            RemoveCraft(ship);
        }
        contract.Stranded = CraftId.None;
    }

    /// <summary>Once a second: the ends of things.</summary>
    private void CheckContract(Contract contract)
    {
        if (!contract.IsOpen) return;
        if (FactionById(contract.FactionId) is not { } faction) { Close(contract, ContractState.Withdrawn, "They're gone."); return; }
        if (!Offers(faction) && (contract.State == ContractState.Offered || faction.Disposition == Disposition.Hostile))
        {
            Close(contract, ContractState.Withdrawn, contract.State == ContractState.Offered ? "Withdrawn." : "They broke it off.");
            return;
        }
        if (contract.State == ContractState.Offered)
        {
            if (Tick >= contract.OfferUntilTick) Close(contract, ContractState.Expired, "Not taken up.");
            return;
        }
        switch (contract.Kind)
        {
            case ContractKind.Survey when contract.Bodies.All(b => !_byId.TryGetValue(b, out var body) || body.Surveyed):
                Complete(contract, faction, null);
                return;
            case ContractKind.Rescue when !_craftsById.ContainsKey(contract.Stranded):
                Fail(contract, faction, "Their ship was lost.");
                return;
        }
        if (Tick >= contract.DeadlineTick) Fail(contract, faction, "Out of time.");
    }

    private void Complete(Contract contract, Faction faction, CraftState? by)
    {
        if (contract.Goodwill > 0) AddGoodwill(faction, contract.Goodwill);
        if (contract.Research > 0) ResearchPoints += contract.Research;
        if (contract.AccessHours > 0) faction.MiningAccessUntilTick = Math.Max(faction.MiningAccessUntilTick, Tick) + (long)(contract.AccessHours * 3600 * FixedStepClock.TicksPerSecond);
        var paid = "";
        if (contract.RewardKg > 0 && Payee(by) is { } payee)
        {
            var kg = Math.Min(contract.RewardKg, payee.HasCargoLimit ? payee.FreeCargoKg : contract.RewardKg);
            if (kg > 0)
            {
                payee.Inventory.Add(contract.RewardMaterial, kg);
                DiscoveredKg += kg; // from outside
                paid = $" {kg / 1000.0:0.#} t of {Named(contract.RewardMaterial)} went to {payee.Name}" + (kg < contract.RewardKg ? " (all it had room for)." : ".");
            }
        }
        Close(contract, ContractState.Done, "Done." + paid);
        AddEvent($"Contract done for the {faction.Name}: {RewardText(contract)}.{paid}", EventKind.Civilization, by is null ? TargetRef.None : TargetRef.Of(by));
        if (contract.Kind != ContractKind.Delivery) return;
        foreach (var craft in _crafts) // freighters on it stop
            if (craft.Job is HaulJob { Active: true } haul && TradeStationOf(faction)?.Id == haul.DestinationId && haul.MaterialId == contract.Material
                && !_contracts.Any(c => c.State == ContractState.Accepted && c.FactionId == faction.Id && c.Material == haul.MaterialId))
            {
                haul.Active = false;
                haul.WaitReason = "the contract is done";
            }
    }

    /// <summary>Your station nearest to whoever finished it with room, else that craft.</summary>
    private CraftState? Payee(CraftState? by)
    {
        var from = by?.Position ?? _crafts.FirstOrDefault(c => c.Id == PilotCraftId)?.Position;
        if (from is not { } at) return by;
        var station = _crafts.Where(c => IsPlayers(c) && !c.IsWreck && !Movable(c) && c.FreeCargoKg > 1_000 && StarAt(c.Position)?.Id == StarAt(at)?.Id)
            .OrderBy(c => c.Position.DistanceTo(at)).ThenBy(c => c.Id.Value).FirstOrDefault();
        return station ?? by;
    }

    // --- Deliveries ---

    /// <summary>The open delivery of this material to this trade station, if any.</summary>
    public Contract? DeliveryTo(CraftState station, string material) =>
        station.IsTradeStation ? _contracts.FirstOrDefault(c => c.State == ContractState.Accepted && c.Kind == ContractKind.Delivery && c.FactionId == station.FactionId && c.Material == material) : null;

    /// <summary>Counts goods handed to a trade station; returns how much of what's offered it takes.</summary>
    private long Deliver(CraftState from, CraftState station, string material, long kg)
    {
        if (DeliveryTo(station, material) is not { } contract || FactionById(contract.FactionId) is not { } faction) return 0;
        var take = Math.Min(kg, Math.Min(from.Inventory[material], contract.Kg - contract.DoneKg));
        if (take <= 0) return 0;
        from.Inventory.TryRemove(new Dictionary<string, long> { [material] = take });
        station.Inventory.Add(material, take);
        contract.DoneKg += take;
        if (contract.DoneKg >= contract.Kg) Complete(contract, faction, from);
        return take;
    }

    /// <summary>Hands over the contract's goods from the ship you fly, docked at their station.</summary>
    public ActionResult HandOver(CraftId craftId, int id)
    {
        var craft = GetCraft(craftId);
        if (ContractById(id) is not { State: ContractState.Accepted } contract || FactionById(contract.FactionId) is not { } faction) return new(false, "That contract isn't under way.");
        if (contract.Kind == ContractKind.Rescue) return Rescue(craft, contract, faction);
        if (contract.Kind != ContractKind.Delivery) return new(false, "There's nothing to hand over.");
        if (!craft.IsDocked || TradeStationOf(faction) is not { } station || craft.DockedToCraftId != station.Id) return new(false, $"Dock at {StationName(contract)} first.");
        if (craft.Inventory[contract.Material] <= 0) return new(false, $"You carry no {Named(contract.Material)}.");
        var given = Deliver(craft, station, contract.Material, long.MaxValue);
        return new(true, contract.State == ContractState.Done ? "Delivered: the contract is done." : $"Handed over {given / 1000.0:0.#} t; {(contract.Kg - contract.DoneKg) / 1000.0:0.#} t to go.");
    }

    // --- Rescues ---

    private static readonly ShipDesign StrandedDesign = new("stranded_ship", "Small ship", new[]
    {
        new DesignCell(new GridPos(0, -1), "cockpit"), new DesignCell(new GridPos(0, 0), "hull"), new DesignCell(new GridPos(-1, 0), "cargo"), new DesignCell(new GridPos(1, 0), "cargo"),
        new DesignCell(new GridPos(0, 1), "thruster"),
    });

    private string? MakeStranded(Contract contract, Faction faction)
    {
        if (Parts is null || !_byId.TryGetValue(contract.Place, out var body)) return "The place is gone.";
        var rng = new DeterministicRandom(contract.Roll);
        var ship = new CraftState(AllocateCraftId(), $"{faction.Name} {StrandedNames[(int)(rng.NextDouble() * StrandedNames.Length)]}", StrandedDesign, StrandedDesign.ComputeStats(Parts), Vector2D.Zero, rng.Range(0, 2 * Math.PI))
        {
            FactionId = faction.Id, IsStranded = true,
        };
        AddCraft(ship);
        var radius = body.RadiusM + rng.Range(8_000, 30_000);
        PutOnRails(ship.Id, new CircularOrbit(body.Id, radius, CircularOrbitPeriodS(body, radius, 3_600), rng.Range(0, 2 * Math.PI)));
        DiscoveredKg += (long)Math.Round(ship.Stats.TotalMassKg); // it came from outside
        contract.Stranded = ship.Id;
        return null;
    }

    private static readonly string[] StrandedNames = { "Wayfarer", "Lark", "Small Mercy", "Tern", "Driftwood", "Errand" };

    private ActionResult Rescue(CraftState craft, Contract contract, Faction faction)
    {
        if (!_craftsById.TryGetValue(contract.Stranded, out var ship)) return new(false, "Their ship is gone.");
        var gap = craft.Position.DistanceTo(ship.Position) - craft.CollisionRadiusM - ship.CollisionRadiusM;
        if (gap > RescueRangeM) return new(false, $"Come within {RescueRangeM:0} m of {ship.Name} ({MathF.Round((float)gap):0} m now).");
        if ((craft.Velocity - ship.Velocity).Length > SalvageMaxSpeedMps) return new(false, $"Match {ship.Name}'s speed.");
        if (craft.Inventory[contract.Material] < contract.Kg) return new(false, $"You need {contract.Kg} kg of {Named(contract.Material)} aboard.");
        craft.Inventory.TryRemove(new Dictionary<string, long> { [contract.Material] = contract.Kg });
        ship.Inventory.Add(contract.Material, contract.Kg); // and it leaves with it (Close)
        Complete(contract, faction, craft);
        return new(true, $"{ship.Name} is on its way home.");
    }

    /// <summary>What a stranded ship wants of the ship you fly, for the HUD; null if it's not one.</summary>
    public Contract? RescueOf(CraftState ship) => ship.IsStranded ? _contracts.FirstOrDefault(c => c.Stranded == ship.Id && c.State == ContractState.Accepted) : null;
}
