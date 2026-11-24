using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>A stop on a route: a craft's picture or an icon, with a name under it.</summary>
public readonly record struct RouteStop(string Name, ShipDesign? Design, string Icon);

/// <summary>A route as a picture (#103): stops in a row, with what travels between them on the arrows.</summary>
public partial class RouteView : HBoxContainer
{
    private string _key = "";

    public RouteView()
    {
        Alignment = AlignmentMode.Center;
        AddThemeConstantOverride("separation", 4);
    }

    /// <summary>One more stop than goods; the goods are icon names.</summary>
    public void Set(IReadOnlyList<RouteStop> stops, IReadOnlyList<string> goods)
    {
        var key = string.Join("|", stops) + "#" + string.Join(",", goods);
        if (key == _key) return;
        _key = key;
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        for (var i = 0; i < stops.Count; i++)
        {
            if (i > 0)
            {
                var arrow = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
                arrow.AddThemeConstantOverride("separation", 0);
                var good = UiTheme.Icon(i - 1 < goods.Count ? goods[i - 1] : "", 32);
                good.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
                arrow.AddChild(good);
                var line = new Label { Text = "→", HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(46, 0) };
                line.AddThemeColorOverride("font_color", UiTheme.Accent);
                arrow.AddChild(line);
                AddChild(arrow);
            }
            var stop = new VBoxContainer { CustomMinimumSize = new Vector2(112, 0) };
            Control picture = stops[i].Design is { } design ? new DesignPicture { Design = design, CustomMinimumSize = new Vector2(48, 48) } : UiTheme.Icon(stops[i].Icon, 48);
            picture.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            stop.AddChild(picture);
            stop.AddChild(new Label
            {
                Text = stops[i].Name, ThemeTypeVariation = "HudText", HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(112, 0),
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            });
            AddChild(stop);
        }
        Visible = stops.Count > 0;
    }
}
