using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Time acceleration (#18). The simulation only ever advances in whole fixed ticks, so any speed
/// gives exactly the same results as 1×: warp only changes how many ticks run per real second.
/// The view asks <see cref="TicksDue"/> each frame, runs ticks until they're done or its frame
/// budget is spent, and reports back with <see cref="RecordFrame"/>. Ticks it couldn't fit are
/// skipped (time runs slower), never queued, so a heavy sector can't freeze the game; the HUD
/// shows requested vs actual speed. Pause is speed 0. No Godot types: testable headless.
/// </summary>
public sealed class TimeWarp
{
    /// <summary>Speeds the faster/slower keys step through.</summary>
    public static readonly int[] Levels = { 1, 2, 5, 10, 25, 50, 100 };

    public const int MaxSpeed = 1_000;

    /// <summary>Real time the view may spend simulating per frame before it skips the rest.</summary>
    public const double FrameBudgetMs = 20;

    /// <summary>Ticks a frame may ask for at 1× (absorbs a slow frame without dropping time).</summary>
    private const int BaseTicksPerFrame = 8;

    private readonly FixedStepClock _clock = new(BaseTicksPerFrame);
    private int _speed = 1;
    private double _actualTicksPerSecond = FixedStepClock.TicksPerSecond;

    /// <summary>Requested speed (1..<see cref="MaxSpeed"/>), kept while paused.</summary>
    public int Speed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, 1, MaxSpeed);
            _clock.MaxTicksPerAdvance = BaseTicksPerFrame * _speed;
        }
    }

    public bool Paused { get; set; }

    /// <summary>For render interpolation between ticks.</summary>
    public double Alpha => _clock.Alpha;

    /// <summary>Speed actually achieved recently (smoothed; 0 while paused).</summary>
    public double ActualSpeed => Paused ? 0 : _actualTicksPerSecond / FixedStepClock.TicksPerSecond;

    /// <summary>True when recent frames had to skip ticks to stay within the budget.</summary>
    public bool Limited { get; private set; }

    /// <summary>Ticks skipped so far because of the frame budget (time that ran slower than asked).</summary>
    public long SkippedTicks { get; private set; }

    public void Faster()
    {
        foreach (var level in Levels)
            if (level > _speed) { Speed = level; return; }
    }

    public void Slower()
    {
        for (var i = Levels.Length - 1; i >= 0; i--)
            if (Levels[i] < _speed) { Speed = Levels[i]; return; }
    }

    /// <summary>Ticks to run for <paramref name="realDeltaS"/> seconds of real time at the current speed.</summary>
    public int TicksDue(double realDeltaS) => Paused ? 0 : _clock.Advance(realDeltaS * _speed);

    /// <summary>Tells the warp how the frame went: <paramref name="ran"/> of <paramref name="due"/> ticks fitted the budget.</summary>
    public void RecordFrame(double realDeltaS, int ran, int due)
    {
        if (!(realDeltaS > 0)) return;
        SkippedTicks += Math.Max(0, due - ran);
        Limited = ran < due;
        // Exponential smoothing over roughly half a second of frames.
        var alpha = Math.Clamp(realDeltaS / 0.5, 0, 1);
        _actualTicksPerSecond += (ran / realDeltaS - _actualTicksPerSecond) * alpha;
    }

    public override string ToString() =>
        Paused ? "PAUSED" : Limited ? $"{_speed}× (running {ActualSpeed:0}×, limited by CPU)" : $"{_speed}×";
}

/// <summary>Rolling cost of recent simulation ticks, measured by the view (#19 diagnostics).</summary>
public sealed class TickCostMeter
{
    private readonly double[] _ms;
    private int _next;
    private int _count;

    public TickCostMeter(int window = 200)
    {
        if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
        _ms = new double[window];
    }

    public void Add(double milliseconds)
    {
        _ms[_next] = milliseconds;
        _next = (_next + 1) % _ms.Length;
        if (_count < _ms.Length) _count++;
    }

    public int Count => _count;

    public double AverageMs
    {
        get
        {
            if (_count == 0) return 0;
            double sum = 0;
            for (var i = 0; i < _count; i++) sum += _ms[i];
            return sum / _count;
        }
    }

    public double MaxMs
    {
        get
        {
            double max = 0;
            for (var i = 0; i < _count; i++) max = Math.Max(max, _ms[i]);
            return max;
        }
    }
}
