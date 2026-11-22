using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// A station packed for transport (#8). A shipyard builds a station design into a kit instead of a
/// finished structure; the kit sits in the yard's storage (it takes cargo room equal to its mass),
/// a ship with cargo bays carries it, and deploys it where the station should stand: on a body's
/// surface while landed, or in orbit. The kit holds exactly the materials of the order it came
/// from, and the deployed station is built from them, so mass is conserved end to end.
/// </summary>
public sealed class ConstructionKit
{
    public ConstructionKit(OrderId id, string name, ShipDesign design, ShipStats stats, IReadOnlyDictionary<string, long> materialsKg)
    {
        if (materialsKg.Count == 0 || materialsKg.Values.Any(kg => kg <= 0))
            throw new ArgumentException("A kit needs positive materials.", nameof(materialsKg));
        Id = id;
        Name = name;
        Design = design;
        Stats = stats;
        MaterialsKg = new SortedDictionary<string, long>(new Dictionary<string, long>(materialsKg), StringComparer.Ordinal);
        MassKg = MaterialsKg.Values.Sum();
    }

    /// <summary>The id of the build order that made it (unique per sector).</summary>
    public OrderId Id { get; }

    /// <summary>Name the deployed station gets.</summary>
    public string Name { get; }

    public ShipDesign Design { get; }
    public ShipStats Stats { get; }

    public IReadOnlyDictionary<string, long> MaterialsKg { get; }

    /// <summary>Whole kg; the cargo room it takes and the mass it adds to whoever carries it.</summary>
    public long MassKg { get; }

    /// <summary>A fixed structure (foundation) can only be deployed while landed.</summary>
    public bool NeedsSurface => Stats.IsFixedStructure;
}
