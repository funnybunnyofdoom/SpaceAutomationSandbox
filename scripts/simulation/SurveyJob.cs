namespace SpaceAutomationSandbox.Simulation;

public enum SurveyJobState
{
    Flying,
    Waiting,
}

/// <summary>
/// Automated scouting (#4): the craft flies to the nearest unsurveyed body and surveys it by the same
/// rule as the player (coming within <see cref="SectorState.SurveyRangeM"/> of its surface), then the
/// next, until every body is surveyed. Waits, with the reason, when there is nothing left.
/// <see cref="ShipJob.CompletedCycles"/> counts the bodies this scout surveyed (including ones it passed on the way).
/// </summary>
public sealed class SurveyJob : ShipJob
{
    public SurveyJobState State { get; internal set; } = SurveyJobState.Flying;

    /// <summary>The body it is heading for (None until picked). Saved, so a loaded game flies on to the same one.</summary>
    public BodyId TargetBodyId { get; internal set; } = BodyId.None;

    /// <summary>Name of the target, for the status line (set by the simulation).</summary>
    internal string TargetName { get; set; } = "";

    public override string Kind => "survey";

    protected override int StateKey => (int)State * 100_000 + (int)(TargetBodyId.Value % 100_000);

    protected override string JobStatus => !Active ? "stopped"
        : State == SurveyJobState.Waiting ? $"waiting: {WaitReason}"
        : TargetBodyId.IsNone ? "looking for an unsurveyed body"
        : $"flying to survey {TargetName}";
}
