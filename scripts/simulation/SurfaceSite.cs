using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// One site on a body's rim (#70, decided in #33): an equal arc of the surface with its own deposits.
/// The body's deposits are the sum of its sites; mining takes from the site under the drill. A site is
/// found by survey. Bulk rock or ice (#27) belongs to the whole body, not to a site.
/// </summary>
public sealed class SurfaceSite
{
    internal SurfaceSite(int index, int count, string bodyName)
    {
        Index = index;
        Count = count;
        Name = $"{bodyName} {Letter(index)}";
    }

    public int Index { get; }

    /// <summary>How many sites the body has (they split the rim evenly).</summary>
    public int Count { get; }

    /// <summary>"Rock-1 B".</summary>
    public string Name { get; }

    public bool Surveyed { get; internal set; }

    internal readonly SortedDictionary<string, long> Deposits = new(StringComparer.Ordinal);

    /// <summary>Ore left at this site, whole kg by material.</summary>
    public IReadOnlyDictionary<string, long> DepositsKg => Deposits;

    /// <summary>Arc width in radians.</summary>
    public double WidthRad => 2 * Math.PI / Count;

    /// <summary>Bearing (from the body's centre) where the site starts; sites run anticlockwise from −π.</summary>
    public double StartRad => -Math.PI + Index * WidthRad;

    public double CentreRad => StartRad + WidthRad / 2;

    /// <summary>The bearing nearest to <paramref name="bearingRad"/> inside this site, kept <paramref name="marginRad"/> from its edges.</summary>
    public double Clamp(double bearingRad, double marginRad)
    {
        var margin = Math.Min(marginRad, WidthRad / 2);
        var offset = Math.IEEERemainder(bearingRad - CentreRad, 2 * Math.PI);
        return FlightModel.WrapAngle(CentreRad + Math.Clamp(offset, -WidthRad / 2 + margin, WidthRad / 2 - margin));
    }

    public static string Letter(int index) => index < 26 ? ((char)('A' + index)).ToString() : $"Z{index - 25}";

    // --- Layout (pure functions of the body, so new games and old saves get the same sites) ---

    /// <summary>Target length of a site along the surface.</summary>
    public const double TargetArcM = 2_500;
    public const int MinSites = 3, MaxSites = 24;

    /// <summary>Share of a body's sites that hold each ore (at least one).</summary>
    public const double OreShare = 0.35;

    public static int SiteCount(double radiusM) =>
        Math.Clamp((int)Math.Round(2 * Math.PI * radiusM / TargetArcM), MinSites, MaxSites);

    /// <summary>Which site a bearing falls in.</summary>
    public static int IndexAt(double bearingRad, int count)
    {
        var t = (FlightModel.WrapAngle(bearingRad) + Math.PI) / (2 * Math.PI); // 0..1
        return Math.Clamp((int)Math.Floor(t * count), 0, count - 1);
    }

    /// <summary>
    /// Splits each deposit over about a third of the sites, by a fixed rule from the body id and the
    /// material, in whole kg: the parts add up exactly to the deposit.
    /// </summary>
    public static void Distribute(IReadOnlyList<SurfaceSite> sites, BodyId bodyId, IReadOnlyDictionary<string, long> depositsKg)
    {
        var count = sites.Count;
        foreach (var (material, kg) in depositsKg.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (kg <= 0) continue;
            var rng = new DeterministicRandom((ulong)bodyId.Value * 0x9E3779B97F4A7C15UL ^ NewGame.SeedFromText(material));
            var order = Enumerable.Range(0, count).ToArray();
            for (var i = count - 1; i > 0; i--) // Fisher–Yates
            {
                var j = (int)(rng.NextUInt64() % (ulong)(i + 1));
                (order[i], order[j]) = (order[j], order[i]);
            }
            var holders = order.Take(Math.Max(1, (int)Math.Round(count * OreShare))).OrderBy(i => i).ToArray();
            var weights = holders.Select(_ => 1 + 2 * rng.NextDouble()).ToArray();
            var total = weights.Sum();
            long given = 0;
            var parts = new long[holders.Length];
            for (var i = 0; i < holders.Length; i++)
            {
                parts[i] = (long)Math.Floor(kg * weights[i] / total);
                given += parts[i];
            }
            parts[Array.IndexOf(weights, weights.Max())] += kg - given; // the rounding remainder: the total stays exact
            for (var i = 0; i < holders.Length; i++)
                if (parts[i] > 0) sites[holders[i]].Deposits[material] = sites[holders[i]].Deposits.GetValueOrDefault(material) + parts[i];
        }
    }
}
