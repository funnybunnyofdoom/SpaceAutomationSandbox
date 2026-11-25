using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// A job that drives a craft (data, not code). Common parts: what it moves, where to, whether it
/// runs, progress counters, and why it's waiting. Run by SectorState.StepJobs.
/// </summary>
public abstract class ShipJob
{
    protected ShipJob(string materialId, CraftId destinationId)
    {
        if (destinationId.IsNone) throw new ArgumentException("Job needs a destination.", nameof(destinationId));
        _materialId = materialId;
        _destinationId = destinationId;
    }

    /// <summary>For jobs that pick what and where per trip (<see cref="ServeRequestsJob"/>).</summary>
    protected ShipJob()
    {
        _materialId = "";
        _destinationId = CraftId.None;
    }

    private readonly string _materialId;
    private readonly CraftId _destinationId;

    /// <summary>What it moves ("" when the job hasn't picked anything yet).</summary>
    public virtual string MaterialId => _materialId;

    /// <summary>Where it delivers (None when the job hasn't picked anything yet).</summary>
    public virtual CraftId DestinationId => _destinationId;

    public bool Active { get; internal set; } = true;

    /// <summary>Why the job is waiting, or null when it isn't.</summary>
    public string? WaitReason { get; internal set; }

    /// <summary>Completed cycles (mine or pick up → deliver).</summary>
    public int CompletedCycles { get; internal set; }

    /// <summary>Total kg unloaded at the destination.</summary>
    public long DeliveredKg { get; internal set; }

    /// <summary>While waiting: don't retry before this tick (0 = retry every tick).</summary>
    public long RetryAtTick { get; internal set; }

    /// <summary>What it is doing now, in plain words (with the wait reason when blocked); servicing (#3) shows instead while it happens.</summary>
    public string Status => ServiceStatus ?? JobStatus;

    /// <summary>The job's own status.</summary>
    protected abstract string JobStatus { get; }

    /// <summary>While the craft is away for repairs (#3): what it's doing. Set each tick by the simulation; not saved.</summary>
    public string? ServiceStatus { get; internal set; }

    /// <summary>Short kind name for UI and saves.</summary>
    public abstract string Kind { get; }

    // --- Recent history (#19): what the job did lately, for "why did it stop?" questions. ---

    /// <summary>How many recent events a job keeps.</summary>
    public const int HistoryLength = 12;

    private readonly Queue<JobEvent> _history = new();
    private int _lastKey = int.MinValue;
    private string? _lastWait;

    /// <summary>Recent state changes, oldest first. Kept in memory only (not saved).</summary>
    public IReadOnlyCollection<JobEvent> History => _history;

    /// <summary>A wait in words; recharging is part of the round, not something to wait for, so it needs nobody.</summary>
    protected static string Waits(string? why) => why is not null && why.StartsWith("recharging", StringComparison.Ordinal) ? why : $"waiting: {why}";

    /// <summary>A number that changes whenever the job's state (or trip) changes; wait reasons are compared separately.</summary>
    protected abstract int StateKey { get; }

    /// <summary>Called once per tick after the job ran: logs the new status when the state or wait reason changed. Cheap otherwise.</summary>
    internal void NoteTick(long tick)
    {
        var key = !Active ? -1 : ServiceStatus is not null ? -2 : StateKey;
        var wait = ServiceStatus is { } service && service.StartsWith("waiting", StringComparison.Ordinal) ? service : WaitReason;
        if (key == _lastKey && string.Equals(wait, _lastWait, StringComparison.Ordinal)) return;
        _lastKey = key;
        _lastWait = wait;
        if (_history.Count == HistoryLength) _history.Dequeue();
        _history.Enqueue(new JobEvent(tick, Status));
    }
}

