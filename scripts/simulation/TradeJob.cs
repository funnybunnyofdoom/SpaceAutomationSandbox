namespace SpaceAutomationSandbox.Simulation;

public enum TradeJobState
{
    ToHome,
    AtHome,
    ToTrader,
    Trading,
    Waiting,
}

/// <summary>
/// A trade route (#89): loads one material at a station of yours, barters it at a civilization's trade station for another, and brings
/// that home. Repeats, through warp gates.
/// </summary>
public sealed class TradeJob : ShipJob
{
    public TradeJob(CraftId homeId, string giveMaterial, CraftId traderId, string getMaterial) : base(getMaterial, homeId)
    {
        GiveMaterial = giveMaterial;
        TraderId = traderId;
    }

    /// <summary>Where it loads and brings the goods back to (the job's destination).</summary>
    public CraftId HomeId => DestinationId;

    public string GiveMaterial { get; }

    /// <summary>What it brings back (the job's material).</summary>
    public string GetMaterial => MaterialId;

    public CraftId TraderId { get; }

    public TradeJobState State { get; internal set; } = TradeJobState.ToHome;
    public TradeJobState ResumeState { get; internal set; } = TradeJobState.ToHome;

    public override string Kind => "trade";

    protected override int StateKey => (int)State;

    protected override string JobStatus => !Active ? "stopped" : State switch
    {
        TradeJobState.Waiting => $"waiting: {WaitReason}",
        TradeJobState.ToHome => "flying home",
        TradeJobState.AtHome => "unloading and loading",
        TradeJobState.ToTrader => "flying to the trade station",
        _ => "trading",
    };
}
