using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Authoritative material store of one craft or facility, in whole kilograms (docs/06). Amounts
/// are never negative. Changes are all-or-nothing, so a failed transfer can't leave half a bill
/// removed. There is no capacity limit yet (storage parts come later).
/// </summary>
public sealed class Inventory
{
    private readonly SortedDictionary<string, long> _kg = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Kg => _kg;

    public long TotalKg => _kg.Values.Sum();

    public long this[string materialId] => _kg.TryGetValue(materialId, out var kg) ? kg : 0;

    public void Add(string materialId, long kg)
    {
        if (kg < 0) throw new ArgumentOutOfRangeException(nameof(kg), "Use TryRemove to take material out.");
        if (kg == 0) return;
        _kg[materialId] = checked(this[materialId] + kg);
    }

    public void Add(IReadOnlyDictionary<string, long> amounts)
    {
        foreach (var (id, kg) in amounts) Add(id, kg);
    }

    /// <summary>What is missing to cover <paramref name="amounts"/>; empty when everything is in stock.</summary>
    public SortedDictionary<string, long> Shortfall(IReadOnlyDictionary<string, long> amounts)
    {
        var missing = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (id, kg) in amounts)
            if (kg > this[id]) missing[id] = kg - this[id];
        return missing;
    }

    /// <summary>Removes all of <paramref name="amounts"/>, or nothing at all if anything is short.</summary>
    public bool TryRemove(IReadOnlyDictionary<string, long> amounts)
    {
        if (amounts.Values.Any(kg => kg < 0)) throw new ArgumentOutOfRangeException(nameof(amounts));
        if (Shortfall(amounts).Count > 0) return false;
        foreach (var (id, kg) in amounts)
        {
            var left = this[id] - kg;
            if (left == 0) _kg.Remove(id);
            else _kg[id] = left;
        }
        return true;
    }
}
