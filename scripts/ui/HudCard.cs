using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>A line of a HUD card: an icon and either text, or (with a fraction) a bar with the text on it.</summary>
public sealed record HudRow(string Icon, string Text, Severity Severity = Severity.Normal, double? Fraction = null, double? Marker = null, string Tip = "", Action? Click = null);

/// <summary>What a HUD card shows: a heading, a picture (a design or an icon) and its rows. No rows and no heading: hidden.</summary>
public sealed record HudCardData(string Heading, IReadOnlyList<HudRow> Rows, ShipDesign? Design = null, string? Icon = null)
{
    public static readonly HudCardData None = new("", Array.Empty<HudRow>());
}

/// <summary>One area of the graphical HUD (#101). Clicking its heading folds it away; rows are reused, so showing them is cheap.</summary>
public partial class HudCard : PanelContainer
{
    private readonly string _title;
    private readonly Func<bool> _isCollapsed;
    private readonly VBoxContainer _rows;
    private readonly Button _header;
    private readonly DesignPicture _picture = new();
    private readonly TextureRect _icon = UiTheme.Icon("", 32);
    private readonly List<InfoRow> _views = new();
    private HudCardData _data = HudCardData.None;

    public HudCardData Data => _data;

    /// <summary>Only the heading shows: there's no room (#96).</summary>
    public bool Squeezed { get; set; }

    public HudCard(string title, Func<bool> isCollapsed, Action toggled)
    {
        _title = title;
        _isCollapsed = isCollapsed;
        MouseFilter = MouseFilterEnum.Stop; // a click on a card isn't one in the world (#108)
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 3);
        AddChild(box);
        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddChild(top);
        top.AddChild(_picture);
        top.AddChild(_icon);
        _header = new Button
        {
            Flat = true, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, ThemeTypeVariation = "HudHeader", TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, ClipText = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Click to fold or unfold",
        };
        _header.AddThemeColorOverride("font_color", UiTheme.Accent);
        _header.Pressed += () => { toggled(); Set(_data); };
        top.AddChild(_header);
        _rows = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _rows.AddThemeConstantOverride("separation", 3);
        box.AddChild(_rows);
    }

    public HudCard() : this("", () => false, () => { }) { }

    public void Set(HudCardData data)
    {
        _data = data;
        Visible = data.Rows.Count > 0 || data.Heading.Length > 0;
        if (!Visible) return;
        var folded = _isCollapsed();
        var closed = folded || Squeezed;
        var heading = data.Heading.Length > 0 ? data.Heading : _title;
        var text = (folded ? "+ " : "") + heading;
        if (_header.Text != text) _header.Text = text;
        _picture.Visible = !closed && data.Design is not null;
        _picture.Design = data.Design;
        var icon = data.Icon is null ? null : UiIcons.Get(data.Icon);
        _icon.Visible = !closed && data.Design is null && icon is not null;
        if (_icon.Texture != icon) _icon.Texture = icon;
        _rows.Visible = !closed && data.Rows.Count > 0;
        if (closed) return;
        while (_views.Count < data.Rows.Count)
        {
            var view = new InfoRow();
            _views.Add(view);
            _rows.AddChild(view);
        }
        for (var i = 0; i < _views.Count; i++) _views[i].Set(i < data.Rows.Count ? data.Rows[i] : null);
    }

    /// <summary>The height this card needs at a width, worked out from its text (its layout may not have run yet).</summary>
    public float HeightAt(float width, bool squeezed)
    {
        if (!Visible) return 0;
        var headerSize = GetThemeFontSize("font_size", "HudHeader");
        var closed = squeezed || _isCollapsed();
        var font = GetThemeDefaultFont();
        var height = 18 + Mathf.Max(font.GetHeight(headerSize) + 6, !closed && _data.Design is not null ? 40 : !closed && _data.Icon is not null ? 32 : 0);
        if (closed) return height;
        var size = GetThemeFontSize("font_size", "HudText");
        foreach (var row in _data.Rows)
            height += 3 + (row.Fraction is not null ? size + 6 : font.GetMultilineStringSize(row.Text, HorizontalAlignment.Left, width - 44, size).Y + 2);
        return height;
    }
}
