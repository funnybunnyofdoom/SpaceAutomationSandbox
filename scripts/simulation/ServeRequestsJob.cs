using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// One trip a "serve requests" freighter has claimed (#1): carry <see cref="AmountKg"/> of a material
/// from <see cref="PickupId"/> to <see cref="DestinationId"/>. While it exists it is a reservation:
/// the amount counts as already on its way to the destination, and (until loaded) as already taken
/// from the pickup, so no two freighters claim the same cargo.
/// </summary>
public sealed record HaulContract(CraftId PickupId, CraftId DestinationId, string MaterialId, long AmountKg)
{
    public HaulContract Validated()
    {
        if (PickupId.IsNone || DestinationId.IsNone || PickupId == DestinationId) throw new ArgumentException("A contract needs two different stations.");
        if (AmountKg <= 0) throw new ArgumentOutOfRangeException(nameof(AmountKg));
        if (string.IsNullOrEmpty(MaterialId)) throw new ArgumentException("A contract needs a material.");
        return this;
    }
}

/// <summary>
/// Freight that serves station requests (#1) instead of a fixed route: whenever it has no contract it
/// looks for a station that wants more of something ("keep at least X t") and another station with
/// spare stock of it, claims a trip (a <see cref="HaulContract"/>), and runs it with the same flying,
/// docking and transfers as a fixed-route freight job. Waits, with the reason, when nothing needs
/// moving. Reservations cover cargo and docking ports, so freighters don't double-claim either.
/// </summary>
public sealed class ServeRequestsJob : ShipJob
{
    /// <param name="minLoadKg">Don't make a trip for less than this (unless the request itself is smaller).</param>
    public ServeRequestsJob(long minLoadKg)
    {
        if (minLoadKg < 1) throw new ArgumentOutOfRangeException(nameof(minLoadKg));
        MinLoadKg = minLoadKg;
    }

    public long MinLoadKg { get; }

    /// <summary>The trip claimed now, or null while looking for one.</summary>
    public HaulContract? Contract { get; internal set; }

    /// <summary>Leg of the current contract (ToPickup also means "looking" when there is none).</summary>
    public HaulJobState State { get; internal set; } = HaulJobState.ToPickup;
    public HaulJobState ResumeState { get; internal set; } = HaulJobState.ToPickup;

    /// <summary>True while the contract's cargo is still at the pickup (it reserves stock there).</summary>
    public bool BeforePickup =>
        (State == HaulJobState.Waiting ? ResumeState : State) is HaulJobState.ToPickup or HaulJobState.Loading;

    public override string MaterialId => Contract?.MaterialId ?? "";
    public override CraftId DestinationId => Contract?.DestinationId ?? CraftId.None;

    public override string Kind => "requests";

    protected override int StateKey => (int)State * 2 + (Contract is null ? 0 : 1);

    protected override string JobStatus => !Active ? "stopped"
        : State == HaulJobState.Waiting ? $"waiting: {WaitReason}"
        : Contract is not { } c ? "looking for a request to serve"
        : State switch
        {
            HaulJobState.ToPickup => $"flying to pick up {c.AmountKg / 1000.0:0.0} t of {c.MaterialId}",
            HaulJobState.Loading => "loading",
            HaulJobState.ToDropoff => $"delivering {c.AmountKg / 1000.0:0.0} t of {c.MaterialId}",
            HaulJobState.Unloading => "unloading",
            _ => State.ToString(),
        };
}
