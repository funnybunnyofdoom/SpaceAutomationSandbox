using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The blueprint editor's part tooltip: picture, what the part does, what it's made of and the tech it needs, on an opaque
/// background because the editor is full of text. It never takes the mouse.
/// </summary>
public partial class PartCard : PanelContainer
{
    private const float CardWidth = 340;

    private readonly VBoxContainer _box = new();
    private string _key = "";

    public PartCard()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        ZIndex = 50;
        var back = new StyleBoxFlat { BgColor = new Color(0.03f, 0.05f, 0.09f, 1f), BorderColor = UiTheme.Accent };
        back.SetBorderWidthAll(1);
        back.SetCornerRadiusAll(4);
        back.SetContentMarginAll(10);
        back.ShadowColor = new Color(0, 0, 0, 0.6f);
        back.ShadowSize = 8;
        AddThemeStyleboxOverride("panel", back);
        _box.MouseFilter = MouseFilterEnum.Ignore;
        _box.AddThemeConstantOverride("separation", 4);
        AddChild(_box);
    }

    /// <summary>Shows a part beside an area (both in this control's parent), kept inside the parent.</summary>
    public void Show(PartDefinition part, MaterialCatalog materials, string? lockedBy, Rect2 beside, string? problem = null)
    {
        var scale = GetThemeDefaultFontSize() / 16f;
        var key = $"{part.Id}|{lockedBy}|{scale}|{problem}";
        if (key != _key)
        {
            _key = key;
            foreach (var child in _box.GetChildren()) { _box.RemoveChild(child); child.QueueFree(); }
            var top = Row();
            top.AddChild(new TextureRect
            {
                Texture = PartSprites.Icon(part.Id), CustomMinimumSize = new Vector2(48, 48), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest, MouseFilter = MouseFilterEnum.Ignore,
            });
            var title = Text($"{part.Name}\n{part.MassKg / 1000:0.##} t", CardWidth * scale - 56);
            title.ThemeTypeVariation = "SectionHeader";
            top.AddChild(title);
            if (problem is not null) Line("danger", problem, Severity.Bad, scale);
            if (lockedBy is not null) Line("research", $"Needs {lockedBy} researched before it can be built.", Severity.Warning, scale);
            foreach (var fact in PartFacts.Of(part, materials)) Line(fact.Icon, fact.Text, Severity.Normal, scale);
            if (part.MaterialsKg is { Count: > 0 } made)
            {
                _box.AddChild(new HSeparator { MouseFilter = MouseFilterEnum.Ignore });
                foreach (var (id, kg) in made.OrderBy(m => materials.OrderOf(m.Key)))
                    Line("mat_" + id, $"{(materials.Contains(id) ? materials.Get(id).Name : id)}  {kg / 1000:0.###} t", Severity.Muted, scale);
            }
        }
        Visible = true;
        Size = Vector2.Zero;
        var size = GetCombinedMinimumSize();
        var room = GetParent<Control>().Size;
        var x = beside.End.X + 10;
        if (x + size.X > room.X - 8) x = beside.Position.X - 10 - size.X;
        Position = new Vector2(Mathf.Max(8, x), Mathf.Clamp(beside.Position.Y, 8, Mathf.Max(8, room.Y - size.Y - 8)));
    }

    private HBoxContainer Row()
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        _box.AddChild(row);
        return row;
    }

    private void Line(string icon, string text, Severity severity, float scale)
    {
        var row = Row();
        var picture = UiTheme.Icon(icon);
        picture.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        row.AddChild(picture);
        var label = Text(text, CardWidth * scale - 24);
        label.Modulate = UiTheme.Of(severity);
        row.AddChild(label);
    }

    private static Label Text(string text, float width) => new()
    {
        Text = text, ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart,
        CustomMinimumSize = new Vector2(width, 0), MouseFilter = MouseFilterEnum.Ignore,
    };
}
