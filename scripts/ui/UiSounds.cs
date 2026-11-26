using System;

namespace SpaceAutomationSandbox.UI;

/// <summary>Interface sounds asked for by panels and the editor (#129); the audio director plays them.</summary>
public static class UiSounds
{
    public static event Action<string>? Played;

    public static void Play(string name) => Played?.Invoke(name);
}