/// <summary>One line of a job's recent history: when (tick) and what it was doing from then on.</summary>
public readonly record struct JobEvent(long Tick, string Text);

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
/// Repeatable mining: fly to <see cref="SourceBodyId"/>, land, anchor, mine <see cref="ShipJob.MaterialId"/>
/// until the hold is <see cref="ReturnFraction"/> full (or the deposit runs out), fly to the destination,
/// dock, unload, repeat.
/// </summary>
public sealed class MiningJob : ShipJob
{
    public MiningJob(BodyId sourceBodyId, string materialId, CraftId destinationId, double returnFraction)
        : base(materialId, destinationId)
    {
        if (sourceBodyId.IsNone) throw new ArgumentException("Job needs a source body.", nameof(sourceBodyId));
        if (!(returnFraction > 0) || returnFraction > 1) throw new ArgumentOutOfRangeException(nameof(returnFraction));
        SourceBodyId = sourceBodyId;
        ReturnFraction = returnFraction;
    }

    public BodyId SourceBodyId { get; }

    /// <summary>Head home when cargo reaches this fraction of capacity (0–1].</summary>
    public double ReturnFraction { get; }

    public MiningJobState State { get; internal set; } = MiningJobState.ToSource;

    /// <summary>State to resume after a wait clears.</summary>
    public MiningJobState ResumeState { get; internal set; } = MiningJobState.ToSource;

    /// <summary>
    /// This trip's landing spot on the source (#55): a bearing from the body's centre, picked on the
    /// side the craft comes from, nudged per craft so miners land apart. Null until picked; reset each trip.
    /// </summary>
    public double? SpotBearingRad { get; internal set; }

    public override string Kind => "mining";

    protected override int StateKey => (int)State;

    protected override string JobStatus => !Active ? "stopped"
        : State == MiningJobState.Waiting ? Waits(WaitReason)
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

/// <summary>Where a freight job is in its cycle.</summary>
public enum HaulJobState
{
    ToPickup,
    Loading,
    ToDropoff,
    Unloading,
    Waiting,
}

/// <summary>
/// Repeatable freight: dock at <see cref="PickupId"/>, load <see cref="ShipJob.MaterialId"/> once at least
/// <see cref="MinPickupKg"/> is there (as much as fits), fly to the destination, dock, unload, repeat.
/// With <see cref="KeepStockedKg"/> &gt; 0 it only delivers until the destination holds that much
/// ("keep stocked"), then waits there with the rest. Replaces the player hauling goods by hand.
/// </summary>
public sealed class HaulJob : ShipJob
{
    public HaulJob(CraftId pickupId, string materialId, CraftId destinationId, long minPickupKg, long keepStockedKg)
        : base(materialId, destinationId)
    {
        if (pickupId.IsNone) throw new ArgumentException("Job needs a pickup station.", nameof(pickupId));
        if (minPickupKg < 1) throw new ArgumentOutOfRangeException(nameof(minPickupKg));
        if (keepStockedKg < 0) throw new ArgumentOutOfRangeException(nameof(keepStockedKg));
        PickupId = pickupId;
        MinPickupKg = minPickupKg;
        KeepStockedKg = keepStockedKg;
    }

    public CraftId PickupId { get; }

    /// <summary>Wait at the pickup until at least this much is there (avoids hauling scraps).</summary>
    public long MinPickupKg { get; }

    /// <summary>0 = deliver everything; otherwise deliver only until the destination holds this much.</summary>
    public long KeepStockedKg { get; }

    public HaulJobState State { get; internal set; } = HaulJobState.ToPickup;
    public HaulJobState ResumeState { get; internal set; } = HaulJobState.ToPickup;

    public override string Kind => "freight";

    protected override int StateKey => (int)State;

    protected override string JobStatus => !Active ? "stopped"
        : State == HaulJobState.Waiting ? Waits(WaitReason)
        : State switch
        {
            HaulJobState.ToPickup => "flying to the pickup",
            HaulJobState.Loading => "loading",
            HaulJobState.ToDropoff => "flying to the destination",
            HaulJobState.Unloading => "unloading",
            _ => State.ToString(),
        };
}
