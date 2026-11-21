using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Converts variable frame time into a whole number of fixed simulation ticks.
/// The simulation only ever advances by whole ticks, so results don't depend on the
/// frame rate. The view uses <see cref="Alpha"/> to interpolate between ticks.
/// </summary>
public sealed class FixedStepClock
{
    /// <summary>Simulation rate. Docs/02 proposes 5–20 ticks/s; 20 is the provisional choice.</summary>
    public const int TicksPerSecond = 20;

    public const double TickSeconds = 1.0 / TicksPerSecond;

    // Absorbs floating-point error so that, e.g., 20 frames of 1/20 s yield exactly 20 ticks.
    private const double Epsilon = 1e-9;

    private double _pendingTicks;

    /// <param name="maxTicksPerAdvance">
    /// Cap on ticks per call, so a long frame (debugger pause, window drag) can't stall the
    /// game catching up. Time beyond the cap is dropped and counted in <see cref="DroppedTicks"/>.
    /// </param>
    public FixedStepClock(int maxTicksPerAdvance = 8)
    {
        if (maxTicksPerAdvance < 1) throw new ArgumentOutOfRangeException(nameof(maxTicksPerAdvance));
        MaxTicksPerAdvance = maxTicksPerAdvance;
    }

    public int MaxTicksPerAdvance { get; }

    /// <summary>Whole ticks of real time skipped because of the cap.</summary>
    public long DroppedTicks { get; private set; }

    /// <summary>Fraction of the next tick already elapsed, in [0, 1). For render interpolation only.</summary>
    public double Alpha => Math.Clamp(_pendingTicks, 0.0, 1.0 - Epsilon);

    /// <summary>Adds real elapsed time and returns how many ticks the caller must run now.</summary>
    public int Advance(double deltaSeconds)
    {
        if (!(deltaSeconds > 0) || double.IsInfinity(deltaSeconds)) return 0;

        _pendingTicks += deltaSeconds * TicksPerSecond;
        var due = (long)Math.Floor(_pendingTicks + Epsilon);
        _pendingTicks = Math.Max(0.0, _pendingTicks - due);

        if (due <= MaxTicksPerAdvance) return (int)due;

        DroppedTicks += due - MaxTicksPerAdvance;
        return MaxTicksPerAdvance;
    }
}
