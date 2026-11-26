using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>Megastructures (#126) in the world and on the map: drawn as lines and arcs, so cheap at any zoom.</summary>
public partial class Main
{
    private static readonly Color MegaColor = new(0.7f, 0.85f, 1f);

    private MegaPanel? _megaPanel;

    private void DrawMegas(double renderTimeS)
    {
        if (_sector.Megas.Count == 0) return;
        var ui = _uiRoot.Scale.X;
        foreach (var project in _sector.Megas)
        {
            if (_sector.KindOf(project) is not { } kind || !_sector.Exists(new TargetRef(CraftId.None, project.BodyId))) continue;
            var body = _sector.GetBody(project.BodyId);
            var centre = ToPixels(_sector.GetPositionAt(body.Id, renderTimeS));
            var radius = (float)(body.RadiusM + kind.AltitudeM) * _pixelsPerMetre;
            var built = (float)project.Stages / kind.Stages.Count;
            var colour = new Color(MegaColor, _sector.Share(project) > 0 || project.Stages == 0 ? 0.85f : 0.35f);
            var width = Mathf.Max(2f, 60 * _pixelsPerMetre);
            var hub = _sector.Exists(new TargetRef(project.HubId, BodyId.None)) ? ToPixels(InterpolatedPosition(_sector.GetCraft(project.HubId))) : centre;
            var start = Mathf.Atan2(hub.Y - centre.Y, hub.X - centre.X);
            switch (kind.Place)
            {
                case MegaPlace.RockyPlanet: // a cable from the ground straight up to the hub
                    var foot = centre + (hub - centre).Normalized() * (float)body.RadiusM * _pixelsPerMetre;
                    var top = foot.Lerp(hub, built);
                    DrawLine(foot, top, colour, width);
                    if (built < 1) DashedLine(top, hub, new Color(MegaColor, 0.3f), Mathf.Max(1f, width / 2));
                    break;
                case MegaPlace.Star: // collectors on a circle, more with each stage
                    if (radius > 6) DashedArc(centre, radius, new Color(MegaColor, 0.15f), 1);
                    var count = (int)(240 * built);
                    for (var i = 0; i < count; i++)
                    {
                        var angle = Mathf.Tau * i / 240 + (float)(renderTimeS * 0.0005);
                        DrawRect(new Rect2(centre + Vector2.FromAngle(angle) * radius - Vector2.One * 1.5f * ui, Vector2.One * 3 * ui), colour);
                    }
                    break;
                default:
                    if (kind.HubBuilds) // a frame round the hub, an edge a stage
                    {
                        var size = Mathf.Max(24 * ui, 400 * _pixelsPerMetre);
                        var corners = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
                        for (var i = 0; i < 4; i++)
                            if (i < Mathf.CeilToInt(built * 4)) DrawLine(hub + corners[i] * size, hub + corners[(i + 1) % 4] * size, colour, Mathf.Max(2f, 30 * _pixelsPerMetre));
                            else DashedLine(hub + corners[i] * size, hub + corners[(i + 1) % 4] * size, new Color(MegaColor, 0.3f), 1);
                    }
                    else // a ring growing round from the hub
                    {
                        if (radius < 3) break;
                        if (built > 0) DrawArc(centre, radius, start, start + Mathf.Tau * built, Mathf.Clamp((int)(radius / 4), 16, 512), colour, width);
                        if (built < 1) DashedArc(centre, radius, new Color(MegaColor, 0.25f), 1);
                    }
                    break;
            }
            if (_mapMode) MapIcon("mega", hub + new Vector2(0, -16) * ui, 16 * ui);
        }
    }

    /// <summary>Dev/test hook <c>--open-mega</c>: everything researched, a ring and an elevator founded at Home's neighbours, partly built, and the panel open.</summary>
    private void DemoMega()
    {
        if (_player is null) return;
        _sector.DebugUnlockAllResearch();
        var home = SectorGenerator.HomePlanet(_sector);
        var moon = _sector.Bodies.First(b => b.Name == "Moon");
        _sector.FoundMega("orbital_ring", home.Id);
        _sector.FoundMega("space_elevator", moon.Id);
        foreach (var project in _sector.Megas)
        {
            if (_sector.KindOf(project) is not { } kind) continue;
            _sector.DebugBuildMega(project.Id, project.KindId == "orbital_ring" ? 2 : 1);
        }
        _player.TargetBodyId = home.Id;
        _megaPanel?.Open();
    }
}
