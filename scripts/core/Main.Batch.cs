using System.Collections.Generic;
using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Batched drawing (#39): ship cells, flames, far-away markers and the starfield are collected as
/// coloured triangles and sent in one call each, instead of one draw command per cell or star.
/// That keeps 1,000 ships on screen within a 60 fps frame.
/// </summary>
public partial class Main
{
    private readonly List<Vector2> _batchPoints = new();
    private readonly List<Color> _batchColors = new();
    private readonly List<(Vector2 From, Vector2 To, float Width)> _crackLines = new();

    // Part sprites (#46): textured triangles from the part atlas, drawn under the flat ones (flames go on top).
    private readonly List<Vector2> _spritePoints = new();
    private readonly List<Vector2> _spriteUvs = new();
    private readonly List<Color> _spriteColors = new();

    private void BatchSprite((Vector2 A, Vector2 B, Vector2 C, Vector2 D) corners, (Vector2 A, Vector2 B, Vector2 C, Vector2 D) uv, Color modulate)
    {
        _spritePoints.Add(corners.A); _spritePoints.Add(corners.B); _spritePoints.Add(corners.C);
        _spritePoints.Add(corners.A); _spritePoints.Add(corners.C); _spritePoints.Add(corners.D);
        _spriteUvs.Add(uv.A); _spriteUvs.Add(uv.B); _spriteUvs.Add(uv.C);
        _spriteUvs.Add(uv.A); _spriteUvs.Add(uv.C); _spriteUvs.Add(uv.D);
        for (var i = 0; i < 6; i++) _spriteColors.Add(modulate);
    }

    private Vector2[]? _starPoints;
    private Color[]? _starColors;
    private int[]? _starIndices;
    private Vector2 _starfieldSize;

    private void BatchTriangle(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        _batchPoints.Add(a);
        _batchPoints.Add(b);
        _batchPoints.Add(c);
        _batchColors.Add(color);
        _batchColors.Add(color);
        _batchColors.Add(color);
    }

    private void BatchQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
    {
        BatchTriangle(a, b, c, color);
        BatchTriangle(a, c, d, color);
    }

    /// <summary>Sends the collected triangles, then the crack lines that go on top of them.</summary>
    private void FlushBatch()
    {
        if (_spritePoints.Count > 0 && PartSprites.Atlas is { } atlas)
        {
            var spriteIndices = new int[_spritePoints.Count];
            for (var i = 0; i < spriteIndices.Length; i++) spriteIndices[i] = i;
            RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), spriteIndices, _spritePoints.ToArray(), _spriteColors.ToArray(),
                _spriteUvs.ToArray(), null, null, atlas.GetRid());
        }
        _spritePoints.Clear();
        _spriteUvs.Clear();
        _spriteColors.Clear();
        if (_batchPoints.Count > 0)
        {
            var indices = new int[_batchPoints.Count];
            for (var i = 0; i < indices.Length; i++) indices[i] = i;
            RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), indices, _batchPoints.ToArray(), _batchColors.ToArray());
        }
        foreach (var (from, to, width) in _crackLines) DrawLine(from, to, new Color(0, 0, 0, 0.7f), width);
        _batchPoints.Clear();
        _batchColors.Clear();
        _crackLines.Clear();
    }

    /// <summary>The background stars: fixed on screen, rebuilt only when the window size changes.</summary>
    private void DrawStarfield(Node2D on)
    {
        var size = GetViewportRect().Size;
        if (_starPoints is null || size != _starfieldSize)
        {
            _starfieldSize = size;
            // Fills the window at any size, at the same star density as the 1280×720 design.
            var half = size / 2;
            var count = (int)(StarCount * (half.X * half.Y) / (640f * 360f));
            var rng = new RandomNumberGenerator { Seed = StarfieldSeed };
            var points = new List<Vector2>(count * 6);
            var colors = new List<Color>(count * 6);
            for (var i = 0; i < count; i++)
            {
                var p = new Vector2(rng.RandfRange(-half.X, half.X), rng.RandfRange(-half.Y, half.Y)).Floor();
                var brightness = rng.RandfRange(0.35f, 1.0f);
                var s = rng.Randf() < 0.1f ? 2f : 1f;
                var c = new Color(brightness, brightness, brightness);
                points.AddRange(new[] { p, p + new Vector2(s, 0), p + new Vector2(s, s), p, p + new Vector2(s, s), p + new Vector2(0, s) });
                for (var k = 0; k < 6; k++) colors.Add(c);
            }
            _starPoints = points.ToArray();
            _starColors = colors.ToArray();
            _starIndices = new int[_starPoints.Length];
            for (var i = 0; i < _starIndices.Length; i++) _starIndices[i] = i;
        }
        if (_starPoints.Length == 0) return;
        on.DrawSetTransform(_camera.Position, 0, Vector2.One);
        RenderingServer.CanvasItemAddTriangleArray(on.GetCanvasItem(), _starIndices!, _starPoints, _starColors!);
        on.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
