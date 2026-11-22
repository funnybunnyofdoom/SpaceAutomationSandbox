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
        return settings;
    }

    public void Save()
    {
        var file = new ConfigFile();
        file.SetValue("display", "window_mode", WindowMode.ToString());
        file.SetValue("display", "ui_scale", UiScale);
        if (WindowSize is { } size) file.SetValue("display", "window_size", size);
        if (WindowPosition is { } position) file.SetValue("display", "window_position", position);
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
