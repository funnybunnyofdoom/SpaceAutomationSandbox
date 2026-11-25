using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>Interface icons (#99): <c>assets/art/icons/&lt;name&gt;.png</c>, 16×16; materials are <c>mat_&lt;id&gt;</c>.</summary>
public static class UiIcons
{
    private static readonly Dictionary<string, Texture2D?> Loaded = new();

    /// <summary>The icon, or null if there's none by that name.</summary>
    public static Texture2D? Get(string name)
    {
        if (Loaded.TryGetValue(name, out var texture)) return texture;
        var path = $"res://assets/art/icons/{name}.png";
        return Loaded[name] = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    public static Texture2D? Material(string id) => Get("mat_" + id);

    /// <summary>The icon name for a body, by its kind.</summary>
    public static string BodyIcon(Simulation.CelestialBodyState body) => body.Kind switch
    {
        Simulation.BodyKind.Star => "star", Simulation.BodyKind.Moon => "moon", Simulation.BodyKind.Asteroid => Simulation.SectorState.IsComet(body) ? "comet" : "asteroid", Simulation.BodyKind.GasGiant => "gas", _ => "planet",
    };
}

/// <summary>How serious something shown is. Always has its own icon or words too, never colour alone (#68).</summary>
public enum Severity { Normal, Muted, Good, Warning, Bad }

/// <summary>The "clean glass" look (#99): dark translucent panels, thin cyan edges, readable text.</summary>
public static class UiTheme
{
    public const float PanelAlpha = 0.7f;
    public static readonly Color Cyan = new(0.36f, 0.84f, 1f);
    public static readonly Color Glass = new(0.02f, 0.04f, 0.08f, PanelAlpha);

    /// <summary>The interface's colour; a race can have its own (#97). Set before <see cref="Build"/>.</summary>
    public static Color Accent { get; set; } = Cyan;

    public static Color Edge => Tint(new Color(0.36f, 0.75f, 1f, 0.35f));

    /// <summary>A bar with nothing wrong: duller when the accent is near the warning colour, so the two can't be mistaken.</summary>
    public static Color Bar => Accent.IsEqualApprox(Cyan) ? Accent : new Color(0.66f, 0.6f, 0.52f);

    /// <summary>One of the theme's blues in the accent's hue.</summary>
    private static Color Tint(Color blue) => Accent.IsEqualApprox(Cyan) ? blue : Color.FromHsv(Accent.H, blue.S * 0.9f, blue.V, blue.A);

    public static Color Of(Severity severity) => severity switch
    {
        Severity.Muted => new Color(0.72f, 0.78f, 0.86f),
        Severity.Good => Palette.Good,
        Severity.Warning => Palette.Warning,
        Severity.Bad => Palette.Bad,
        _ => Colors.White,
    };

    private static StyleBoxFlat Box(Color fill, Color edge, float x, float y, int border = 1, int radius = 4)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = edge, ContentMarginLeft = x, ContentMarginRight = x, ContentMarginTop = y, ContentMarginBottom = y };
        box.SetBorderWidthAll(border);
        box.SetCornerRadiusAll(radius);
        return box;
    }

    public static Theme Build(float textScale)
    {
        var size = Mathf.RoundToInt(16 * textScale);
        var theme = new Theme { DefaultFontSize = size };
        var focus = Box(Colors.Transparent, Palette.Focus, 0, 0, 2);
        focus.DrawCenter = false;
        focus.SetExpandMarginAll(2);
        foreach (var type in new[] { "Button", "OptionButton", "CheckBox", "CheckButton", "LineEdit", "HSlider", "ItemList", "MenuButton" })
            theme.SetStylebox("focus", type, focus);

        theme.SetStylebox("panel", "PanelContainer", Box(Glass, Edge, 10, 8));
        theme.SetStylebox("panel", "TooltipPanel", Box(new Color(0.03f, 0.05f, 0.09f, 1f), Edge, 8, 5));
        theme.SetStylebox("panel", "PopupMenu", Box(new Color(0.03f, 0.06f, 0.11f, 0.97f), Edge, 6, 6));
        theme.SetStylebox("hover", "PopupMenu", Box(Tint(new Color(0.12f, 0.32f, 0.45f)), Colors.Transparent, 4, 2, 0));
        theme.SetStylebox("panel", "ItemList", Box(new Color(0, 0, 0, 0.3f), new Color(Edge, 0.2f), 6, 4));

        foreach (var type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            theme.SetStylebox("normal", type, Box(Tint(new Color(0.09f, 0.17f, 0.25f, 0.75f)), new Color(Edge, 0.3f), 5, 3));
            theme.SetStylebox("hover", type, Box(Tint(new Color(0.12f, 0.27f, 0.38f, 0.9f)), new Color(Accent, 0.8f), 5, 3));
            theme.SetStylebox("pressed", type, Box(Tint(new Color(0.1f, 0.4f, 0.55f, 0.95f)), Accent, 5, 3));
            theme.SetStylebox("hover_pressed", type, Box(Tint(new Color(0.13f, 0.46f, 0.62f, 0.95f)), Accent, 5, 3));
            theme.SetStylebox("disabled", type, Box(new Color(0.06f, 0.08f, 0.11f, 0.55f), new Color(1, 1, 1, 0.08f), 5, 3));
        }
        foreach (var type in new[] { "CheckBox", "CheckButton" }) // no box of their own
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
                theme.SetStylebox(state, type, new StyleBoxEmpty { ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2 });
        theme.SetStylebox("normal", "LineEdit", Box(new Color(0, 0, 0, 0.45f), new Color(Edge, 0.3f), 8, 3));
        theme.SetStylebox("read_only", "LineEdit", Box(new Color(0, 0, 0, 0.25f), new Color(1, 1, 1, 0.08f), 8, 3));

        foreach (var (name, alpha) in new[] { ("tab_selected", 0.9f), ("tab_hovered", 0.6f), ("tab_unselected", 0.3f), ("tab_disabled", 0.15f) })
            foreach (var type in new[] { "TabBar", "TabContainer" })
                theme.SetStylebox(name, type, Box(Tint(new Color(0.1f, 0.28f, 0.4f, alpha)), name == "tab_selected" ? Accent : new Color(Edge, 0.2f), 10, 4));
        theme.SetStylebox("panel", "TabContainer", Box(new Color(0, 0, 0, 0.2f), new Color(Edge, 0.2f), 8, 8));
        theme.SetStylebox("background", "ProgressBar", Box(new Color(0, 0, 0, 0.5f), new Color(Edge, 0.3f), 2, 1, 1, 3));
        theme.SetStylebox("fill", "ProgressBar", Box(new Color(Accent, 0.65f), Colors.Transparent, 0, 0, 0, 3));
        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            theme.SetStylebox("scroll", type, Box(new Color(1, 1, 1, 0.05f), Colors.Transparent, 4, 4, 0));
            theme.SetStylebox("grabber", type, Box(new Color(Accent, 0.35f), Colors.Transparent, 4, 4, 0));
            theme.SetStylebox("grabber_highlight", type, Box(new Color(Accent, 0.6f), Colors.Transparent, 4, 4, 0));
            theme.SetStylebox("grabber_pressed", type, Box(new Color(Accent, 0.8f), Colors.Transparent, 4, 4, 0));
        }
        // Text over the HUD is a little smaller; section headers are accented.
        theme.SetTypeVariation("HudText", "Label");
        theme.SetFontSize("font_size", "HudText", Mathf.RoundToInt(14 * textScale));
        theme.SetTypeVariation("HudHeader", "Button");
        theme.SetFontSize("font_size", "HudHeader", Mathf.RoundToInt(15 * textScale));
        theme.SetTypeVariation("SectionHeader", "Label");
        theme.SetColor("font_color", "SectionHeader", Accent);
        theme.SetColor("font_color", "HudHeader", Accent);
        return theme;
    }

    /// <summary>An accented heading inside a panel.</summary>
    public static Label Header(string text) => new() { Text = text, ThemeTypeVariation = "SectionHeader" };

    /// <summary>A pixel-art icon at a whole multiple of its size.</summary>
    public static TextureRect Icon(string name, int size = 16) => new()
    {
        Texture = UiIcons.Get(name), CustomMinimumSize = new Vector2(size, size), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
    };
}

