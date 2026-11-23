using Godot;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Colours that carry meaning (#68): good/placing, bad/erasing/broken, and warnings. With the
/// colourblind-safe option they come from the Okabe–Ito palette (sky blue, vermillion, yellow), which
/// stays apart for the common kinds of colour blindness. Meaning is never shown by colour alone:
/// each use also has a mark or text (an ×, a "!", the word).
/// </summary>
public static class Palette
{
    /// <summary>Set from <see cref="GameSettings.ColorblindSafe"/>.</summary>
    public static bool ColorblindSafe { get; set; }

    public static Color Good => ColorblindSafe ? new Color(0.34f, 0.71f, 0.91f) : new Color(0.4f, 1f, 0.6f);
    public static Color Bad => ColorblindSafe ? new Color(0.9f, 0.45f, 0.1f) : new Color(1f, 0.35f, 0.3f);
    public static Color Warning => ColorblindSafe ? new Color(0.94f, 0.89f, 0.26f) : new Color(1f, 0.7f, 0.3f);

    /// <summary>The highlight around whatever has keyboard/controller focus.</summary>
    public static Color Focus => ColorblindSafe ? new Color(0.94f, 0.89f, 0.26f) : new Color(0.45f, 0.85f, 1f);
}
