using Godot;

namespace SpaceAutomationSandbox.UI;

/// <summary>An icon with text, or with a bar (#99): the line every panel and the HUD shows things with.</summary>
public partial class InfoRow : HBoxContainer
{
    private readonly TextureRect _icon = UiTheme.Icon("");
    private readonly Label _label = new() { ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
    private readonly StatBar _bar = new();
    private HudRow? _row;

    public InfoRow()
    {
        MouseFilter = MouseFilterEnum.Pass; // for its tooltip
        AddThemeConstantOverride("separation", 6);
        _icon.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !e.IsActionPressed("ui_accept")) return;
            if (!HasLink) return;
            AcceptEvent();
            _row?.Click?.Invoke();
            if (_row?.Link is { } link) UiLinks.Open(link);
        };
        MouseEntered += () => Lit(true);
        MouseExited += () => Lit(false);
        FocusEntered += QueueRedraw;
        FocusExited += QueueRedraw;
        AddChild(_icon);
        AddChild(_label);
        AddChild(_bar);
    }

    /// <summary>Clicking it (or choosing it with keys or a controller) does something.</summary>
    public bool HasLink => _row is { Click: not null } or { Link: not null };

    private void Lit(bool lit) => _label.SelfModulate = lit && HasLink ? UiTheme.Accent : Colors.White;

    public override void _Draw()
    {
        if (HasFocus()) DrawRect(new Rect2(-2, -1, Size.X + 4, Size.Y + 2), Core.Palette.Focus, filled: false, width: 2);
    }

    public void Set(HudRow? row)
    {
        Visible = row is not null;
        if (row is null || row == _row) return;
        if (row.Icon != _row?.Icon) _icon.Texture = UiIcons.Get(row.Icon);
        if (row.Tip != _row?.Tip) TooltipText = row.Tip;
        MouseDefaultCursorShape = row.Click is null && row.Link is null ? CursorShape.Arrow : CursorShape.PointingHand;
        _label.Visible = row.Fraction is null;
        _bar.Visible = row.Fraction is not null;
        if (row.Fraction is { } fraction) _bar.Set(fraction, row.Text, row.Severity, row.Marker);
        else
        {
            if (_label.Text != row.Text) _label.Text = row.Text;
            if (_row is null || row.Severity != _row.Severity) _label.Modulate = UiTheme.Of(row.Severity);
        }
        _row = row;
    }
}

/// <summary>A list of <see cref="InfoRow"/>s, reused between refreshes.</summary>
public partial class InfoList : VBoxContainer
{
    private readonly System.Collections.Generic.List<InfoRow> _views = new();

    public InfoList()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 3);
    }

    public void Set(System.Collections.Generic.IReadOnlyList<HudRow> rows)
    {
        while (_views.Count < rows.Count)
        {
            var view = new InfoRow();
            _views.Add(view);
            AddChild(view);
        }
        for (var i = 0; i < _views.Count; i++) _views[i].Set(i < rows.Count ? rows[i] : null);
    }
}
