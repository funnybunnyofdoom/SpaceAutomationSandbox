using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>What to look for (#109); anything left out doesn't matter.</summary>
public sealed record TargetQuery
{
    public string Text { get; init; } = "";
    public TargetKind? Kind { get; init; }
    public BodyId? Star { get; init; }

    /// <summary>0 for yours, else a civilization's id.</summary>
    public int? Owner { get; init; }
    public string? Job { get; init; }

    /// <summary>Bodies with this ore at a surveyed site.</summary>
    public string? Ore { get; init; }
    public bool FreePort { get; init; }

    /// <summary>Yours, waiting or with something wrong.</summary>
    public bool Attention { get; init; }
}

/// <summary>Something found: how far it is by way of any warp gates, how many jumps that takes, and the first gate. Jumps −1: no way there.</summary>
public sealed record FoundTarget(TargetRef Target, double DistanceM, int Jumps, BodyId? Star, CraftId FirstGate);

/// <summary>What a smart key offers (#110), best first, and why; nothing: why not.</summary>
public sealed record TargetChoice(IReadOnlyList<TargetRef> Targets, string Why);

public enum AlertKind { Raid, Power, Crew, Waiting }

/// <summary>Something that needs the player.</summary>
public sealed record Alert(AlertKind Kind, TargetRef Target, string Text);

/// <summary>A favourite target; its name is kept so that one that's gone can be named.</summary>
public sealed record Favourite(TargetRef Target, string Name);

/// <summary>Finding targets (#109), favourites, and the smart target keys (#110).</summary>
public sealed partial class SectorState
{
    public const int FavouriteSlots = 9;

    /// <summary>Enemies nearer than this are what matters most.</summary>
    public const double FightRangeM = 20_000;

    private readonly Favourite?[] _favourites = new Favourite?[FavouriteSlots];

    public IReadOnlyList<Favourite?> Favourites => _favourites;

    public string NameOf(TargetRef target) => !target.Craft.IsNone ? GetCraft(target.Craft).Name : GetBody(target.Body).Name;

    /// <summary>Keeps a target in a slot (0 to 8), or empties it with None. A target has one slot.</summary>
    public void SetFavourite(int slot, TargetRef target)
    {
        if (slot < 0 || slot >= FavouriteSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        if (target.IsNone) { _favourites[slot] = null; return; }
        if (!Exists(target)) return;
        for (var i = 0; i < FavouriteSlots; i++) if (_favourites[i]?.Target == target) _favourites[i] = null;
        _favourites[slot] = new Favourite(target, NameOf(target));
    }

    internal void RestoreFavourite(int slot, Favourite favourite) => _favourites[slot] = favourite;

    // --- Finding ---

    /// <summary>The ways to every system from here: how far to its arrival gate, where that is, the jumps and the first gate.</summary>
    private Dictionary<BodyId, (double Distance, Vector2D At, int Jumps, CraftId Gate)> WaysFrom(CraftState from)
    {
        var ways = new Dictionary<BodyId, (double Distance, Vector2D At, int Jumps, CraftId Gate)>();
        if (StarAt(from.Position) is not { } here) return ways;
        ways[here.Id] = (0, from.Position, 0, CraftId.None);
        var frontier = new Queue<BodyId>();
        frontier.Enqueue(here.Id);
        while (frontier.Count > 0) // fewest jumps first, gates in id order: as jobs go
        {
            var system = frontier.Dequeue();
            var way = ways[system];
            foreach (var (gate, at, to) in _gateLinks)
            {
                if (at != system || ways.ContainsKey(to) || !_craftsById.TryGetValue(gate.GateLinkId, out var far)) continue;
                ways[to] = (way.Distance + way.At.DistanceTo(gate.Position), far.Position, way.Jumps + 1, way.Jumps == 0 ? gate.Id : way.Gate);
                frontier.Enqueue(to);
            }
        }
        return ways;
    }

    /// <summary>Everything that matches, nearest first by the way there; what can't be reached is last.</summary>
    public IReadOnlyList<FoundTarget> Find(CraftState from, TargetQuery query)
    {
        var ways = WaysFrom(from);
        var stars = _stars.Select(s => (s.Id, At: GetPosition(s.Id))).ToList();
        var occupied = new Dictionary<CraftId, int>();
        if (query.FreePort)
            foreach (var craft in _crafts)
                if (craft.IsDocked) occupied[craft.DockedToCraftId] = occupied.GetValueOrDefault(craft.DockedToCraftId) + 1;
        var text = query.Text.Trim();
        var found = new List<FoundTarget>();

        void Add(TargetRef target, Vector2D at, double radius, BodyId? star)
        {
            if (query.Star is { } only && star != only) return;
            if (star is { } system && ways.TryGetValue(system, out var way))
                found.Add(new(target, Math.Max(0, way.Distance + way.At.DistanceTo(at) - radius), way.Jumps, star, way.Gate));
            else found.Add(new(target, Math.Max(0, from.Position.DistanceTo(at) - radius), star is null && ways.Count == 0 ? 0 : -1, star, CraftId.None));
        }

        if ((query.Kind is null or TargetKind.Bodies) && query is { Owner: null, Job: null, FreePort: false, Attention: false })
            foreach (var body in _bodies)
            {
                if (text.Length > 0 && !body.Name.Contains(text, StringComparison.OrdinalIgnoreCase) && body.OriginalName?.Contains(text, StringComparison.OrdinalIgnoreCase) != true) continue;
                if (query.Ore is { } ore && !HasSurveyed(body, ore)) continue;
                Add(TargetRef.Of(body), GetPosition(body.Id), body.RadiusM, body.Kind == BodyKind.Star ? body.Id : StarOf(body.Id)?.Id);
            }
        if (query.Ore is null && query.Kind != TargetKind.Bodies)
            foreach (var craft in _crafts)
            {
                if (craft.Id == from.Id) continue;
                if (text.Length > 0 && !craft.Name.Contains(text, StringComparison.OrdinalIgnoreCase) && !craft.Id.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                if (query.Kind is { } kind && KindOf(craft) != kind) continue;
                if (query.Owner is { } owner && craft.FactionId != owner) continue;
                if (query.Job is { } job && (craft.Job is not { Active: true } doing || doing.Kind != job)) continue;
                if (query.FreePort && (craft.IsWreck || IsEnemy(craft) || craft.IsDocked || occupied.GetValueOrDefault(craft.Id) >= craft.Stats.DockingPorts.Count)) continue;
                if (query.Attention && AttentionReason(craft) is null) continue;
                BodyId? star = null;
                var nearest = double.MaxValue;
                foreach (var (id, at) in stars)
                    if (at.DistanceTo(craft.Position) is var d && d < nearest) (star, nearest) = (id, d);
                Add(TargetRef.Of(craft), craft.Position, craft.CollisionRadiusM, star);
            }
        return found.OrderBy(f => f.Jumps < 0).ThenBy(f => f.DistanceM).ThenBy(f => f.Target.Craft.Value).ThenBy(f => f.Target.Body.Value).ToList();
    }

    private static bool HasSurveyed(CelestialBodyState body, string ore)
    {
        foreach (var site in body.SiteList)
            if (site.Surveyed && site.Deposits.GetValueOrDefault(ore) > 0) return true;
        return false;
    }

    /// <summary>Ores at surveyed sites anywhere, in name order.</summary>
    public IReadOnlyList<string> SurveyedOres() =>
        _bodies.SelectMany(b => b.SiteList).Where(s => s.Surveyed).SelectMany(s => s.Deposits).Where(d => d.Value > 0).Select(d => d.Key).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToList();

    // --- What needs the player ---

    /// <summary>What one of your craft needs you for, or null.</summary>
    public static string? AttentionReason(CraftState craft) =>
        !IsPlayers(craft) || craft.IsWreck ? null
        : craft.PowerShort && !craft.LegacyFreePower ? "no power"
        : craft.CrewShortReason is { } crew ? $"stopped: {crew}"
        : craft.Job is { Active: true } job && job.Status.StartsWith("waiting", StringComparison.Ordinal) ? job.Status
        : NeedsRepair(craft) ? "damaged" : null;

    /// <summary>Raids, then craft without power, without crew, and waiting; by id.</summary>
    public IReadOnlyList<Alert> Alerts()
    {
        var alerts = new List<Alert>();
        foreach (var faction in _factions)
            if (RaidOn(faction) && _craftsById.TryGetValue(faction.RaidGateId, out var gate))
                alerts.Add(new(AlertKind.Raid, TargetRef.Of(gate), $"The {faction.Name} are raiding {gate.Name}."));
        foreach (var craft in _crafts)
        {
            if (!IsPlayers(craft) || craft.IsWreck || AttentionReason(craft) is not { } why) continue;
            var kind = craft.PowerShort && !craft.LegacyFreePower ? AlertKind.Power : craft.CrewShortReason is not null ? AlertKind.Crew : AlertKind.Waiting;
            if (why != "damaged") alerts.Add(new(kind, TargetRef.Of(craft), $"{craft.Name}: {why}."));
        }
        return alerts.OrderBy(a => a.Kind).ThenBy(a => a.Target.Craft.Value).ToList();
    }

    // --- Smart keys ---

    private bool SameSystem(CraftState a, Vector2D b) => StarAt(a.Position)?.Id == StarAt(b)?.Id;

    private IEnumerable<CraftState> NearestCraft(CraftState from, Func<CraftState, bool> which) =>
        _crafts.Where(c => c.Id != from.Id && which(c) && SameSystem(from, c.Position))
            .OrderBy(c => c.Position.DistanceTo(from.Position) - c.CollisionRadiusM).ThenBy(c => c.Id.Value);

    private static List<TargetRef> Refs(IEnumerable<CraftState> crafts) => crafts.Select(TargetRef.Of).ToList();

    /// <summary>Is an enemy shooting (or ready to shoot) at a craft now?</summary>
    private bool Attacks(CraftState enemy, CraftState victim)
    {
        if (!CanBeShot(enemy) || !CanBeShot(victim)) return false;
        foreach (var mount in enemy.Stats.WeaponList)
            if (victim.Position.DistanceTo(enemy.Position) <= mount.Weapon.RangeM + enemy.CollisionRadiusM && PickTarget(enemy, mount, TimeS) == victim) return true;
        return false;
    }

    private CraftState? Guarded(CraftState craft) =>
        craft.Job is DefendJob { Active: true } job && _craftsById.TryGetValue(job.PostId, out var post) ? post : null;

    /// <summary>Whoever attacks you or what you guard, then the other enemies; each nearest first.</summary>
    public TargetChoice EnemyTargets(CraftState from)
    {
        var enemies = NearestCraft(from, c => KindOf(c) == TargetKind.Enemies).ToList();
        if (enemies.Count == 0) return new(Array.Empty<TargetRef>(), _crafts.Any(c => KindOf(c) == TargetKind.Enemies) ? "No enemies in this system." : "No enemies.");
        var guarded = Guarded(from);
        var attackers = enemies.Where(e => Attacks(e, from) || guarded is not null && Attacks(e, guarded)).ToList();
        return new(Refs(attackers.Concat(enemies.Except(attackers))), attackers.Count > 0 ? $"attacking {(attackers.Any(a => Attacks(a, from)) ? "you" : guarded!.Name)}" : "nearest enemy");
    }

    /// <summary>Places with a free port: your shipyards first when damaged, else nearest first.</summary>
    public TargetChoice DockTargets(CraftState from)
    {
        var occupied = new Dictionary<CraftId, int>();
        foreach (var craft in _crafts)
            if (craft.IsDocked) occupied[craft.DockedToCraftId] = occupied.GetValueOrDefault(craft.DockedToCraftId) + 1;
        bool Free(CraftState c) => !c.IsWreck && !IsEnemy(c) && !c.IsDocked && !c.Cruising && c.Transit is null && occupied.GetValueOrDefault(c.Id) < c.Stats.DockingPorts.Count;
        var places = NearestCraft(from, Free).ToList();
        if (places.Count == 0) return new(Array.Empty<TargetRef>(), _crafts.Any(c => c.Id != from.Id && Free(c)) ? "No free docking port in this system." : "No free docking port anywhere.");
        var yards = NeedsRepair(from) ? places.Where(c => c.IsShipyard && IsPlayers(c)).ToList() : new List<CraftState>();
        return new(Refs(yards.Concat(places.Except(yards))), yards.Count > 0 ? "shipyard for repairs" : NeedsRepair(from) ? "free port (no shipyard with one here)" : "free docking port");
    }

    /// <summary>Warp gates of this system, nearest first.</summary>
    public TargetChoice GateTargets(CraftState from)
    {
        var gates = Refs(NearestCraft(from, c => IsGate(c) && !c.IsWreck));
        return new(gates, gates.Count > 0 ? "nearest warp gate" : "No warp gate in this system.");
    }

    /// <summary>Wrecks for a salvage arm, and bodies with the ore at a surveyed site for a drill; nearest first.</summary>
    public TargetChoice WorkTargets(CraftState from, string? ore)
    {
        var (salvages, mines) = (from.Stats.SalvageRateKgPerS > 0, from.Stats.MiningRateKgPerS > 0);
        if (!salvages && !mines) return new(Array.Empty<TargetRef>(), $"{from.Name} has no drill or salvage arm.");
        var found = new List<(double Distance, uint Id, TargetRef Target)>();
        if (salvages)
            foreach (var wreck in NearestCraft(from, c => c.IsWreck))
                found.Add((wreck.Position.DistanceTo(from.Position) - wreck.CollisionRadiusM, wreck.Id.Value, TargetRef.Of(wreck)));
        if (mines)
            foreach (var body in _bodies)
            {
                var at = GetPosition(body.Id);
                if (!(ore is not null ? HasSurveyed(body, ore) : body.SiteList.Any(s => s.Surveyed && s.Deposits.Any(d => d.Value > 0))) || !SameSystem(from, at)) continue;
                found.Add((at.DistanceTo(from.Position) - body.RadiusM, body.Id.Value, TargetRef.Of(body)));
            }
        var targets = found.OrderBy(f => f.Distance).ThenBy(f => f.Id).Select(f => f.Target).ToList();
        var what = string.Join(" or ", new[] { salvages ? "wreck" : "", mines ? ore is null ? "surveyed ore" : $"surveyed {ore.Replace('_', ' ')}" : "" }.Where(w => w.Length > 0));
        return new(targets, targets.Count > 0 ? $"nearest {what}" : $"No {what} in this system.");
    }

    /// <summary>What most likely matters now: a fight, repairs, a full hold or little fuel, then work, then what needs you.</summary>
    public TargetChoice SmartTargets(CraftState from, string? ore)
    {
        var enemies = EnemyTargets(from);
        if (enemies.Targets.Count > 0 && GetCraft(enemies.Targets[0].Craft) is var enemy
            && (enemies.Why.StartsWith("attacking", StringComparison.Ordinal) || enemy.Position.DistanceTo(from.Position) < FightRangeM)) return enemies;
        if (!from.IsDocked)
        {
            var why = NeedsRepair(from) ? null
                : from.HasCargoLimit && from.CargoCapacityKg > 0 && from.CargoKg >= 0.95 * from.CargoCapacityKg ? "the hold is full"
                : from.Stats.TotalThrustN > 0 && from.Inventory[FuelMaterial] < 120 * CruiseFuelKgPerS(from) ? "little cruise fuel" : "";
            if (why != "" && DockTargets(from) is { Targets.Count: > 0 } docks) return why is null ? docks : docks with { Why = $"{why}: {docks.Why}" };
        }
        if (WorkTargets(from, ore) is { Targets.Count: > 0 } work) return work;
        var alerts = Alerts().Where(a => a.Target.Craft != from.Id).ToList();
        if (alerts.Count > 0) return new(alerts.Select(a => a.Target).Distinct().ToList(), "needs you");
        if (!from.IsDocked && DockTargets(from) is { Targets.Count: > 0 } dock) return dock;
        var bodies = TargetsOf(from, TargetKind.Bodies, 20).Targets;
        return new(bodies, bodies.Count > 0 ? "nearest body" : "Nothing to target.");
    }
}
