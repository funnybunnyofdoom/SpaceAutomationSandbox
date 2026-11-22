using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Research and progression (#37). Research labs turn power and electronics into research points
/// (<see cref="ResearchCatalog"/> in data/research/research.json lists the techs, their costs and
/// prerequisites, and the parts each unlocks). Points are shared by the whole game; a tech is bought
/// with them once its prerequisites are done. A part a tech lists can't be built or fitted in a refit
/// until that tech is researched; parts no tech lists are open from the start, and parts already
/// built stay usable. Saves from before research existed have everything unlocked.
/// <para>A lab makes its rate in points per second while its craft is working (crew), powered, and has
/// electronics in stock; it uses <see cref="LabElectronicsKgPerPoint"/> kg of electronics per point
/// (counted as consumed in the mass ledger).</para>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Electronics a lab uses up per research point.</summary>
    public const double LabElectronicsKgPerPoint = 1;

    private readonly SortedSet<string> _unlockedTech = new(StringComparer.Ordinal);

    /// <summary>Research points to spend (saved).</summary>
    public double ResearchPoints { get; private set; }

    /// <summary>Techs researched (saved).</summary>
    public IReadOnlyCollection<string> UnlockedTech => _unlockedTech;

    /// <summary>Saves from before research (#37): everything is open. Saved.</summary>
    public bool ResearchAllUnlocked { get; private set; }

    /// <summary>Points made per second by the labs working this tick (for display).</summary>
    public double ResearchRateNow { get; private set; }

    private ResearchCatalog Tech => Parts?.Research ?? ResearchCatalog.Empty;

    public bool IsTechUnlocked(string techId) => ResearchAllUnlocked || _unlockedTech.Contains(techId);

    /// <summary>Can this part be built now?</summary>
    public bool IsPartUnlocked(string partId) => Tech.TechFor(partId) is not { } tech || IsTechUnlocked(tech.Id);

    /// <summary>Why a set of parts can't be built yet ("Warp gate needs Warp theory"), or null if they all can.</summary>
    public string? LockedPartsReason(IEnumerable<string> partIds)
    {
        var locked = partIds.Distinct().Where(id => !IsPartUnlocked(id)).ToList();
        if (locked.Count == 0) return null;
        return "Not researched yet: " + string.Join(", ", locked.Select(id =>
            $"{(Parts is { } p && p.TryGet(id, out var part) ? part.Name : id)} (needs {Tech.TechFor(id)!.Name})")) + ". Research it with R.";
    }

    /// <summary>What still stands in the way of researching a tech, or null if it can be bought now.</summary>
    public string? ResearchBlocker(string techId)
    {
        if (!Tech.TryGet(techId, out var tech)) return $"Unknown tech '{techId}'.";
        if (IsTechUnlocked(techId)) return $"{tech.Name} is already researched.";
        var missing = (tech.Requires ?? new List<string>()).Where(r => !IsTechUnlocked(r)).ToList();
        if (missing.Count > 0)
            return $"{tech.Name} needs " + string.Join(" and ", missing.Select(r => Tech.TryGet(r, out var t) ? t.Name : r)) + " first.";
        if (ResearchPoints + 1e-9 < tech.Cost) return $"{tech.Name} costs {tech.Cost:0} points (you have {ResearchPoints:0}).";
        return null;
    }

    /// <summary>Spends points on a tech.</summary>
    public ActionResult TryResearch(string techId)
    {
        if (ResearchBlocker(techId) is { } why) return new(false, why);
        var tech = Tech.TryGet(techId, out var t) ? t : throw new InvalidOperationException();
        ResearchPoints -= tech.Cost;
        _unlockedTech.Add(techId);
        var parts = (tech.Parts ?? new List<string>()).Select(id => Parts!.TryGet(id, out var p) ? p.Name : id);
        return new(true, $"Researched {tech.Name}: " + string.Join(", ", parts) + " can be built now.");
    }

    /// <summary>DEBUG: everything researched (tests and trying things out).</summary>
    public void DebugUnlockAllResearch()
    {
        foreach (var tech in Tech.Techs) _unlockedTech.Add(tech.Id);
    }

    /// <summary>DEBUG: adds research points.</summary>
    public void DebugAddResearch(double points)
    {
        if (!(points > 0)) throw new ArgumentOutOfRangeException(nameof(points));
        ResearchPoints += points;
    }

    public void RestoreResearch(double points, IEnumerable<string> unlocked, bool allUnlocked)
    {
        if (!(points >= 0)) throw new ArgumentOutOfRangeException(nameof(points));
        ResearchPoints = points;
        _unlockedTech.Clear();
        foreach (var id in unlocked) _unlockedTech.Add(id); // ids the tree no longer has are kept (harmless)
        ResearchAllUnlocked = allUnlocked;
    }

    private void StepResearch()
    {
        double rate = 0;
        foreach (var craft in _crafts)
        {
            craft.LabStatus = null;
            var labRate = craft.Stats.ResearchRatePerS;
            if (labRate <= 0) continue;
            if (!Operational(craft)) { craft.LabStatus = $"lab stopped: {craft.CrewShortReason}"; continue; }
            var due = craft.LabRemainderKg + labRate * LabElectronicsKgPerPoint * FixedStepClock.TickSeconds;
            var whole = (long)Math.Floor(due);
            if (craft.Inventory["electronics"] < Math.Max(whole, 1)) { craft.LabStatus = "lab idle: no electronics in stock"; continue; }
            if (!TryUsePower(craft, craft.Stats.LabPowerKW * FixedStepClock.TickSeconds)) { craft.LabStatus = "lab idle: no power"; continue; }
            if (whole > 0)
            {
                craft.Inventory.TryRemove(new Dictionary<string, long> { ["electronics"] = whole });
                _ejectedKg["electronics"] = _ejectedKg.GetValueOrDefault("electronics") + whole;
            }
            craft.LabRemainderKg = due - whole;
            ResearchPoints += labRate * FixedStepClock.TickSeconds;
            rate += labRate;
            craft.LabStatus = $"lab: {labRate * 60:0.#} points/min";
        }
        ResearchRateNow = rate;
    }
}
