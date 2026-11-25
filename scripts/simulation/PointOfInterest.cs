namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// A place worth finding (#122). Until it's found only this exists: the derelict's craft or the anomaly's body is made then,
/// from <see cref="Roll"/>, so what's hidden costs nothing and the same seed holds the same things.
/// </summary>
public sealed class PointOfInterest
{
    public PointOfInterest(int id, string kindId, string name, BodyId parent, CircularOrbit? orbit, double bearingRad, ulong roll)
    {
        (Id, KindId, Name, Parent, Orbit, BearingRad, Roll) = (id, kindId, name, parent, orbit, bearingRad, roll);
    }

    public int Id { get; }
    public string KindId { get; }
    public string Name { get; }

    /// <summary>What it orbits, or is on.</summary>
    public BodyId Parent { get; }

    /// <summary>Null for a ruin: it's on the ground at <see cref="BearingRad"/>.</summary>
    public CircularOrbit? Orbit { get; }
    public double BearingRad { get; }
    public ulong Roll { get; }

    public bool Found { get; internal set; }

    /// <summary>Ticks of studying or scanning done.</summary>
    public int StudyTicks { get; internal set; }

    /// <summary>Studied or scanned: what's there is known.</summary>
    public bool Studied { get; internal set; }

    /// <summary>Nothing more to decide here.</summary>
    public bool Done { get; internal set; }

    /// <summary>What came of it, once something has.</summary>
    public string Result { get; internal set; } = "";

    /// <summary>An energy field left as it is.</summary>
    public bool FieldKept { get; internal set; }

    /// <summary>The derelict, or the anomaly's body, once found.</summary>
    public CraftId CraftId { get; internal set; } = CraftId.None;
    public BodyId BodyId { get; internal set; } = BodyId.None;
}

/// <summary>Something that can be done at a point of interest, and why it can't be now.</summary>
public sealed record PointChoice(string Id, string Text, string Detail, string? Blocker = null);
