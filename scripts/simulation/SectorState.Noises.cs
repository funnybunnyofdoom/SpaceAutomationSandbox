using System.Linq;
namespace SpaceAutomationSandbox.Simulation;

/// <summary>How things are going, for the music (#129).</summary>
public enum MusicMood { Calm, Tension, Combat }

/// <summary>Something that makes a sound (#129): what, where, and when. For the audio only: not saved, and nothing depends on it.</summary>
public readonly record struct Noise(long Tick, string Kind, Vector2D At);

public sealed partial class SectorState
{
    private const int NoiseRing = 256;
    private readonly Noise[] _noises = new Noise[NoiseRing];

    /// <summary>How many noises there have been (the newest is at index NoiseCount - 1, the last <see cref="NoiseRing"/> are kept).</summary>
    public long NoiseCount { get; private set; }

    public Noise NoiseAt(long index) => _noises[index % NoiseRing];

    public static int NoisesKept => NoiseRing;

    private void Make(string kind, Vector2D at) => _noises[NoiseCount++ % NoiseRing] = new Noise(Tick, kind, at);

    /// <summary>What the music should be now: a fight near you, or danger coming; else calm.</summary>
    public MusicMood MoodFor(CraftState pilot)
    {
        var here = StarAt(pilot.Position)?.Id;
        foreach (var craft in Crafts)
            if (!craft.IsWreck && IsEnemy(craft) && craft.Position.DistanceTo(pilot.Position) < FightRangeM) return MusicMood.Combat;
        foreach (var faction in Factions)
            if (!faction.RaidGateId.IsNone && Crafts.FirstOrDefault(c => c.Id == faction.RaidGateId) is { } gate && StarAt(gate.Position)?.Id == here) return MusicMood.Tension;
        foreach (var hazard in Hazards)
            if (hazard.Kind != HazardKind.Comet && hazard.Star == here) return MusicMood.Tension;
        return MusicMood.Calm;
    }
}
