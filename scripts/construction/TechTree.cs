using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Construction;

/// <summary>Where a tech goes in the drawn tree (#104): its row (how many techs deep it is) and its place in that row.</summary>
public readonly record struct TechPlace(TechDefinition Tech, int Row, int Column);

/// <summary>Lays the tech tree out in rows, each tech under what it needs, near the middle of it.</summary>
public static class TechTree
{
    public static IReadOnlyList<TechPlace> Layout(ResearchCatalog catalog)
    {
        var row = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tech in catalog.Techs) // requirements come earlier in the file
            row[tech.Id] = (tech.Requires ?? new List<string>()).Select(r => row[r] + 1).DefaultIfEmpty(0).Max();
        var places = new List<TechPlace>();
        var centre = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in catalog.Techs.Select((tech, index) => (tech, index)).GroupBy(t => row[t.tech.Id]).OrderBy(g => g.Key))
        {
            // The first row by what's built on it, so relatives sit together; the rest by where what they need is.
            double Wanted((TechDefinition tech, int index) t) =>
                t.tech.Requires is { Count: > 0 } needs ? needs.Average(n => centre[n]) : t.index;
            var ordered = group.OrderBy(Wanted).ThenBy(t => t.index).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                places.Add(new(ordered[i].tech, group.Key, i));
                centre[ordered[i].tech.Id] = (i + 0.5) / ordered.Count;
            }
        }
        return places;
    }
}
