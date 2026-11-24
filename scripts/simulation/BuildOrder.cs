using System;
using System.Collections.Generic;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Stable identifier of a build order (unique per sector, never reused).</summary>
public readonly record struct OrderId(long Value)
{
    public override string ToString() => $"order:{Value}";
}

/// <summary>
/// A craft being built in a shipyard. Its materials were taken from the yard's inventory when the
/// order was accepted and are held here, exactly as consumed, until the craft is finished
/// (they become its parts) or the order is cancelled (a full refund). So inventory + orders +
/// built craft always add up to the same mass.
/// </summary>
public sealed class BuildOrder
{
    public BuildOrder(OrderId id, string shipName, ShipDesign design, ShipStats stats,
        IReadOnlyDictionary<string, long> materialsKg, double progressKg = 0, bool isKit = false)
    {
        if (!(stats.TotalMassKg > 0)) throw new ArgumentException("Order needs a design with mass.", nameof(stats));
        if (!(progressKg >= 0) || progressKg > stats.TotalMassKg) throw new ArgumentOutOfRangeException(nameof(progressKg));
        Id = id;
        ShipName = shipName;
        Design = design;
        Stats = stats;
        MaterialsKg = new SortedDictionary<string, long>(new Dictionary<string, long>(materialsKg), StringComparer.Ordinal);
        ProgressKg = progressKg;
        IsKit = isKit;
    }

    /// <summary>
    /// A station design (#8): it finishes as a <see cref="ConstructionKit"/> in the yard's storage, to
    /// be carried and deployed by a ship, instead of as a craft.
    /// </summary>
    public bool IsKit { get; }

    /// <summary>True while a finished kit waits for room in the yard's storage.</summary>
    public bool WaitingForRoom { get; internal set; }

    /// <summary>The swarm the finished ship joins (#30), or None.</summary>
    public SwarmId SwarmId { get; internal set; } = SwarmId.None;

    public OrderId Id { get; }
    public string ShipName { get; }

    /// <summary>Snapshot taken when ordered; later edits to the blueprint don't change this order.</summary>
    public ShipDesign Design { get; }

    public ShipStats Stats { get; }

    /// <summary>Materials held by this order (removed from the yard's inventory when ordered).</summary>
    public IReadOnlyDictionary<string, long> MaterialsKg { get; }

    /// <summary>Assembled so far, in kg of finished craft (0..TotalKg).</summary>
    public double ProgressKg { get; internal set; }

    public double TotalKg => Stats.TotalMassKg;

    public double Fraction => ProgressKg / TotalKg;
}

/// <summary>Outcome of <see cref="SectorState.OrderBuild"/>: accepted with an id, or rejected with reasons.</summary>
public sealed record BuildOrderResult(
    bool Accepted,
    OrderId? OrderId,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, long> ShortfallKg)
{
    public static BuildOrderResult Rejected(params string[] errors) =>
        new(false, null, errors, new Dictionary<string, long>());
}
