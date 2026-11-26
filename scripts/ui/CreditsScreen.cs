using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The credits (#66, #138): who made the game and every third-party asset with its licence, from <c>data/credits.json</c>, then the
/// Godot Engine's own copyright and licence notice, which its MIT licence asks to be shipped. Opened from the title screen; Close,
/// Esc or B goes back.
/// </summary>
public partial class CreditsScreen : Control
{
    public event Action? Closed;

    private Button _close = null!;
    private ScrollContainer _scroll = null!;

    public CreditsScreen()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        AddChild(new ColorRect { Color = new Color(0, 0.01f, 0.03f, 0.94f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });

        var margin = new MarginContainer { AnchorRight = 1, AnchorBottom = 1 };
        foreach (var side in new[] { "margin_left", "margin_right" }) margin.AddThemeConstantOverride(side, 96);
        foreach (var side in new[] { "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 40);
        AddChild(margin);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 14);
        margin.AddChild(outer);
        var heading = new Label { Text = $"{GameInfo.Name.ToUpperInvariant()}   ·   {GameInfo.VersionLabel}", HorizontalAlignment = HorizontalAlignment.Center };
        heading.AddThemeFontSizeOverride("font_size", 28);
        heading.Modulate = UiTheme.Cyan;
        outer.AddChild(heading);

        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        outer.AddChild(_scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        _scroll.AddChild(list);
        foreach (var (title, lines) in Sections())
        {
            var t = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center, Modulate = UiTheme.Cyan };
            t.AddThemeFontSizeOverride("font_size", 20);
            list.AddChild(t);
            foreach (var line in lines)
                list.AddChild(new Label { Text = line, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart });
            list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        }
        var engine = new Label { Text = "Godot Engine licence", HorizontalAlignment = HorizontalAlignment.Center, Modulate = UiTheme.Cyan };
        engine.AddThemeFontSizeOverride("font_size", 20);
        list.AddChild(engine);
        var notice = new Label { Text = Engine.GetLicenseText(), AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.6f) };
        notice.AddThemeFontSizeOverride("font_size", 12);
        list.AddChild(notice);
        list.AddChild(new Label { Text = $"{GameInfo.Name} is copyright (c) 2026 funnybunnyofdoom. All rights reserved.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.6f) });

        _close = new Button { Text = "Close", CustomMinimumSize = new Vector2(200, 40), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.All };
        _close.Pressed += Close;
        outer.AddChild(_close);
    }

    /// <summary>The sections of data/credits.json, or just the maker if it can't be read.</summary>
    private static List<(string Title, List<string> Lines)> Sections()
    {
        const string path = "res://data/credits.json";
        var sections = new List<(string, List<string>)>();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(path));
            foreach (var section in doc.RootElement.GetProperty("sections").EnumerateArray())
            {
                var lines = new List<string>();
                foreach (var line in section.GetProperty("lines").EnumerateArray()) lines.Add(line.GetString() ?? "");
                sections.Add((section.GetProperty("title").GetString() ?? "", lines));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            GD.PushWarning($"{path}: {e.Message}");
            sections.Add(("Made by", new List<string> { "funnybunnyofdoom" }));
        }
        return sections;
    }

    public void Open()
    {
        Visible = true;
        _scroll.ScrollVertical = 0;
        _close.GrabFocus();
    }

    private void Close()
    {
        Visible = false;
        Closed?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || !e.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        Close();
    }
}
