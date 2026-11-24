namespace SpaceAutomationSandbox.Simulation;

public enum DefendMode
{
    /// <summary>Stay by a station (or any craft that doesn't move).</summary>
    Guard,
    /// <summary>Fly with a ship.</summary>
    Escort,
    /// <summary>Go round a system's planets.</summary>
    Patrol,
}

public enum DefendState
{
    OnStation,
    Engaging,
    Rearming,
    Waiting,
}

/// <summary>
/// Fleet orders (#88): an armed craft guards a station, escorts a ship or patrols a system on its own. It attacks enemies that
/// come near, goes for repairs when badly damaged (as any job does, #3), and for ammunition when a weapon has run out.
/// </summary>
public sealed class DefendJob : ShipJob
{
    /// <summary>Guard a station or escort a ship.</summary>
    public DefendJob(DefendMode mode, CraftId postId)
    {
        Mode = mode;
        PostId = postId;
    }

    /// <summary>Patrol a star's system.</summary>
    public DefendJob(BodyId starId)
    {
        Mode = DefendMode.Patrol;
        StarId = starId;
    }

    public DefendMode Mode { get; }

    /// <summary>What it guards or escorts (None on patrol).</summary>
    public CraftId PostId { get; } = CraftId.None;

    /// <summary>The system it patrols (None otherwise).</summary>
    public BodyId StarId { get; } = BodyId.None;

    public DefendState State { get; internal set; } = DefendState.OnStation;

    /// <summary>Which of the system's planets it's heading for.</summary>
    public int PatrolIndex { get; internal set; }

    /// <summary>The station it's fetching ammunition from.</summary>
    public CraftId RearmAt { get; internal set; } = CraftId.None;

    /// <summary>What it's doing, in words, set by the simulation. Not saved.</summary>
    internal string Doing { get; set; } = "";

    public override string Kind => Mode switch { DefendMode.Guard => "guard", DefendMode.Escort => "escort", _ => "patrol" };

    protected override int StateKey => (int)State * 1_000 + PatrolIndex % 1_000;

    protected override string JobStatus => !Active ? "stopped" : State == DefendState.Waiting ? $"waiting: {WaitReason}" : Doing.Length > 0 ? Doing : "starting";
}
