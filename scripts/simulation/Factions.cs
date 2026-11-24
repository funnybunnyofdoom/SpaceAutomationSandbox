using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>A law the player can switch on (#36). Effects are applied by the simulation (see <see cref="Laws"/>).</summary>
public sealed record LawDefinition(string Id, string Name, string Effect);

/// <summary>The laws, in the order the Laws panel lists them (#36, chosen by the creator).</summary>
public static class Laws
{
    public const string RespectClaims = "respect_claims";
    public const string NoDismantling = "no_dismantling";
    public const string OrganicCrews = "organic_crews";
    public const string ArmedGates = "armed_gates";
    public const string ProtectLife = "protect_life";

    public static readonly IReadOnlyList<LawDefinition> All = new LawDefinition[]
    {
        new(RespectClaims, "Respect claimed systems", "Your craft don't mine bodies in a system a civilization claims."),
        new(NoDismantling, "No planet dismantling", "No bulk rock or ice mining anywhere: ore deposits only."),
        new(OrganicCrews, "Organic crews only", "Robot bays don't count as crew."),
        new(ArmedGates, "Armed gates", "Gate defense batteries fire at raiders near their gate."),
        new(ProtectLife, "Protect native life", "No mining or harvesting on sites where native plants grow."),
    };

    public static LawDefinition Get(string id) => All.First(l => l.Id == id);
}

/// <summary>
/// What a civilization values (#36): how much it likes (+) or dislikes (−) each law, −2..+2. Its attitude to
/// the player drifts towards what it thinks of the laws in force.
/// </summary>
public sealed record Culture(string Id, string Name, IReadOnlyDictionary<string, int> Stances)
{
    public static readonly IReadOnlyList<Culture> All = new Culture[]
    {
        new("territorial", "territorial", new Dictionary<string, int> { [Laws.RespectClaims] = 2, [Laws.ArmedGates] = -1 }),
        new("conservationist", "conservationist", new Dictionary<string, int> { [Laws.NoDismantling] = 2, [Laws.RespectClaims] = 1, [Laws.ProtectLife] = 2 }),
        new("purist", "purist", new Dictionary<string, int> { [Laws.OrganicCrews] = 2, [Laws.NoDismantling] = -1 }),
        new("pacifist", "pacifist", new Dictionary<string, int> { [Laws.ArmedGates] = -2, [Laws.RespectClaims] = 1, [Laws.ProtectLife] = 1 }),
        new("militarist", "militarist", new Dictionary<string, int> { [Laws.ArmedGates] = 2, [Laws.OrganicCrews] = -1 }),
    };

    public static Culture Get(string id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];

    public int StanceOn(string lawId) => Stances.TryGetValue(lawId, out var s) ? s : 0;
}

public enum Disposition { Hostile, Wary, Neutral, Friendly }

/// <summary>
/// Another civilization (#36), met when a warp gate opens a system it claims. It never follows you through
/// gates. Attitude runs −100 (hostile) to +100 (friendly): it drifts towards a target set by its culture's view
/// of your laws, minus grievances (mining in its system), which fade over time.
/// </summary>
public sealed class Faction
{
    public Faction(int id, string name, string cultureId, BodyId homeStarId, double baseline)
    {
        Id = id;
        Name = name;
        Culture = Culture.Get(cultureId);
        HomeStarId = homeStarId;
        Baseline = baseline;
        Attitude = baseline;
    }

    public int Id { get; }
    public string Name { get; }
    public Culture Culture { get; }

    /// <summary>The star of the system it claims.</summary>
    public BodyId HomeStarId { get; }

    /// <summary>Its attitude with no laws in force and no grievances.</summary>
    public double Baseline { get; }

    public double Attitude { get; internal set; }

    /// <summary>Points taken off its target attitude for mining in its system; fades over time.</summary>
    public double Grievance { get; internal set; }

    /// <summary>Tick of its next decision (raid, warning).</summary>
    public long NextEventTick { get; internal set; }

    /// <summary>The gate its raiders are attacking now (#84), or None, and when they give up. Saved.</summary>
    public CraftId RaidGateId { get; internal set; } = CraftId.None;
    public long RaidEndTick { get; internal set; }

    /// <summary>When its patrol may get its next ship, and how many it has had so far (#89). Saved.</summary>
    public long NextPatrolTick { get; internal set; }
    public int PatrolShipsBuilt { get; internal set; }

    /// <summary>Goodwill earned by trade and gifts (#89): added to its target attitude; fades slowly. Saved.</summary>
    public double Goodwill { get; internal set; }

    /// <summary>Until then it isn't hostile, after peace (#89), and when a lost trade station may be rebuilt. Saved.</summary>
    public long TruceUntilTick { get; internal set; }
    public long NextStationTick { get; internal set; }

    /// <summary>What it thinks of how you treat living worlds (#94), if it cares. Worked out every second; not saved.</summary>
    public double LifeOpinion { get; internal set; }

    public const double MaxGoodwill = 40, GoodwillFadePerMinute = 0.05;

    public const double HostileBelow = -30, FriendlyFrom = 30;
    public const double PointsPerStance = 15;

    public Disposition Disposition => Attitude < HostileBelow ? Disposition.Hostile
        : Attitude < 0 ? Disposition.Wary
        : Attitude < FriendlyFrom ? Disposition.Neutral
        : Disposition.Friendly;

    /// <summary>Where its attitude is heading with these laws in force.</summary>
    public double TargetAttitude(IReadOnlyCollection<string> laws) =>
        Math.Clamp(Baseline + laws.Sum(l => Culture.StanceOn(l)) * PointsPerStance - Grievance + Goodwill + LifeOpinion, -100, 100);
}

/// <summary>Something that happened, for the player's log and status line (#36).</summary>
public sealed record SectorEvent(long Tick, string Text);
