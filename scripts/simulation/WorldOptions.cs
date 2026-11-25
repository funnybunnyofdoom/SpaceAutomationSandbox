using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>How much of something a world has.</summary>
public enum WorldAmount { Off, Rare, Normal, Often }

/// <summary>An option of the New Game screen's Advanced section (#119).</summary>
public sealed record WorldOption(string Id, string Name, string Description);

/// <summary>
/// What a world has more or less of (#119), by option id. It's saved with the game and can be changed at any time, from then on.
/// What isn't listed is off, so a game saved before an option existed plays as it did.
/// </summary>
public sealed class WorldOptions
{
    public const string PointsOfInterest = "points_of_interest";

    /// <summary>The options there are: one is added here and looked at where it counts; the menus and saves follow.</summary>
    public static readonly IReadOnlyList<WorldOption> All = new[]
    {
        new WorldOption(PointsOfInterest, "Points of interest", "Derelicts, ruins and anomalies to find and explore. In a running game, a change counts for star systems opened from then on."),
    };

    private readonly SortedDictionary<string, WorldAmount> _amounts = new(StringComparer.Ordinal);

    /// <summary>What a new game has if nothing is changed.</summary>
    public static WorldOptions Usual() => Every(WorldAmount.Normal);

    public static WorldOptions Every(WorldAmount amount)
    {
        var options = new WorldOptions();
        foreach (var option in All) options[option.Id] = amount;
        return options;
    }

    public WorldAmount this[string id]
    {
        get => _amounts.GetValueOrDefault(id);
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == WorldAmount.Off) _amounts.Remove(id); else _amounts[id] = value;
        }
    }

    /// <summary>What isn't off, by id (what's saved). An option this build doesn't have is kept.</summary>
    public IReadOnlyDictionary<string, WorldAmount> Amounts => _amounts;

    public WorldOptions Copy()
    {
        var copy = new WorldOptions();
        foreach (var (id, amount) in _amounts) copy._amounts[id] = amount;
        return copy;
    }

    public bool IsUsual => All.All(o => this[o.Id] == WorldAmount.Normal);

    /// <summary>One line: what differs from usual.</summary>
    public string Summary()
    {
        var changed = All.Where(o => this[o.Id] != WorldAmount.Normal).ToList();
        if (changed.Count == 0) return "everything as usual";
        return string.Join(", ", changed.Select(o => $"{o.Name}: {this[o.Id].ToString().ToLowerInvariant()}"));
    }
}
