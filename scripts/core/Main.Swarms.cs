using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Drawing swarms (#30, the creator's choice: looks only). A cloud of little ships flies the route between the
/// source and the destination at the pace of the real trip. At most <see cref="MaxSwarmShipsDrawn"/> are drawn, however
/// many there are; they aren't craft, can't be targeted and touch nothing.
/// </summary>
public partial class Main
{
    private const int MaxSwarmShipsDrawn = 150;
    private static readonly Color SwarmColor = new(0.55f, 0.95f, 1f, 0.9f);
    private static readonly Color SwarmLoadedColor = new(1f, 0.85f, 0.5f, 0.9f);

    /// <summary>Collects the swarms' ships into the frame's triangle batch (call before it's flushed); labels are drawn at once.</summary>
    private void DrawSwarms(double renderTimeS)
    {
        if (_sector.Swarms.Count == 0) return;
        var view = new Rect2(-GetViewportRect().Size / 2, GetViewportRect().Size).Grow(40);
        foreach (var swarm in _sector.Swarms)
        {
            if (_sector.Crafts.FirstOrDefault(c => c.Id == swarm.DestinationId) is not { } destination) continue;
            if (_sector.Bodies.FirstOrDefault(b => b.Id == swarm.SourceBodyId) is not { } source) continue;
            var body = _sector.GetPositionAt(source.Id, renderTimeS);
            var home = InterpolatedPosition(destination);
            var toBody = body - home;
            var distance = toBody.Length;
            if (distance < 1) continue;
            var along = toBody * (1 / distance);
            var side = new Vector2D(-along.Y, along.X);
            var rim = body - along * source.RadiusM; // the surface facing the destination
            var route = rim - home;
            // All its ships within a pixel: nothing to see but the label.
            var routePx = (float)(route.Length * _pixelsPerMetre);
            var rate = swarm.Rate;
            var cycle = rate.CycleS > 0 && !double.IsInfinity(rate.CycleS) ? rate.CycleS : 600;
            var outShare = rate.CycleS > 0 ? rate.OutS / cycle : 0.4;
            var mineShare = rate.CycleS > 0 ? (cycle - rate.OutS - rate.BackS) / cycle : 0.2;
            var moving = swarm.LimitReason is null || swarm.DeliveryKgPerS > 0;
            var size = Mathf.Clamp((float)swarm.Stats.BoundingRadiusM * _pixelsPerMetre, 2.5f, 14f);
            var lane = System.Math.Max(20, 6 / _pixelsPerMetre); // out and back keep apart
            if (routePx > 6)
                for (var i = 0; i < System.Math.Min(swarm.Count, MaxSwarmShipsDrawn); i++)
                {
                    // Spread evenly round the trip, with a fixed scatter per ship. View only.
                    var scatter = Hash(i * 2 + 1);
                    var phase = (moving ? renderTimeS / cycle : 0) + i * 0.6180339887;
                    phase -= System.Math.Floor(phase);
                    var sideM = lane * (0.5 + 2.5 * scatter) * (swarm.Count > 50 ? 1.6 : 1);
                    Vector2D at, heading;
                    var loaded = true;
                    if (phase < outShare)
                    {
                        at = home + route * Ease(phase / outShare) + side * sideM;
                        heading = along;
                        loaded = false;
                    }
                    else if (phase < outShare + mineShare)
                    {
                        var bearing = System.Math.Atan2(-along.Y, -along.X) + (scatter - 0.5) * 1.2; // at the surface, on the near side
                        heading = new Vector2D(System.Math.Cos(bearing), System.Math.Sin(bearing));
                        at = body + heading * (source.RadiusM + swarm.Stats.BoundingRadiusM);
                    }
                    else
                    {
                        at = rim - route * Ease((phase - outShare - mineShare) / System.Math.Max(1e-6, 1 - outShare - mineShare)) - side * sideM;
                        heading = along * -1;
                    }
                    var px = ToPixels(at);
                    if (!view.HasPoint(px)) continue;
                    var f = new Vector2((float)heading.X, (float)heading.Y);
                    var s = new Vector2(-f.Y, f.X);
                    BatchTriangle(px + f * size, px - f * size * 0.8f + s * size * 0.6f, px - f * size * 0.8f - s * size * 0.6f, loaded ? SwarmLoadedColor : SwarmColor);
                }
            var label = ToPixels(home + route * 0.5);
            if (view.HasPoint(label) && routePx > 60) _swarmHits.Add((swarm, new Rect2(label + new Vector2(8, -28), 160, 24))); // #108
            if (view.HasPoint(label) && routePx > 60)
                DrawString(ThemeDB.FallbackFont, label + new Vector2(12, -12), $"{swarm.Name} ({swarm.Count:N0}){(swarm.LimitReason is null ? "" : "  !")}",
                    HorizontalAlignment.Left, -1, 12, CraftLabelColor);
        }

        static double Ease(double t) => t * t * (3 - 2 * t);
        static double Hash(int n)
        {
            var x = (uint)n * 2654435761u;
            x ^= x >> 15;
            return (x * 2246822519u >> 8) / 16777216.0;
        }
    }

    /// <summary>Dev/test hook: <c>--demo-swarm=5000</c> makes a swarm of that many starter ships mining the Moon (cheats; nothing is saved).</summary>
    private void DemoSwarm(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith("--demo-swarm=")) is not { } arg || !int.TryParse(arg["--demo-swarm=".Length..], out var count)) return;
        if (_starterDesign is null || _catalog is null || _sector.Crafts.FirstOrDefault(c => c.IsShipyard) is not { } yard) return;
        _sector.DebugSurveyAll();
        var moon = _sector.Bodies.First(b => b.Kind == BodyKind.Moon && b.IsMineable);
        var stats = _starterDesign.ComputeStats(_catalog);
        var ids = new List<CraftId>();
        for (var i = 0; i < System.Math.Max(count, SectorState.MinSwarmShips); i++)
        {
            var craft = new CraftState(_sector.AllocateCraftId(), $"S{i}", _starterDesign, stats, yard.Position + new Vector2D(300 + i, 300), 0) { Velocity = yard.Velocity };
            _sector.AddCraft(craft);
            _sector.StartJob(craft.Id, new MiningJob(moon.Id, moon.BulkMaterialId, yard.Id, 0.25));
            ids.Add(craft.Id);
        }
        ShowStatus(_sector.FormSwarm(ids).Message);
        for (var i = 0; i < 1200; i++) _sector.Step();
    }
}