/// <summary>A bar with a value (#99): how full something is, its text over it, and optionally a mark to stay under.</summary>
public partial class StatBar : Control
{
    private double _fraction;
    private double? _marker;
    private string _text = "";
    private Severity _severity;

    public StatBar()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    private int TextSize => GetThemeFontSize("font_size", "HudText");

    public override Vector2 _GetMinimumSize() => new(90, TextSize + 6);

    public void Set(double fraction, string text, Severity severity = Severity.Normal, double? marker = null)
    {
        fraction = double.IsFinite(fraction) ? Mathf.Clamp(fraction, 0, 1) : 0;
        if (System.Math.Abs(fraction - _fraction) < 0.002 && text == _text && severity == _severity && marker == _marker) return;
        (_fraction, _text, _severity, _marker) = (fraction, text, severity, marker);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var fill = _severity == Severity.Normal ? UiTheme.Bar : UiTheme.Of(_severity);
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.5f));
        DrawRect(new Rect2(0, 0, Size.X * (float)_fraction, Size.Y), new Color(fill, 0.42f));
        DrawRect(new Rect2(0, Size.Y - 2, Size.X * (float)_fraction, 2), fill);
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(UiTheme.Edge, 0.35f), filled: false);
        if (_marker is { } at) DrawLine(new Vector2(Size.X * (float)at, 0), new Vector2(Size.X * (float)at, Size.Y), Palette.Warning, 2);
        var font = GetThemeDefaultFont();
        var baseline = (Size.Y + font.GetAscent(TextSize) - font.GetDescent(TextSize)) / 2;
        DrawString(font, new Vector2(7, baseline + 1), _text, HorizontalAlignment.Left, Size.X - 10, TextSize, new Color(0, 0, 0, 0.9f));
        DrawString(font, new Vector2(6, baseline), _text, HorizontalAlignment.Left, Size.X - 10, TextSize, Colors.White);
    }
}

