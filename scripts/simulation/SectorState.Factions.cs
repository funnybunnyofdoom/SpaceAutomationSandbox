using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Other civilizations and the player's laws (#36). Civilizations act through attitude-driven
/// events; a raid is a real fight (#84, SectorState.Combat). One may claim a system a warp gate opens (about one in four, never the home system). Its
/// attitude drifts towards what its culture thinks of your laws, minus grievances for mining in its system.
/// A hostile one raids your gate in its system now and then; gate defense batteries fire with the Armed gates law. A destroyed far gate leaves the near gate tied to that system:
/// its next jump sends a new far gate through, built from its stock as when it first opened the system.
/// Civilizations never follow you through gates. Deterministic: every roll comes from the seed and the tick.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Chance that a newly opened system is claimed by a civilization.</summary>
    public const double EncounterChance = 0.25;

    /// <summary>How often each civilization decides whether to act (raid, warn).</summary>
    public const double FactionDecisionIntervalS = 300;

    /// <summary>Chance a hostile civilization raids your gate at each decision.</summary>
    public const double RaidChance = 0.5;

    /// <summary>Attitude points a civilization moves per minute towards its target.</summary>
    public const double AttitudeDriftPerMinute = 2;

    /// <summary>Grievance per tonne mined in a civilization's system, and how fast it fades (points per minute).</summary>
    public const double GrievancePerTonne = 0.1, GrievanceFadePerMinute = 0.1;

    public const int MaxEvents = 300;

    private readonly List<Faction> _factions = new();
    private readonly SortedSet<string> _laws = new(StringComparer.Ordinal);
    private readonly List<SectorEvent> _events = new();
    private readonly Dictionary<int, Disposition> _lastDisposition = new();
    private readonly Dictionary<int, bool> _warned = new();

    public IReadOnlyList<Faction> Factions => _factions;

    /// <summary>The laws in force (ids from <see cref="Simulation.Laws"/>).</summary>
    public IReadOnlyCollection<string> ActiveLaws => _laws;

    public bool LawActive(string lawId) => _laws.Contains(lawId);

    /// <summary>What happened, oldest first (at most <see cref="MaxEvents"/>).</summary>
    public IReadOnlyList<SectorEvent> Events => _events;

    /// <summary>Notes what happened, what kind of thing it is, and the craft or body it's about, if any.</summary>
    public void AddEvent(string text, EventKind kind = EventKind.Other, TargetRef target = default)
    {
        _events.Add(new SectorEvent(Tick, text, kind, target));
        if (_events.Count > MaxEvents) _events.RemoveAt(0);
    }

    public ActionResult SetLaw(string lawId, bool on)
    {
        if (Laws.All.All(l => l.Id != lawId)) return new(false, $"No law '{lawId}'.");
        if (on == _laws.Contains(lawId)) return new(false, $"{Laws.Get(lawId).Name} is already {(on ? "in force" : "off")}.");
        if (on) _laws.Add(lawId); else _laws.Remove(lawId);
        var text = $"{Laws.Get(lawId).Name} {(on ? "enacted" : "repealed")}.";
        AddEvent(text, EventKind.Civilization);
        return new(true, text);
    }

    /// <summary>The civilization claiming the system a star is in, or null.</summary>
    public Faction? FactionAtStar(BodyId starId) => _factions.FirstOrDefault(f => f.HomeStarId == starId);

    /// <summary>The civilization claiming the system a body is in, or null.</summary>
    public Faction? ClaimantOf(BodyId bodyId) => StarOf(bodyId) is { } star ? FactionAtStar(star.Id) : null;

    /// <summary>The civilization claiming the system a point is in, or null.</summary>
    public Faction? ClaimantAt(Vector2D point) => StarAt(point) is { } star ? FactionAtStar(star.Id) : null;

    /// <summary>Why nothing may be mined on this body under the laws in force (Respect claimed systems), or null.</summary>
    public string? LawForbidsMining(CelestialBodyState body)
    {
        if (LawActive(Laws.RespectClaims) && ClaimantOf(body.Id) is { } owner)
            return $"law: {body.Name} is in a system the {owner.Name} claim (Respect claimed systems)";
        return null;
    }

    /// <summary>Bulk rock or ice may be mined unless No planet dismantling is in force.</summary>
    public bool BulkMiningAllowed => !LawActive(Laws.NoDismantling);

    /// <summary>Kg of a material that may be mined at a bearing, under the laws in force.</summary>
    public long MineableAt(CelestialBodyState body, string material, double bearing) =>
        LawForbidsMining(body) is not null || LifeProtected(body.SiteAt(bearing)) ? 0 // native life protected (#91)
            : material == BiomassMaterial ? body.BiomassKg(body.SiteAt(bearing), JobLeavesCover)
            : body.DepositAt(material, bearing, BulkMiningAllowed);

    /// <summary>Kg of a material in the surveyed sites that may be mined, under the laws in force.</summary>
    public long MineableSurveyed(CelestialBodyState body, string material) =>
        LawForbidsMining(body) is null ? body.SurveyedDeposit(material, BulkMiningAllowed, LawActive(Laws.ProtectLife), JobLeavesCover) : 0;

    // --- Meeting civilizations ---

    /// <summary>Called when a gate opens system <paramref name="index"/>: maybe a civilization claims it.</summary>
    private void MaybeMeetCivilization(CelestialBodyState star, int index)
    {
        if (!CivilizationAt(Seed, index, out var rng)) return;
        MeetCivilization(star, rng);
    }

    /// <summary>Whether system <paramref name="index"/> of a game with this seed is claimed (never the home system, 0).</summary>
    public static bool CivilizationAt(ulong seed, int index, out DeterministicRandom rng)
    {
        rng = new DeterministicRandom(seed ^ (0xC1F11A5EUL * (ulong)(index + 1)));
        return index > 0 && rng.NextDouble() < EncounterChance;
    }

    /// <summary>Adds a civilization claiming a star's system (also a debug cheat and for tests).</summary>
    public Faction MeetCivilization(CelestialBodyState star, DeterministicRandom? rng = null, string? cultureId = null, double? baseline = null)
    {
        rng ??= new DeterministicRandom(Seed ^ (0xFAC7104EUL * (ulong)(_factions.Count + 1)) ^ star.Id.Value);
        var culture = cultureId is null ? Culture.All[(int)(rng.NextUInt64() % (ulong)Culture.All.Count)] : Culture.Get(cultureId);
        var faction = new Faction(_factions.Count + 1, FactionName(rng), culture.Id, star.Id, baseline ?? Math.Round(rng.Range(-20, 20)))
        {
            NextEventTick = Tick + (long)(FactionDecisionIntervalS * FixedStepClock.TicksPerSecond),
        };
        _factions.Add(faction);
        _lastDisposition[faction.Id] = faction.Disposition;
        var likes = culture.Stances.Where(s => s.Value > 0).Select(s => Laws.Get(s.Key).Name).ToList();
        var dislikes = culture.Stances.Where(s => s.Value < 0).Select(s => Laws.Get(s.Key).Name).ToList();
        AddEvent($"Contact: the {faction.Name}, a {culture.Name} civilization, claim the {star.Name} system. "
                 + $"They favour {string.Join(" and ", likes)}" + (dislikes.Count > 0 ? $" and oppose {string.Join(" and ", dislikes)}." : "."), EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
        return faction;
    }

    private static readonly string[] NameStarts = { "Ve", "Kor", "Thal", "Ys", "Mar", "Oru", "Zen", "Ash", "Quel", "Dra", "Ulo", "Ner" };
    private static readonly string[] NameEnds = { "ari", "eth", "on", "ussi", "ar", "iel", "ox", "anth", "eni", "or", "umi", "ax" };

    private string FactionName(DeterministicRandom rng)
    {
        for (var attempt = 0; ; attempt++)
        {
            var name = NameStarts[rng.NextUInt64() % (ulong)NameStarts.Length] + NameEnds[rng.NextUInt64() % (ulong)NameEnds.Length];
            if (attempt > 20 || _factions.All(f => f.Name != name)) return name;
        }
    }

    // --- Each tick ---

    private void StepFactions()
    {
        if (_factions.Count == 0 || Tick % FixedStepClock.TicksPerSecond != 0) return; // once a second is plenty
        UpdateLifeOpinion(); // #94
        StepFleets(); // their patrols (#89)
        StepTrade(); // their trade stations (#89)
        foreach (var faction in _factions)
        {
            faction.Grievance = Math.Max(0, faction.Grievance - GrievanceFadePerMinute / 60);
            if (faction.Grievance == 0) _warned[faction.Id] = false;
            var target = faction.TargetAttitude(_laws);
            var step = AttitudeDriftPerMinute / 60;
            faction.Attitude = Math.Abs(target - faction.Attitude) <= step ? target : faction.Attitude + Math.Sign(target - faction.Attitude) * step;

            if (Tick < faction.TruceUntilTick) faction.Attitude = Math.Max(faction.Attitude, TruceAttitude); // peace holds for a while (#89)
            var now = faction.Disposition;
            if (_lastDisposition.TryGetValue(faction.Id, out var before) && before != now)
                AddEvent($"The {faction.Name} are now {now.ToString().ToLowerInvariant()} towards you.", EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
            _lastDisposition[faction.Id] = now;

            if (Tick < faction.NextEventTick) continue;
            faction.NextEventTick = Tick + (long)(FactionDecisionIntervalS * FixedStepClock.TicksPerSecond);
            Decide(faction);
        }
    }

    private void Decide(Faction faction)
    {
        var rng = new DeterministicRandom(Seed ^ ((ulong)faction.Id * 0x9E3779B97F4A7C15UL) ^ (ulong)Tick);
        if (faction.Disposition == Disposition.Hostile)
        {
            if (RaidOn(faction)) return;
            var gate = _crafts.Where(c => IsGate(c) && !c.IsWreck && StarAt(c.Position)?.Id == faction.HomeStarId).OrderBy(c => c.Id.Value).FirstOrDefault();
            if (gate is null || rng.NextDouble() >= RaidChance) return;
            StartRaid(faction, gate, 2 + (int)(rng.NextUInt64() % 3)); // real raiders (#84)
        }
        else if (faction.Disposition == Disposition.Wary && faction.Grievance >= 5 && !_warned.GetValueOrDefault(faction.Id))
        {
            _warned[faction.Id] = true;
            AddEvent($"The {faction.Name} warn you: stop mining in their system.", EventKind.Civilization, new TargetRef(CraftId.None, faction.HomeStarId));
        }
    }

    /// <summary>Working gate defense batteries on a gate: only with the Armed gates law, and only while the gate works.</summary>
    public int GateDefenses(CraftState gate) =>
        LawActive(Laws.ArmedGates) && Operational(gate) ? gate.Stats.GateDefenseCount : 0;

    /// <summary>Mining in a claimed system is a grievance (called with each kg mined).</summary>
    private void NoteMining(CelestialBodyState body, long kg)
    {
        if (kg <= 0 || _factions.Count == 0 || ClaimantOf(body.Id) is not { } owner) return;
        owner.Grievance = Math.Min(100, owner.Grievance + kg / 1000.0 * GrievancePerTonne);
    }

    /// <summary>
    /// Sends a new far gate through to the system its twin was destroyed in (#36), built from the near
    /// gate's stock like the first one. Returns why it can't, or null when the link is made again.
    /// </summary>
    private string? RebuildFarGate(CraftState gate)
    {
        var star = GetBody(gate.GateLostStarId);
        var bill = gate.Design.ComputeBillOfMaterials(Parts!).ToWholeKg();
        var shortfall = gate.Inventory.Shortfall(bill);
        if (shortfall.Count > 0)
            return $"the gate in {star.Name} was destroyed: sending a new one through needs "
                   + string.Join(", ", shortfall.Select(kv => $"{kv.Value / 1000.0:0.0} t {kv.Key}")) + " more in its stock";
        gate.Inventory.TryRemove(bill);
        var planet = _bodies.Where(b => b.Kind == BodyKind.Planet && StarOf(b.Id)?.Id == star.Id).OrderBy(b => b.Id.Value).First();
        var far = new CraftState(AllocateCraftId(), $"{star.Name} gate", gate.Design, gate.Design.ComputeStats(Parts!), GetPosition(planet.Id), 0);
        AddCraft(far);
        var radius = planet.RadiusM + far.CollisionRadiusM + 700;
        PutOnRails(far.Id, new CircularOrbit(planet.Id, radius, CircularOrbitPeriodS(planet, radius, DeployOrbitPeriodS), 0));
        far.GateLinkId = gate.Id;
        gate.GateLinkId = far.Id;
        gate.GateLostStarId = BodyId.None;
        AddEvent($"{gate.Name} sent a new gate through to {star.Name}.", EventKind.Fleet, TargetRef.Of(gate));
        return null;
    }

    // --- Saves ---

    /// <summary>Restores a saved civilization exactly.</summary>
    public Faction RestoreFaction(int id, string name, string cultureId, BodyId homeStarId, double baseline, double attitude, double grievance, long nextEventTick, bool warned,
        CraftId raidGateId = default, long raidEndTick = 0, long nextPatrolTick = 0, int patrolShipsBuilt = 0,
        double goodwill = 0, long truceUntilTick = 0, long nextStationTick = 0, bool invading = false)
    {
        var faction = new Faction(id, name, cultureId, homeStarId, baseline) { Attitude = attitude, Grievance = grievance, NextEventTick = nextEventTick, RaidGateId = raidGateId, RaidEndTick = raidEndTick,
            NextPatrolTick = nextPatrolTick, PatrolShipsBuilt = patrolShipsBuilt,
            Goodwill = goodwill, TruceUntilTick = truceUntilTick, NextStationTick = nextStationTick, Invading = invading };
        _factions.Add(faction);
        _lastDisposition[id] = faction.Disposition;
        _warned[id] = warned;
        return faction;
    }

    public bool FactionWarned(int factionId) => _warned.GetValueOrDefault(factionId);

    public void RestoreLaws(IEnumerable<string> laws)
    {
        _laws.Clear();
        foreach (var law in laws) if (Laws.All.Any(l => l.Id == law)) _laws.Add(law);
    }

    public void RestoreEvents(IEnumerable<SectorEvent> events)
    {
        _events.Clear();
        _events.AddRange(events.TakeLast(MaxEvents));
    }
}
