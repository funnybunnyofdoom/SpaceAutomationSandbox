using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// First visits. The craft the player is piloting, within <see cref="SurveyRangeM"/> of a body's
/// surface, surveys the sites it can see: those with any part above its horizon (#70). A craft
/// running a survey job (an automated scout, #4) circles the body and surveys all of its sites.
/// Jobs and structures can use a body once any site is surveyed; mining jobs only the surveyed sites.
/// </summary>
public sealed partial class SectorState
{
    public const double SurveyRangeM = 2_000;

    /// <summary>The craft the player is flying (it does the surveying). Saved as the save's player craft.</summary>
    public CraftId PilotCraftId { get; set; } = CraftId.None;

    /// <summary>Marks a body and all its sites surveyed directly (scenario setup and tests).</summary>
    public void MarkSurveyed(BodyId bodyId) => GetBody(bodyId).Surveyed = true;

    /// <summary>Restores a body's saved sites (#70).</summary>
    public void RestoreSites(BodyId bodyId, IReadOnlyList<(bool Surveyed, IReadOnlyDictionary<string, long> DepositsKg)> sites) =>
        GetBody(bodyId).RestoreSites(sites);

    /// <summary>Marks one site surveyed (tests and scenarios).</summary>
    public void MarkSiteSurveyed(BodyId bodyId, int siteIndex) => GetBody(bodyId).Sites[siteIndex].Surveyed = true;

    /// <summary>Restores a body's craters from a save (#28).</summary>
    public void RestoreCraters(BodyId bodyId, IEnumerable<Crater> craters) => GetBody(bodyId).RestoreCraters(craters);

    private void StepSurvey()
    {
        if (!PilotCraftId.IsNone && _craftsById.TryGetValue(PilotCraftId, out var pilot)) SurveyAround(pilot, wholeBody: false);
        foreach (var craft in _crafts)
            if (craft.Job is SurveyJob { Active: true } scout && craft.Transit is null) scout.CompletedCycles += SurveyAround(craft, wholeBody: true);
    }

    /// <summary>
    /// Surveys what the craft can see within range: every site of a body (a scout) or the sites above its
    /// horizon (the pilot, #70). Returns how many bodies became fully surveyed.
    /// </summary>
    private int SurveyAround(CraftState craft, bool wholeBody)
    {
        var count = 0;
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Surveyed || body.Unscanned || body.Kind == BodyKind.Star) continue;
            var centre = BodyPositionAt(i, TimeS);
            var distance = centre.DistanceTo(craft.Position);
            if (distance - body.RadiusM > SurveyRangeM) continue;
            if (wholeBody) body.Surveyed = true;
            else
            {
                // Above the surface the horizon is acos(R / d) from the point below; landed, a little way along.
                var bearing = Math.Atan2(craft.Position.Y - centre.Y, craft.Position.X - centre.X);
                var horizon = Math.Acos(Math.Clamp(body.RadiusM / Math.Max(distance, body.RadiusM), -1, 1));
                horizon = Math.Max(horizon, SurveyRangeM / 4 / Math.Max(body.RadiusM, 1));
                foreach (var site in body.Sites)
                {
                    if (site.Surveyed) continue;
                    var gap = Math.Abs(Math.IEEERemainder(bearing - site.CentreRad, 2 * Math.PI)) - site.WidthRad / 2; // to the site's nearest edge
                    if (gap <= horizon) site.Surveyed = true;
                }
            }
            if (body.Surveyed) count++;
        }
        return count;
    }
}
