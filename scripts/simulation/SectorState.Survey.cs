namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// First visits: the craft the player is piloting surveys any body whose surface it comes within
/// <see cref="SurveyRangeM"/> of. Only surveyed bodies can be mined by jobs or built on, so new
/// sites are founded by the player going there (NPC expansion comes later).
/// </summary>
public sealed partial class SectorState
{
    public const double SurveyRangeM = 2_000;

    /// <summary>The craft the player is flying (it does the surveying). Saved as the save's player craft.</summary>
    public CraftId PilotCraftId { get; set; } = CraftId.None;

    /// <summary>Marks a body surveyed directly (scenario setup and tests).</summary>
    public void MarkSurveyed(BodyId bodyId) => GetBody(bodyId).Surveyed = true;

    private void StepSurvey()
    {
        if (PilotCraftId.IsNone || !_craftsById.TryGetValue(PilotCraftId, out var pilot)) return;
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Surveyed) continue;
            if (BodyPositionAt(i, TimeS).DistanceTo(pilot.Position) - body.RadiusM <= SurveyRangeM) body.Surveyed = true;
        }
    }
}
