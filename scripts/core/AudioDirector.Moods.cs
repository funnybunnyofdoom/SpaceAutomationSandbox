using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// The sound pass (#129): what's heard of the world (fights, building, jumps) near the ship you fly, alerts told apart by ear, interface sounds,
/// music that follows what's happening with crossfades, and captions. Sounds come from the sector's noise ring, so a thousand ships firing make a
/// few sounds, not a thousand: at most a few a frame, each kind spaced out, quieter with distance, and fewer at high time speeds.
/// </summary>
public partial class AudioDirector
{
    /// <summary>Farther than this from the ship you fly, nothing is heard.</summary>
    public const double HearingM = 6_000;

    private const int MostPerFrame = 4;
    private const double FadeS = 3, CalmAfterS = 10, MoodAfterS = 3, AlertGapS = 20;

    private readonly Dictionary<MusicMood, AudioStreamPlayer> _moods = new();
    private Dictionary<string, string> _moodFiles = new(StringComparer.Ordinal);
    private Dictionary<string, string> _captions = new(StringComparer.Ordinal);
    private MusicMood _mood = MusicMood.Calm, _wanted = MusicMood.Calm;
    private double _wantedFor;
    private long _heard = -1;
    private SectorEvent? _lastEvent;
    private SectorState? _of;
    private double _alertsAt;
    private readonly Dictionary<AlertKind, int> _alertCounts = new();

    /// <summary>A caption for a sound that played: show it when captions are on.</summary>
    public event Action<string>? Captioned;

    public MusicMood Mood => _mood;

    private void SetUpMoods(Dictionary<string, string> moods, Dictionary<string, string> captions)
    {
        (_moodFiles, _captions) = (moods, captions);
        foreach (var mood in new[] { MusicMood.Tension, MusicMood.Combat })
        {
            var player = new AudioStreamPlayer { Bus = "Music", VolumeDb = -60 };
            if (_moodFiles.TryGetValue(mood.ToString().ToLowerInvariant(), out var path) && Load(path) is { } stream)
            {
                if (stream is AudioStreamWav wav) { wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward; wav.LoopBegin = 0; wav.LoopEnd = (int)(wav.GetLength() * wav.MixRate); }
                if (stream is AudioStreamOggVorbis ogg) ogg.Loop = true;
                player.Stream = stream;
            }
            AddChild(player);
            _moods[mood] = player;
        }
        UiSounds.Played += Interface;
    }

    public override void _ExitTree() => UiSounds.Played -= Interface;

    private void Caption(string name)
    {
        if (_settings.Captions && _captions.TryGetValue(name, out var text)) Captioned?.Invoke(text);
    }

    /// <summary>Once a frame: the mood (it changes only after a while, then fades), and the music players' volumes.</summary>
    private void StepMusic(MusicMood wanted, double delta)
    {
        if (wanted != _wanted) { _wanted = wanted; _wantedFor = 0; }
        _wantedFor += delta;
        // Into danger quickly; back to calm only once it has been calm a while.
        if (_wanted != _mood && _wantedFor >= (_wanted < _mood ? CalmAfterS : MoodAfterS) && (_wanted == MusicMood.Calm || _moods[_wanted].Stream is not null)) _mood = _wanted;
        var step = (float)(delta * 60 / FadeS);
        Fade(_musicPlayer, _mood == MusicMood.Calm, step);
        foreach (var (mood, player) in _moods) Fade(player, _mood == mood, step);

        static void Fade(AudioStreamPlayer player, bool on, float step)
        {
            if (player.Stream is null) return;
            player.VolumeDb = Mathf.MoveToward(player.VolumeDb, on ? 0 : -60, step);
            if (on && !player.Playing) player.Play();
            if (on && player.StreamPaused) player.StreamPaused = false;
            if (!on && player.Playing && player.VolumeDb <= -59) player.StreamPaused = true; // keeps its place: no gap when it comes back
        }
    }

    /// <summary>Sounds of the world near you, from the sector's noise ring.</summary>
    private void Hear(SectorState sector, CraftState pilot, double timeSpeed)
    {
        if (!ReferenceEquals(sector, _of)) { _of = sector; _heard = sector.NoiseCount; _lastEvent = sector.Events.Count > 0 ? sector.Events[^1] : null; _alertCounts.Clear(); return; }
        var from = Math.Max(_heard, sector.NoiseCount - SectorState.NoisesKept);
        var played = 0;
        var most = timeSpeed > 10 ? 1 : MostPerFrame;
        for (var i = sector.NoiseCount - 1; i >= from && played < most; i--) // newest first
        {
            var noise = sector.NoiseAt(i);
            var distance = noise.At.DistanceTo(pilot.Position);
            if (distance > HearingM) continue;
            if (Effect(noise.Kind, (float)(-18 * distance / HearingM), gapS: 0.08 * Math.Max(1, timeSpeed))) played++;
        }
        _heard = sector.NoiseCount;
    }

    /// <summary>New events: raids, hazards, contracts, discoveries and milestones each sound their own way.</summary>
    private void Announce(SectorState sector)
    {
        var events = sector.Events;
        var start = events.Count;
        while (start > 0 && !ReferenceEquals(events[start - 1], _lastEvent)) start--;
        if (_lastEvent is not null && start == 0) start = events.Count; // ours dropped off the start: nothing new to tell apart
        for (var i = start; i < events.Count; i++)
        {
            var e = events[i];
            var sound = e.Kind switch
            {
                EventKind.Danger when e.Text.Contains("raid", StringComparison.OrdinalIgnoreCase) => "raid_alarm",
                EventKind.Danger when e.Text.Contains("flare") || e.Text.Contains("Meteors") => "hazard",
                EventKind.Civilization when e.Text.Contains("offer a contract") => "contract",
                EventKind.Discovery => "discovery",
                EventKind.Milestone => "milestone",
                _ => null,
            };
            if (sound is not null) Effect(sound, -4, gapS: 2);
        }
        _lastEvent = events.Count > 0 ? events[^1] : null;
    }

    /// <summary>Every two seconds: more craft without crew, or waiting, than before, each kind at most every 20 s.</summary>
    private void Alerts(SectorState sector)
    {
        if (_clock - _alertsAt < 2) return;
        _alertsAt = _clock;
        var counts = sector.Alerts().GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (kind, sound) in new[] { (AlertKind.Crew, "crew_alarm"), (AlertKind.Waiting, "waiting") })
        {
            var now = counts.GetValueOrDefault(kind);
            if (now > _alertCounts.GetValueOrDefault(kind)) Effect(sound, -6, gapS: AlertGapS);
            _alertCounts[kind] = now;
        }
    }
}
