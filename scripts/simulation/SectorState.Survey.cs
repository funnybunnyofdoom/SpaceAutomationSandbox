using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// First visits: the craft the player is piloting surveys any body whose surface it comes within
/// <see cref="SurveyRangeM"/> of; so does a craft running a survey job (an automated scout, #4), by
/// the same rule. Only surveyed bodies can be mined by jobs or built on.
/// </summary>
public sealed partial class SectorState
{
    public const double SurveyRangeM = 2_000;

    /// <summary>The craft the player is flying (it does the surveying). Saved as the save's player craft.</summary>
    public CraftId PilotCraftId { get; set; } = CraftId.None;

    /// <summary>Marks a body surveyed directly (scenario setup and tests).</summary>
    public void MarkSurveyed(BodyId bodyId) => GetBody(bodyId).Surveyed = true;

    /// <summary>Restores a body's craters from a save (#28).</summary>
    public void RestoreCraters(BodyId bodyId, IEnumerable<Crater> craters) => GetBody(bodyId).RestoreCraters(craters);

    private void StepSurvey()
    {
        if (!PilotCraftId.IsNone && _craftsById.TryGetValue(PilotCraftId, out var pilot)) SurveyAround(pilot);
        foreach (var craft in _crafts)
            if (craft.Job is SurveyJob { Active: true } scout && craft.Transit is null) scout.CompletedCycles += SurveyAround(craft);
    }

    /// <summary>Surveys every body within range of the craft; returns how many were new.</summary>
    private int SurveyAround(CraftState craft)
    {
        var count = 0;
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Surveyed || body.Kind == BodyKind.Star) continue;
            if (BodyPositionAt(i, TimeS).DistanceTo(craft.Position) - body.RadiusM > SurveyRangeM) continue;
            body.Surveyed = true;
            count++;
        }
        return count;
    }
}
