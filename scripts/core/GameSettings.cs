using System;
using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Display settings (#52), kept in <c>user://settings.cfg</c>, separate from the game save:
/// window mode, windowed size and position, and the interface scale.
/// <para>The project uses stretch mode "disabled": a bigger window shows more of space instead of
/// a scaled-up 1280×720 picture. The interface is scaled on its own (see <see cref="EffectiveUiScale"/>).</para>
/// </summary>
public sealed class GameSettings
{
    public const string FilePath = "user://settings.cfg";

    /// <summary>The layout every panel is designed for; the interface never gets smaller than this (in its own units).</summary>
    public static readonly Vector2I DesignSize = new(1280, 720);

    /// <summary>Smallest window allowed.</summary>
    public static readonly Vector2I MinWindowSize = new(960, 540);

    /// <summary>Interface scale choices; 0 = automatic.</summary>
    public static readonly float[] UiScales = { 0, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f };

    /// <summary>Windowed size presets (only those that fit the screen are offered).</summary>
    public static readonly Vector2I[] WindowSizes = { new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440) };

    public enum Mode { Windowed, Fullscreen, ExclusiveFullscreen }

    public Mode WindowMode { get; set; } = Mode.Windowed;

    /// <summary>0 = automatic.</summary>
    public float UiScale { get; set; }

    public Vector2I? WindowSize { get; set; }
    public Vector2I? WindowPosition { get; set; }

    /// <summary>Audio buses (#64), in the order Settings shows them.</summary>
    public static readonly string[] AudioBuses = { "Master", "Music", "Effects", "Interface" };

    /// <summary>Volume per bus, 0..1 (#64).</summary>
    public System.Collections.Generic.Dictionary<string, float> Volume { get; } = new(StringComparer.Ordinal)
    {
        ["Master"] = 0.8f, ["Music"] = 0.5f, ["Effects"] = 0.8f, ["Interface"] = 0.6f,
    };

    /// <summary>Buses muted (#64).</summary>
    public System.Collections.Generic.HashSet<string> Muted { get; } = new(StringComparer.Ordinal);

    /// <summary>Autosave interval choices in minutes of play (#60); 0 = off.</summary>
    public static readonly int[] AutosaveChoices = { 0, 5, 10, 15, 30 };

    /// <summary>Minutes of play between autosaves (#60); 0 = off.</summary>
    public int AutosaveMinutes { get; set; } = 10;

    /// <summary>Silence everything while the window isn't in front (#64).</summary>
    public bool MuteWhenUnfocused { get; set; } = true;

    /// <summary>Show what needs attention when a save is loaded (#146).</summary>
    public bool ShowRecap { get; set; } = true;

    /// <summary>Captions for important sounds (#129).</summary>
    public bool Captions { get; set; }

    /// <summary>Effects (#130): 0 full, 1 reduced (no debris, smoke, sparks or dust), 2 off.</summary>
    public int Effects { get; set; }

    /// <summary>HUD areas folded away (#51), by title.</summary>
    public System.Collections.Generic.HashSet<string> HudCollapsed { get; } = new(StringComparer.Ordinal);

    /// <summary>The menu bar is shown (#106).</summary>
    public bool MenuBar { get; set; } = true;

    /// <summary>Target the next enemy by itself when one is destroyed (#110).</summary>
    public bool TargetNextEnemy { get; set; }

    /// <summary>Text size choices (#68), on top of the interface scale.</summary>
    public static readonly float[] TextScales = { 1f, 1.15f, 1.3f, 1.5f };

    /// <summary>Text size (#68): 1 = normal.</summary>
    public float TextScale { get; set; } = 1f;

    /// <summary>Colourblind-safe colours for meaning (#68), see <see cref="Palette"/>.</summary>
    public bool ColorblindSafe { get; set; }

    /// <summary>Controls the player changed (#63): action → bindings as text (see <see cref="InputActions.Serialize(string)"/>).
    /// Only changed actions are kept; the rest use the defaults.</summary>
    public System.Collections.Generic.Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal);

    public static GameSettings Load()
    {
        var settings = new GameSettings();
        var file = new ConfigFile();
        if (file.Load(FilePath) != Error.Ok) return settings;
        settings.WindowMode = Enum.TryParse<Mode>(file.GetValue("display", "window_mode", "Windowed").AsString(), out var mode) ? mode : Mode.Windowed;
        var scale = (float)file.GetValue("display", "ui_scale", 0f).AsDouble();
        settings.UiScale = Array.IndexOf(UiScales, scale) >= 0 ? scale : 0;
        if (file.HasSectionKey("display", "window_size")) settings.WindowSize = file.GetValue("display", "window_size").AsVector2I();
        if (file.HasSectionKey("display", "window_position")) settings.WindowPosition = file.GetValue("display", "window_position").AsVector2I();
        foreach (var title in file.GetValue("hud", "collapsed", "").AsString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            settings.HudCollapsed.Add(title);
        settings.TargetNextEnemy = file.GetValue("targets", "next_enemy", false).AsBool();
        settings.MenuBar = file.GetValue("hud", "menu_bar", true).AsBool();
        foreach (var bus in AudioBuses)
        {
            settings.Volume[bus] = Math.Clamp((float)file.GetValue("audio", $"{bus.ToLowerInvariant()}_volume", settings.Volume[bus]).AsDouble(), 0f, 1f);
            if (file.GetValue("audio", $"{bus.ToLowerInvariant()}_muted", false).AsBool()) settings.Muted.Add(bus);
        }
        settings.MuteWhenUnfocused = file.GetValue("audio", "mute_when_unfocused", true).AsBool();
        settings.ShowRecap = file.GetValue("game", "show_recap", true).AsBool();
        settings.Captions = file.GetValue("audio", "captions", false).AsBool();
        settings.Effects = Math.Clamp(file.GetValue("display", "effects", 0).AsInt32(), 0, 2);
        var autosave = file.GetValue("game", "autosave_minutes", 10).AsInt32();
        settings.AutosaveMinutes = Array.IndexOf(AutosaveChoices, autosave) >= 0 ? autosave : 10;
        var text = (float)file.GetValue("accessibility", "text_scale", 1f).AsDouble();
        settings.TextScale = Array.FindIndex(TextScales, t => Math.Abs(t - text) < 0.001f) >= 0 ? text : 1f;
        settings.ColorblindSafe = file.GetValue("accessibility", "colorblind_safe", false).AsBool();
        if (file.HasSection("controls"))
            foreach (var action in file.GetSectionKeys("controls"))
                settings.Bindings[action] = file.GetValue("controls", action, "").AsString();
        return settings;
    }

    public void Save()
    {
        var file = new ConfigFile();
        file.SetValue("display", "window_mode", WindowMode.ToString());
        file.SetValue("display", "ui_scale", UiScale);
        if (WindowSize is { } size) file.SetValue("display", "window_size", size);
        if (WindowPosition is { } position) file.SetValue("display", "window_position", position);
        file.SetValue("hud", "collapsed", string.Join(",", HudCollapsed));
        file.SetValue("hud", "menu_bar", MenuBar);
        file.SetValue("targets", "next_enemy", TargetNextEnemy);
        foreach (var bus in AudioBuses)
        {
            file.SetValue("audio", $"{bus.ToLowerInvariant()}_volume", Volume[bus]);
            file.SetValue("audio", $"{bus.ToLowerInvariant()}_muted", Muted.Contains(bus));
        }
        file.SetValue("audio", "mute_when_unfocused", MuteWhenUnfocused);
        file.SetValue("game", "show_recap", ShowRecap);
        file.SetValue("audio", "captions", Captions);
        file.SetValue("display", "effects", Effects);
        file.SetValue("game", "autosave_minutes", AutosaveMinutes);
        file.SetValue("accessibility", "text_scale", TextScale);
        file.SetValue("accessibility", "colorblind_safe", ColorblindSafe);
        foreach (var (action, text) in Bindings) file.SetValue("controls", action, text);
        file.Save(FilePath);
    }

    /// <summary>
    /// The interface scale for a window this size. Automatic grows gently with the window (1× at
    /// 1280×720, 1.25× at 1080p, 1.5× at 1440p, 2× at 4K), so text stays readable without covering
    /// more of the screen. Any choice is capped so the 1280×720 layout still fits the window.
    /// </summary>
    public float EffectiveUiScale(Vector2 windowSize)
    {
        var fit = Math.Min(windowSize.X / DesignSize.X, windowSize.Y / DesignSize.Y);
        var wanted = UiScale > 0 ? UiScale : MathF.Floor((1 + (fit - 1) * 0.5f) * 4) / 4;
        return Math.Clamp(Math.Min(wanted, fit), 0.5f, 2f);
    }

    /// <summary>Applies window mode, size and position (call once at start, and after changes).</summary>
    public void ApplyWindow()
    {
        DisplayServer.WindowSetMinSize(MinWindowSize);
        switch (WindowMode)
        {
            case Mode.Fullscreen: DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen); return;
            case Mode.ExclusiveFullscreen: DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen); return;
        }
        if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        if (WindowSize is { } size)
            DisplayServer.WindowSetSize(new Vector2I(Math.Max(size.X, MinWindowSize.X), Math.Max(size.Y, MinWindowSize.Y)));
        // Restore the position only if the window's top-left lands on a screen that still exists.
        if (WindowPosition is { } position)
            for (var screen = 0; screen < DisplayServer.GetScreenCount(); screen++)
                if (DisplayServer.ScreenGetUsableRect(screen).HasPoint(position + new Vector2I(40, 20)))
                {
                    DisplayServer.WindowSetPosition(position);
                    break;
                }
    }

    /// <summary>Remembers the current windowed size and position (fullscreen keeps the last windowed ones).</summary>
    public void CaptureWindow()
    {
        if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed) return;
        WindowSize = DisplayServer.WindowGetSize();
        WindowPosition = DisplayServer.WindowGetPosition();
    }
}
