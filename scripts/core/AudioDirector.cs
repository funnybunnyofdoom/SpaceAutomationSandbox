using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Sound and music (#64). Cinematic, from your own ship: you hear the craft you fly as if from inside
/// its hull (thrust, drills, cruise hum, docking, landing, anchoring, warp jumps, alarms, crashes),
/// plus crashes close to it, interface clicks, and calm ambient music. Nothing far away makes a sound.
/// <list type="bullet">
/// <item>Buses: Master, Music, Effects, Interface, with volume and mute from the settings (and muted
/// while the window is in the background, if set).</item>
/// <item>Sounds come from changes in the piloted craft's state each frame, read from the simulation;
/// no node per craft, and a fixed pool of players with a per-sound minimum gap, so nothing piles up.</item>
/// <item>Files are listed in data/audio/sounds.json and loaded straight from disk (OGG); a missing file
/// is skipped, so the game runs with any subset.</item>
/// </list>
/// </summary>
public partial class AudioDirector : Node
{
    private const string TablePath = "res://data/audio/sounds.json";
    private const int EffectVoices = 8;
    private const double MinGapS = 0.12; // the same sound at most this often
    private const double NearbyImpactM = 2_000;

    private readonly GameSettings _settings;
    private readonly Dictionary<string, AudioStream?> _cache = new(StringComparer.Ordinal);
    private Dictionary<string, string> _effects = new(StringComparer.Ordinal);
    private Dictionary<string, string> _interface = new(StringComparer.Ordinal);
    private List<string> _music = new();
    private readonly List<AudioStreamPlayer> _voices = new();
    private readonly Dictionary<string, double> _lastPlayed = new(StringComparer.Ordinal);
    private AudioStreamPlayer _ui = null!, _musicPlayer = null!;
    private readonly Dictionary<string, AudioStreamPlayer> _loops = new(StringComparer.Ordinal);
    private int _track = -1;
    private bool _focused = true;
    private double _clock;

    // What the piloted craft was doing last frame.
    private CraftId _watched = CraftId.None;
    private bool _wasDocked, _wasLanded, _wasAnchored, _wasCruising, _wasPowerShort, _wasStopped;
    private int _wasJumps, _wasPartsLost;
    private long _lastImpactTick = -1;

    public AudioDirector(GameSettings settings)
    {
        _settings = settings;
    }

    public AudioDirector() : this(new GameSettings()) { }

    public override void _Ready()
    {
        EnsureBuses();
        LoadTable();
        for (var i = 0; i < EffectVoices; i++)
        {
            var voice = new AudioStreamPlayer { Bus = "Effects" };
            AddChild(voice);
            _voices.Add(voice);
        }
        _ui = new AudioStreamPlayer { Bus = "Interface" };
        AddChild(_ui);
        _musicPlayer = new AudioStreamPlayer { Bus = "Music" };
        _musicPlayer.Finished += NextTrack;
        AddChild(_musicPlayer);
        foreach (var loop in new[] { "thrust_loop", "drill_loop", "cruise_loop" })
        {
            var player = new AudioStreamPlayer { Bus = "Effects", Stream = Stream(_effects, loop) };
            AddChild(player);
            _loops[loop] = player;
        }
        // Every button anywhere clicks (panels are built in code, so listen for them as they appear).
        GetTree().NodeAdded += node =>
        {
            if (node is BaseButton button) button.Pressed += () => Interface("click");
        };
        SetUpMoods(_moodFiles, _captions); // #129
        ApplySettings();
        NextTrack();
        var found = _effects.Values.Concat(_interface.Values).Concat(_music).Count(path => Load(path) != null);
        GD.Print($"[Audio] {found} of {_effects.Count + _interface.Count + _music.Count} sounds found; music {(_musicPlayer.Playing ? "playing" : "silent")}");
    }

    /// <summary>Master, Music, Effects and Interface buses (the last three feed Master).</summary>
    private static void EnsureBuses()
    {
        foreach (var name in GameSettings.AudioBuses.Skip(1))
        {
            if (AudioServer.GetBusIndex(name) >= 0) continue;
            AudioServer.AddBus();
            var index = AudioServer.BusCount - 1;
            AudioServer.SetBusName(index, name);
            AudioServer.SetBusSend(index, "Master");
        }
    }

    /// <summary>Applies volumes and mutes (call after settings change and on focus changes).</summary>
    public void ApplySettings()
    {
        foreach (var bus in GameSettings.AudioBuses)
        {
            var index = AudioServer.GetBusIndex(bus);
            if (index < 0) continue;
            var volume = _settings.Volume.TryGetValue(bus, out var v) ? v : 1f;
            AudioServer.SetBusVolumeDb(index, Mathf.LinearToDb(Mathf.Max(volume, 0.0001f)));
            var muted = _settings.Muted.Contains(bus) || (bus == "Master" && !_focused && _settings.MuteWhenUnfocused);
            AudioServer.SetBusMute(index, muted);
        }
    }

    /// <summary>The window came to the front or went to the background.</summary>
    public void SetFocused(bool focused)
    {
        _focused = focused;
        ApplySettings();
    }

    private void LoadTable()
    {
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(TablePath));
            var root = doc.RootElement;
            _effects = root.GetProperty("effects").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
            _interface = root.GetProperty("interface").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
            _music = root.GetProperty("music").EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
            _moodFiles = root.TryGetProperty("moods", out var moods) ? moods.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal) : new(StringComparer.Ordinal);
            _captions = root.TryGetProperty("captions", out var captions) ? captions.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Audio] {TablePath} could not be read ({e.Message}): no sounds.");
        }
    }

    /// <summary>The stream for a name (loaded once from disk), or null if the file isn't there.</summary>
    private AudioStream? Stream(Dictionary<string, string> table, string name) =>
        table.TryGetValue(name, out var path) ? Load(path) : null;

    private AudioStream? Load(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        AudioStream? stream = null;
        // Imported sounds load as resources: an exported game packs them and has no loose files (#140). A file that wasn't
        // imported (one dropped in later) is read from disk.
        if (ResourceLoader.Exists(path)) stream = ResourceLoader.Load<AudioStream>(path);
        var file = ProjectSettings.GlobalizePath(path);
        if (stream is null && System.IO.File.Exists(file))
            stream = file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? AudioStreamWav.LoadFromFile(file) : AudioStreamOggVorbis.LoadFromFile(file); // generated music is WAV (#129)
        _cache[path] = stream;
        return stream;
    }

    /// <summary>Plays an effect once (on a free voice), unless it played a moment ago; true if it played.</summary>
    public bool Effect(string name, float volumeDb = 0, double gapS = MinGapS)
    {
        if (Stream(_effects, name) is not { } stream || TooSoon(name, gapS)) return false;
        var voice = _voices.FirstOrDefault(v => !v.Playing) ?? _voices.MinBy(v => v.GetPlaybackPosition())!;
        voice.Stream = stream;
        voice.VolumeDb = volumeDb;
        voice.Play();
        Caption(name);
        return true;
    }

    /// <summary>Plays an interface sound.</summary>
    public void Interface(string name)
    {
        if (Stream(_interface, name) is not { } stream || TooSoon("ui:" + name)) return;
        _ui.Stream = stream;
        _ui.Play();
        Caption(name);
    }

    private bool TooSoon(string key, double gapS = MinGapS)
    {
        if (_lastPlayed.TryGetValue(key, out var last) && _clock - last < gapS) return true;
        _lastPlayed[key] = _clock;
        return false;
    }

    private void NextTrack()
    {
        var tracks = _music.Select(Load).Where(s => s != null).ToList();
        if (tracks.Count == 0) return;
        _track = (_track + 1) % tracks.Count;
        _musicPlayer.Stream = tracks[_track];
        _musicPlayer.Play();
    }

    /// <summary>Starts or stops a loop smoothly (it keeps playing quietly while fading).</summary>
    private void Loop(string name, bool on, float onDb, double delta)
    {
        if (!_loops.TryGetValue(name, out var player) || player.Stream == null) return;
        if (player.Stream is AudioStreamOggVorbis ogg) ogg.Loop = true;
        var target = on ? onDb : -60f;
        player.VolumeDb = Mathf.MoveToward(player.VolumeDb, target, (float)(delta * 120));
        if (on && !player.Playing) { player.VolumeDb = -30; player.Play(); }
        if (!on && player.Playing && player.VolumeDb <= -59) player.Stop();
    }

    /// <summary>
    /// Once a frame: sounds for what the craft you fly is doing (and crashes near it). Null craft
    /// (title screen) silences the ship sounds; the music carries on.
    /// </summary>
    public void Watch(SectorState? sector, CraftState? pilot, double delta, double timeSpeed = 1)
    {
        _clock += delta;
        StepMusic(sector is null || pilot is null ? MusicMood.Calm : sector.MoodFor(pilot), delta); // #129
        if (sector is not null && pilot is not null)
        {
            Hear(sector, pilot, timeSpeed);
            Announce(sector);
            Alerts(sector);
        }
        if (sector is null || pilot is null)
        {
            foreach (var name in _loops.Keys.ToList()) Loop(name, false, 0, delta);
            _watched = CraftId.None;
            return;
        }
        if (pilot.Id != _watched)
        {
            // A different craft (or a loaded game): start from its state, without sounds for the switch.
            _watched = pilot.Id;
            (_wasDocked, _wasLanded, _wasAnchored, _wasCruising) = (pilot.IsDocked, pilot.IsLanded, pilot.IsAnchored, pilot.Cruising);
            (_wasPowerShort, _wasStopped, _wasJumps, _wasPartsLost) = (pilot.PowerShort, pilot.CrewShortReason != null, pilot.Jumps, pilot.PartsLost);
            _lastImpactTick = sector.RecentImpacts.Count > 0 ? sector.RecentImpacts.Max(i => i.Tick) : -1;
        }

        if (pilot.Jumps != _wasJumps) Effect("warp");
        else if (pilot.IsDocked && !_wasDocked) Effect("dock");
        else if (!pilot.IsDocked && _wasDocked) Effect("undock");
        if (pilot.IsLanded && !_wasLanded) Effect("land");
        if (pilot.IsAnchored != _wasAnchored) Effect("anchor");
        if (pilot.Cruising && !_wasCruising) Effect("cruise_start");
        if (!pilot.Cruising && _wasCruising) Effect("cruise_stop");
        var stopped = pilot.CrewShortReason != null;
        if ((pilot.PowerShort && !pilot.LegacyFreePower && !_wasPowerShort) || (stopped && !_wasStopped)) Effect("alarm");
        if (pilot.PartsLost > _wasPartsLost) Effect("part_lost");

        // Crashes: the craft you fly, or anything close to it.
        foreach (var impact in sector.RecentImpacts)
        {
            if (impact.Tick <= _lastImpactTick) continue;
            _lastImpactTick = impact.Tick;
            if (impact.Craft == pilot.Id) { Effect("impact"); continue; }
            var other = sector.Crafts.FirstOrDefault(c => c.Id == impact.Craft);
            if (other != null && other.Position.DistanceTo(pilot.Position) < NearbyImpactM) Effect("impact", -8);
        }

        (_wasDocked, _wasLanded, _wasAnchored, _wasCruising) = (pilot.IsDocked, pilot.IsLanded, pilot.IsAnchored, pilot.Cruising);
        (_wasPowerShort, _wasStopped, _wasJumps, _wasPartsLost) = (pilot.PowerShort && !pilot.LegacyFreePower, stopped, pilot.Jumps, pilot.PartsLost);

        // Continuous sounds.
        var controls = pilot.Controls.Clamped();
        var thrusting = (controls.Thrusting || (controls.Brake && pilot.Velocity.Length > 0.5)) && !pilot.PowerShort && !pilot.IsDocked && !pilot.Cruising && !stopped
                        && (pilot.Stats.TotalThrustN > 0 || pilot.TugThrustN > 0);
        Loop("thrust_loop", thrusting, -6, delta);
        Loop("drill_loop", sector.MiningMaterial(pilot) != null && pilot.FreeCargoKg > 0 && !pilot.PowerShort, -10, delta);
        Loop("cruise_loop", pilot.Cruising, -12, delta);
    }
}
