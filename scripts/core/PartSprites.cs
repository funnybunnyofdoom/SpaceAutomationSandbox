using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Ship-part sprites (#46): <c>assets/art/ship_parts/&lt;part id&gt;.png</c>, 16×16 per 2 m cell, exported from
/// the Pixelorama sources in <c>art_source/pixelorama/ship_parts</c>. Rotation 0 faces up.
/// <para>They're packed into one atlas so a whole fleet draws in one call. Each sprite sits in an 18×18 slot
/// with its edge pixels repeated around it, so a rotated or scaled cell never picks up its neighbour's pixels.
/// A part without a sprite falls back to its placeholder colour.</para>
/// </summary>
public static class PartSprites
{
    public const int CellPixels = 16;
    private const int Slot = CellPixels + 2;
    public const string Folder = "res://assets/art/ship_parts";

    /// <summary>Effect sprites (#130), in the same atlas under "fx:" and their name.</summary>
    public const string FxFolder = "res://assets/art/fx";

    private static readonly Dictionary<string, Rect2> UvRects = new();
    private static readonly Dictionary<string, Texture2D> Icons = new();

    /// <summary>All sprites in one texture (null until <see cref="Load"/> found any).</summary>
    public static ImageTexture? Atlas { get; private set; }

    /// <summary>Loads every part's sprite that exists; call once the part catalog is known.</summary>
    public static void Load(IEnumerable<string> partIds)
    {
        UvRects.Clear();
        Icons.Clear();
        var images = new List<(string Id, Image Image)>();
        var fx = DirAccess.DirExistsAbsolute(FxFolder) ? DirAccess.GetFilesAt(FxFolder).Where(f => f.EndsWith(".png")).Select(f => "fx:" + f[..^4]).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();
        foreach (var id in partIds.Concat(fx))
        {
            var path = id.StartsWith("fx:", StringComparison.Ordinal) ? $"{FxFolder}/{id[3..]}.png" : $"{Folder}/{id}.png";
            if (!ResourceLoader.Exists(path) || GD.Load<Texture2D>(path) is not { } texture) continue;
            var image = texture.GetImage();
            if (image is null || image.GetWidth() != CellPixels || image.GetHeight() != CellPixels) continue;
            if (image.IsCompressed()) image.Decompress();
            image.Convert(Image.Format.Rgba8);
            images.Add((id, image));
            Icons[id] = texture;
        }
        if (images.Count == 0) { Atlas = null; return; }

        var columns = Mathf.CeilToInt(Mathf.Sqrt(images.Count));
        var rows = (images.Count + columns - 1) / columns;
        var atlas = Image.CreateEmpty(columns * Slot, rows * Slot, false, Image.Format.Rgba8);
        for (var i = 0; i < images.Count; i++)
        {
            var (id, image) = images[i];
            var ox = (i % columns) * Slot;
            var oy = (i / columns) * Slot;
            for (var y = -1; y <= CellPixels; y++)
                for (var x = -1; x <= CellPixels; x++)
                    atlas.SetPixel(ox + 1 + x, oy + 1 + y, image.GetPixel(Mathf.Clamp(x, 0, CellPixels - 1), Mathf.Clamp(y, 0, CellPixels - 1)));
            UvRects[id] = new Rect2((ox + 1f) / atlas.GetWidth(), (oy + 1f) / atlas.GetHeight(),
                (float)CellPixels / atlas.GetWidth(), (float)CellPixels / atlas.GetHeight());
        }
        Atlas = ImageTexture.CreateFromImage(atlas);
    }

    public static bool Has(string partId) => UvRects.ContainsKey(partId);

    /// <summary>
    /// The atlas coordinates for a part's corners in screen order (top-left, top-right, bottom-right,
    /// bottom-left) when the part is turned <paramref name="rotation"/> quarter turns clockwise.
    /// </summary>
    public static bool TryCorners(string partId, int rotation, out Vector2 c0, out Vector2 c1, out Vector2 c2, out Vector2 c3)
    {
        c0 = c1 = c2 = c3 = default;
        if (!UvRects.TryGetValue(partId, out var r)) return false;
        Span<Vector2> uv = stackalloc Vector2[] { r.Position, new(r.End.X, r.Position.Y), r.End, new(r.Position.X, r.End.Y) };
        var turn = rotation & 3;
        c0 = uv[(4 - turn) % 4];
        c1 = uv[(5 - turn) % 4];
        c2 = uv[(6 - turn) % 4];
        c3 = uv[(7 - turn) % 4];
        return true;
    }

    /// <summary>The part's own texture, for buttons and the blueprint editor; null without a sprite.</summary>
    public static Texture2D? Icon(string partId) => Icons.GetValueOrDefault(partId);
}
