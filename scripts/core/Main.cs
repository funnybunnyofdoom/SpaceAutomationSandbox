using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Phase 0 bootstrap scene. Proves the C# assembly loads and F5 opens a non-blank window.
/// Deliberately contains no gameplay: it draws a deterministic starfield and a status label.
/// Future gameplay belongs in scripts/simulation etc., not here (see AGENTS.md).
/// </summary>
public partial class Main : Node2D
{
    // Fixed seed so the backdrop is identical on every run (determinism habit from day one).
    private const int StarfieldSeed = 20261120;
    private const int StarCount = 220;

    private static readonly Color SpaceColor = new(0.02f, 0.02f, 0.06f);

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(SpaceColor);

        var label = new Label
        {
            Text = "SpaceAutomationSandbox (working title)\nPhase 0 bootstrap OK - Godot "
                   + Engine.GetVersionInfo()["string"].AsString(),
            Position = new Vector2(-620, -340),
        };
        AddChild(label);

        GD.Print("[Bootstrap] Main scene ready. C# assembly loaded. Godot "
                 + Engine.GetVersionInfo()["string"].AsString());
    }

    public override void _Draw()
    {
        var rng = new RandomNumberGenerator { Seed = StarfieldSeed };
        for (var i = 0; i < StarCount; i++)
        {
            var position = new Vector2(rng.RandfRange(-640, 640), rng.RandfRange(-360, 360));
            var brightness = rng.RandfRange(0.35f, 1.0f);
            var size = rng.Randf() < 0.1f ? 2f : 1f;
            DrawRect(new Rect2(position.Floor(), new Vector2(size, size)),
                new Color(brightness, brightness, brightness));
        }
    }
}
