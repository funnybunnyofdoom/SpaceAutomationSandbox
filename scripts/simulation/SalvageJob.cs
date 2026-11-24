namespace SpaceAutomationSandbox.Simulation;

public enum SalvageJobState
{
    ToWreck,
    Cutting,
    ToDestination,
    Unloading,
    Waiting,
}

/// <summary>
/// Automated salvage (#86): flies to the nearest wreck (through warp gates if it's in another system), cuts it up into its
/// hold, delivers, and goes on until there are no wrecks left.
/// </summary>
public sealed class SalvageJob : ShipJob
{
    public SalvageJob(CraftId destinationId) : base("", destinationId) { }

    public SalvageJobState State { get; internal set; } = SalvageJobState.ToWreck;

    /// <summary>The wreck it's working on (None until picked). Saved.</summary>
    public CraftId WreckId { get; internal set; } = CraftId.None;

    public override string Kind => "salvage";

    protected override int StateKey => (int)State * 100_000 + (int)(WreckId.Value % 100_000);

    protected override string JobStatus => !Active ? "stopped"
        : State switch
        {
            SalvageJobState.Waiting => $"waiting: {WaitReason}",
            SalvageJobState.ToWreck => "flying to a wreck",
            SalvageJobState.Cutting => "cutting up a wreck",
            SalvageJobState.ToDestination => "flying to the destination",
            _ => "unloading",
        };
}