/// <summary>A craft's picture from its design (#99), drawn with the part sprites.</summary>
public partial class DesignPicture : Control
{
    private ShipDesign? _design;

    public DesignPicture()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
        CustomMinimumSize = new Vector2(40, 40);
    }

    public ShipDesign? Design
    {
        set
        {
            if (ReferenceEquals(value, _design)) return;
            _design = value;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_design is not { Cells.Count: > 0 } design) return;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (var cell in design.Cells)
        {
            (x0, x1) = (System.Math.Min(x0, cell.Pos.X), System.Math.Max(x1, cell.Pos.X));
            (y0, y1) = (System.Math.Min(y0, cell.Pos.Y), System.Math.Max(y1, cell.Pos.Y));
        }
        var cellPx = Mathf.Min(Mathf.Min(Size.X / (x1 - x0 + 1), Size.Y / (y1 - y0 + 1)), 16);
        var origin = (Size - new Vector2(x1 - x0 + 1, y1 - y0 + 1) * cellPx) / 2;
        foreach (var cell in design.Cells)
        {
            var centre = origin + new Vector2(cell.Pos.X - x0 + 0.5f, cell.Pos.Y - y0 + 0.5f) * cellPx;
            DrawSetTransform(centre, cell.Rotation * Mathf.Pi / 2);
            var rect = new Rect2(-cellPx / 2, -cellPx / 2, cellPx, cellPx);
            if (PartSprites.Icon(cell.PartId) is { } sprite) DrawTextureRect(sprite, rect, false);
            else DrawRect(rect, Colors.Gray);
        }
        DrawSetTransform(Vector2.Zero);
    }
}
