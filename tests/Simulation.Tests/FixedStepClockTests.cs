using System.Linq;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class FixedStepClockTests
{
    private static long RunFrames(double[] frameDeltas, int cap = 1000)
    {
        var clock = new FixedStepClock(cap);
        return frameDeltas.Sum(d => (long)clock.Advance(d));
    }

    [Fact]
    public void SameRealTime_YieldsSameTicks_AtAnyFrameRate()
    {
        // 30 seconds of real time at 20 Hz = 600 ticks, whatever the frame pacing.
        var at60Fps = Enumerable.Repeat(1.0 / 60, 60 * 30).ToArray();
        var at144Fps = Enumerable.Repeat(1.0 / 144, 144 * 30).ToArray();
        var at20Fps = Enumerable.Repeat(1.0 / 20, 20 * 30).ToArray();

        Assert.Equal(600, RunFrames(at60Fps));
        Assert.Equal(600, RunFrames(at144Fps));
        Assert.Equal(600, RunFrames(at20Fps));
    }

    [Fact]
    public void IrregularFrames_YieldExpectedTicks()
    {
        // Deterministic jittery frames between 5 and 45 ms; total time decides the tick count.
        var rng = new DeterministicRandom(7);
        var frames = Enumerable.Range(0, 5000).Select(_ => rng.Range(0.005, 0.045)).ToArray();
        var expected = (long)System.Math.Floor(frames.Sum() * FixedStepClock.TicksPerSecond + 1e-6);

        Assert.InRange(RunFrames(frames), expected - 1, expected);
    }

    [Fact]
    public void LongFrame_IsCapped_AndDroppedTimeIsCounted()
    {
        var clock = new FixedStepClock(maxTicksPerAdvance: 8);

        Assert.Equal(8, clock.Advance(2.0)); // 2 s would be 40 ticks
        Assert.Equal(32, clock.DroppedTicks);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDelta_RunsNoTicks(double delta)
    {
        Assert.Equal(0, new FixedStepClock().Advance(delta));
    }

    [Fact]
    public void Alpha_StaysWithinOneTick()
    {
        var clock = new FixedStepClock();
        clock.Advance(FixedStepClock.TickSeconds * 2.5);

        Assert.InRange(clock.Alpha, 0.49, 0.51);
    }
}
