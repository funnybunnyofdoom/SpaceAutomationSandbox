using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Where a mining job is in its cycle.</summary>
public enum MiningJobState
{
    ToSource,
    Landing,
    Mining,
    Lifting,
    ToDestination,
    Unloading,
    Waiting,
}

/// <summary>
/// A repeatable mining job (data, not code): fly to <see cref="SourceBodyId"/>, land, anchor, mine
/// <see cref="MaterialId"/> until the hold is <see cref="ReturnFraction"/> full (or the deposit
/// runs out), fly to <see cref="DestinationId"/>, dock, unload, repeat. Always says what it's doing
/// (<see cref="Status"/>) and, when blocked, why (<see cref="WaitReason"/>). Run by SectorState.StepJobs.
/// </summary>
public sealed class MiningJob
{
    public MiningJob(BodyId sourceBodyId, string materialId, CraftId destinationId, double returnFraction)
    {
        if (sourceBodyId.IsNone) throw new ArgumentException("Job needs a source body.", nameof(sourceBodyId));
        if (destinationId.IsNone) throw new ArgumentException("Job needs a destination.", nameof(destinationId));
        if (!(returnFraction > 0) || returnFraction > 1) throw new ArgumentOutOfRangeException(nameof(returnFraction));
        SourceBodyId = sourceBodyId;
        MaterialId = materialId;
        DestinationId = destinationId;
        ReturnFraction = returnFraction;
    }

    public BodyId SourceBodyId { get; }
    public string MaterialId { get; }
    public CraftId DestinationId { get; }

    /// <summary>Head home when cargo reaches this fraction of capacity (0–1].</summary>
    public double ReturnFraction { get; }

    public bool Active { get; internal set; } = true;
    public MiningJobState State { get; internal set; } = MiningJobState.ToSource;

    /// <summary>State to resume after a wait clears.</summary>
    public MiningJobState ResumeState { get; internal set; } = MiningJobState.ToSource;

    /// <summary>Why the job is waiting, or null when it isn't.</summary>
    public string? WaitReason { get; internal set; }

    /// <summary>Completed mine → deliver cycles.</summary>
    public int CompletedCycles { get; internal set; }

    /// <summary>Total kg unloaded at the destination.</summary>
    public long DeliveredKg { get; internal set; }

    public string Status => !Active ? "stopped"
        : State == MiningJobState.Waiting ? $"waiting: {WaitReason}"
        : State switch
        {
            MiningJobState.ToSource => "flying to the source",
            MiningJobState.Landing => "landing",
            MiningJobState.Mining => "mining",
            MiningJobState.Lifting => "lifting off",
            MiningJobState.ToDestination => "flying to the destination",
            MiningJobState.Unloading => "unloading",
            _ => State.ToString(),
        };
}
