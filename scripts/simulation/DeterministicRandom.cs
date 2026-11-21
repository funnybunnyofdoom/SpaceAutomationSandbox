namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// SplitMix64 generator. Used instead of System.Random, whose seeded sequence is an
/// implementation detail of .NET and could change between runtime versions.
/// The same seed always produces the same sequence on every platform.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0, 1), using the top 53 bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform double in [min, max).</summary>
    public double Range(double min, double max) => min + (max - min) * NextDouble();
}
