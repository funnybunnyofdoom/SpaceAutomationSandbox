using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Effects (#130): pixel sprites for what happens, from the sector's noises and what craft are doing. Render-only: nothing here is
/// saved or changes the game. They live in sim time (paused when the game is, faster with time warp), are drawn into the same
/// sprite batch as the parts, are capped in number and culled off screen, and come in full, reduced (flames, flashes and
/// explosions only) or off. Nothing lights the whole screen, and nothing blinks more than three times a second.
/// </summary>
public partial class Main
{
    private const int MostEffects = 400, MostNewPerFrame = 40;

    private struct Fx
    {
        public string Sprite;
        public int Frames;
        public Vector2D At, Velocity;
        public double Born, Life;
        public float SizeM, Spin, MinPx;
        public Color Tint;
        public bool Fades;
    }

    private readonly List<Fx> _fx = new();
    private long _fxHeard = -1;
    private SectorState? _fxOf;
    private double _fxLast, _fxSim;
    private readonly Random _fxRandom = new(130);

    private bool FullEffects => _settings.Effects == 0;
    private bool AnyEffects => _settings.Effects < 2 && PartSprites.Atlas is not null;

    private static readonly Color FlameChemical = new(1f, 0.72f, 0.32f), FlameIon = new(0.5f, 0.75f, 1f), FlameMethalox = new(0.6f, 0.8f, 1f),
        FlameNuclear = new(0.85f, 1f, 0.9f), FlameAntimatter = new(0.85f, 0.55f, 1f);

    /// <summary>A light tint by race for a craft's parts (#130); wrecks are greyed.</summary>
    private Color HullTint(CraftState craft)
    {
        if (craft.IsWreck) return new Color(0.62f, 0.6f, 0.58f);
        var race = SectorState.IsPlayers(craft) ? _sector.Race : _sector.FactionById(craft.FactionId)?.Race;
        return race is { HullTint.Length: > 0 } r && Color.HtmlIsValid(r.HullTint) ? Colors.White.Lerp(new Color(r.HullTint), 0.55f) : Colors.White;
    }

    /// <summary>A flickering pixel flame below an engine, tinted by its kind and as long as the throttle; false: none (draw the plain one).</summary>
    private bool Flame(string partId, CraftState craft, Vector2 top, float length, float cellPx, Func<Vector2, Vector2> world)
    {
        if (!AnyEffects || cellPx < 3f) return false;
        var frame = (int)(Time.GetTicksMsec() / 90 + (ulong)craft.Id.Value) % 3; // ~11 changes a second, gentle: it only flickers in length
        if (!PartSprites.TryCorners($"fx:flame_{frame}", 0, out var a, out var b, out var c, out var d)) return false;
        var tint = partId switch
        {
            "ion_drive" => FlameIon, "methalox_rocket" => FlameMethalox, "nuclear_rocket" => FlameNuclear, "antimatter_drive" => FlameAntimatter, _ => FlameChemical,
        };
        var throttle = (float)Math.Clamp(craft.Controls.Throttle, 0.25, 1);
        var half = cellPx * 0.45f;
        var long_ = cellPx * length * throttle * 1.2f;
        BatchSprite((world(top + new Vector2(-half, 0)), world(top + new Vector2(half, 0)), world(top + new Vector2(half, long_)), world(top + new Vector2(-half, long_))), (a, b, c, d), tint);
        return true;
    }

    /// <summary>A missile as a sprite pointing the way it flies.</summary>
    private bool MissileSprite(Vector2 at, Vector2 direction)
    {
        if (!AnyEffects || !PartSprites.TryCorners("fx:missile", 0, out var a, out var b, out var c, out var d)) return false;
        var size = Mathf.Max(10f, 6 * _pixelsPerMetre);
        Sprite(at, size, Mathf.Atan2(direction.X, -direction.Y), (a, b, c, d), Colors.White);
        FlushBatch();
        return true;
    }

    private void Sprite(Vector2 at, float size, float angle, (Vector2, Vector2, Vector2, Vector2) uv, Color tint)
    {
        var (sin, cos) = Mathf.SinCos(angle);
        Vector2 Turn(float x, float y) => at + new Vector2(x * cos - y * sin, x * sin + y * cos) * size / 2;
        BatchSprite((Turn(-1, -1), Turn(1, -1), Turn(1, 1), Turn(-1, 1)), uv, tint);
    }

    private void Spawn(string sprite, int frames, Vector2D at, double life, float sizeM, Color tint, Vector2D velocity = default, float spin = 0, bool fades = true, float minPx = 6)
    {
        if (_fx.Count >= MostEffects) return;
        _fx.Add(new Fx { Sprite = sprite, Frames = frames, At = at, Velocity = velocity, Born = _fxLast, Life = life, SizeM = sizeM, Tint = tint, Spin = spin, Fades = fades, MinPx = minPx });
    }

    private Vector2D Jitter(double metres) => new((_fxRandom.NextDouble() - 0.5) * 2 * metres, (_fxRandom.NextDouble() - 0.5) * 2 * metres);

    /// <summary>New effects from the sector's noises, then from what visible craft are doing; then draws them all.</summary>
    private void DrawEffects(double renderTimeS)
    {
        if (!ReferenceEquals(_fxOf, _sector))
        {
            // Another game: from what happens now on (this tick's noises too).
            _fxOf = _sector;
            _fx.Clear();
            _fxHeard = _sector.NoiseCount;
            while (_fxHeard > Math.Max(0, _sector.NoiseCount - SectorState.NoisesKept) && _sector.NoiseAt(_fxHeard - 1).Tick >= _sector.Tick) _fxHeard--;
        }
        if (!AnyEffects) { _fx.Clear(); _fxHeard = _sector.NoiseCount; return; }
        // Ages go by real time, so an explosion is seen at any speed of time; motion goes with the game's time.
        var now = Time.GetTicksMsec() / 1000.0;
        var dt = Math.Max(0, renderTimeS - _fxSim);
        _fxSim = renderTimeS;
        var real = Math.Max(0, now - _fxLast);
        _fxLast = now;
        // From noises: newest first, a few a frame.
        var made = 0;
        for (var i = _sector.NoiseCount - 1; i >= Math.Max(_fxHeard, _sector.NoiseCount - SectorState.NoisesKept) && made < MostNewPerFrame; i--, made++)
        {
            var noise = _sector.NoiseAt(i);
            switch (noise.Kind)
            {
                case "gun_fire" or "laser_fire" or "point_defense" or "missile_launch":
                    Spawn("flash", 2, noise.At, 0.12, 3, noise.Kind == "laser_fire" ? new Color(1f, 0.55f, 0.5f) : new Color(1f, 0.9f, 0.6f), fades: false);
                    break;
                case "hit":
                    Spawn("flash", 2, noise.At, 0.1, 2.5f, new Color(1f, 0.8f, 0.5f), fades: false);
                    if (FullEffects) Spawn("spark", 3, noise.At, 0.35, 4, new Color(1f, 0.85f, 0.4f), fades: false);
                    break;
                case "shield_hit":
                    Spawn("spark", 3, noise.At, 0.3, 8, new Color(0.55f, 0.85f, 1f), fades: false);
                    break;
                case "explosion":
                    Spawn("explosion", 6, noise.At, 0.9, 16, Colors.White, fades: false, minPx: 14);
                    if (!FullEffects) break;
                    for (var k = 0; k < 6; k++)
                        Spawn($"debris_{_fxRandom.Next(4)}", 1, noise.At + Jitter(2), 6, 2.5f, Colors.White, Jitter(6), (float)(_fxRandom.NextDouble() - 0.5) * 4, minPx: 3);
                    for (var k = 0; k < 3; k++) Spawn("smoke", 3, noise.At + Jitter(4), 2.5, 12, new Color(1, 1, 1, 0.7f), Jitter(1.5));
                    break;
                case "warp" or "quantum_jump":
                    Spawn("glint", 1, noise.At, 0.6, 30, new Color(0.8f, 0.6f, 1f), minPx: 16);
                    break;
                case "ship_built" or "mega_stage":
                    Spawn("glint", 1, noise.At, 0.8, 12, new Color(0.7f, 1f, 0.9f), minPx: 12);
                    break;
            }
        }
        _fxHeard = _sector.NoiseCount;

        // From what craft on screen are doing: smoke from damage and wrecks, drill dust, cruise streaks (full only, at a gentle rate).
        if (FullEffects && dt > 0 && real > 0)
            foreach (var (craft, _, radius) in _visibleCrafts)
            {
                if (radius < 4) continue;
                var chance = real * 2;
                if (craft.IsWreck || craft.PartHealth.Count > 0 && Worst(craft) < 0.5)
                    if (_fxRandom.NextDouble() < chance) Spawn("smoke", 3, craft.Position + Jitter(craft.CollisionRadiusM * 0.5), 2, 6, new Color(1, 1, 1, 0.55f), Jitter(1) + craft.Velocity);
                if (craft.IsLanded && _sector.MiningMaterial(craft) is not null && _fxRandom.NextDouble() < chance * 2)
                {
                    var down = craft.Position - _sector.GetPosition(craft.LandedOnBodyId);
                    var foot = craft.Position - down * (craft.CollisionRadiusM / Math.Max(down.Length, 1));
                    Spawn("dust", 2, foot + Jitter(2), 1.2, 5, Colors.White, craft.Velocity + down * (1.5 / Math.Max(down.Length, 1)));
                }
                if (craft.Cruising && _fxRandom.NextDouble() < chance * 4)
                    Spawn("streak", 1, craft.Position - craft.Velocity * (0.02 + _fxRandom.NextDouble() * 0.03) + Jitter(craft.CollisionRadiusM), 0.4, 14, new Color(0.7f, 0.85f, 1f, 0.6f), craft.Velocity * 0.9);
            }

        // Move, age, draw.
        var view = new Rect2(-GetViewportRect().Size / 2, GetViewportRect().Size).Grow(64);
        for (var i = _fx.Count - 1; i >= 0; i--)
        {
            var fx = _fx[i];
            var age = (now - fx.Born) / fx.Life;
            if (age >= 1 || age < 0) { _fx.RemoveAt(i); continue; }
            fx.At += fx.Velocity * dt;
            _fx[i] = fx;
            var at = ToPixels(fx.At);
            var size = Mathf.Max(fx.MinPx * _uiRoot.Scale.X, fx.SizeM * _pixelsPerMetre);
            if (!view.HasPoint(at) || size < 2) continue;
            var name = fx.Frames > 1 ? $"fx:{fx.Sprite}_{Math.Min(fx.Frames - 1, (int)(age * fx.Frames))}" : fx.Sprite.StartsWith("debris", StringComparison.Ordinal) ? $"fx:{fx.Sprite}" : $"fx:{fx.Sprite}";
            if (!PartSprites.TryCorners(name, 0, out var a, out var b, out var c, out var d)) continue;
            var tint = fx.Tint;
            if (fx.Fades) tint.A *= (float)(1 - age);
            var angle = fx.Sprite == "streak" ? Mathf.Atan2((float)fx.Velocity.X, -(float)fx.Velocity.Y) : fx.Spin * (float)(now - fx.Born);
            Sprite(at, size, angle, (a, b, c, d), tint);
        }
        FlushBatch();

        static double Worst(CraftState craft)
        {
            var worst = 1.0;
            foreach (var h in craft.PartHealth.Values) worst = Math.Min(worst, h);
            return worst;
        }
    }

    /// <summary>Dev/test hook <c>--demo-fx</c>: an explosion, hits and a jump flash beside the ship you fly, and a damaged part smoking.</summary>
    private void DemoEffects()
    {
        if (_player is not { } p) return;
        _sector.DebugNoise("explosion", p.Position + new Vector2D(40, -10));
        for (var i = 0; i < 4; i++) _sector.DebugNoise("hit", p.Position + new Vector2D(-20 + i * 4, 22));
        _sector.DebugNoise("gun_fire", p.Position + new Vector2D(0, -12));
        _sector.DebugNoise("warp", p.Position + new Vector2D(-45, -30));
        _sector.DebugDamagePart(p.Id, p.Design.Cells[3].Pos, 0.3);
    }
}
