using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Drawing celestial bodies (#71–#74). Each visible body is one rectangle drawn with
/// <c>assets/shaders/body.gdshader</c>: its disc at true scale, the pixel surface from
/// <c>assets/art/bodies</c>, light from its star, craters (#28) and ore at surveyed sites (#70).
/// View only: everything comes from the simulation's state each frame, so mining, shrinking and
/// dismantling need nothing extra. Bodies lie under <see cref="Main"/>'s own drawing, over a
/// backdrop with the starfield and orbits.
/// </summary>
public partial class Main
{
    private const string BodyArtFolder = "res://assets/art/bodies";
    private const string BodyShaderPath = "res://assets/shaders/body.gdshader";

    /// <summary>Matches the shader's limits (and <see cref="CelestialBodyState.MaxCraters"/>, <see cref="SurfaceSite.MaxSites"/>).</summary>
    private const int ShaderCraters = 64, ShaderSites = 24;

    /// <summary>Smaller bodies on screen are a plain dot (stars always glow).</summary>
    private const float MinShadedRadiusPx = 4f;

    /// <summary>How far a star's corona reaches, in star radii.</summary>
    private const float CoronaRadii = 1.75f;

    private static readonly Color RockyPlanetColor = new(0.66f, 0.50f, 0.40f);

    private Node2D _backdrop = null!;
    private Node2D _bodyLayer = null!;
    private Shader? _bodyShader;
    private Texture2D? _siteMarker, _claimFlag, _starGlow;
    private readonly Dictionary<string, (Texture2D Texture, Vector3 Mean)> _surfaces = new();
    private readonly Dictionary<string, Color> _materialColors = new();
    private readonly Dictionary<BodyId, BodyVisual> _bodyVisuals = new();
    private readonly HashSet<BodyId> _bodiesShown = new();
    private SectorState? _bodyVisualsOf;

    /// <summary>One body's canvas item and what was last sent to it.</summary>
    private sealed class BodyVisual
    {
        public Rid Item;
        public ShaderMaterial Material = null!;
        public string Surface = "";
        public int Sites = -1;
        public int Craters;
        public bool Visible;
        public readonly Vector4[] CraterData = new Vector4[ShaderCraters], CraterColors = new Vector4[ShaderCraters];
    }

    /// <summary>Makes the layers under this node, and loads the body art.</summary>
    private void SetUpBodyArt()
    {
        _backdrop = new Node2D { Name = "Backdrop", ShowBehindParent = true };
        _backdrop.Draw += DrawBackdrop;
        AddChild(_backdrop);
        _bodyLayer = new Node2D { Name = "Bodies", ShowBehindParent = true };
        AddChild(_bodyLayer);

        if (ResourceLoader.Exists(BodyShaderPath)) _bodyShader = GD.Load<Shader>(BodyShaderPath);
        foreach (var name in new[] { "rock", "ice", "planet", "regolith", "gas_bands", "star_surface", "ore_veins" })
        {
            if (BodyTexture(name) is not { } texture || texture.GetImage() is not { } image) continue;
            if (image.IsCompressed()) image.Decompress();
            float r = 0, g = 0, b = 0;
            for (var y = 0; y < image.GetHeight(); y++)
                for (var x = 0; x < image.GetWidth(); x++)
                {
                    var c = image.GetPixel(x, y);
                    r += c.R; g += c.G; b += c.B;
                }
            var count = image.GetWidth() * image.GetHeight();
            _surfaces[name] = (texture, new Vector3(Mathf.Max(0.01f, r / count), Mathf.Max(0.01f, g / count), Mathf.Max(0.01f, b / count)));
        }
        _siteMarker = BodyTexture("site_marker");
        _claimFlag = BodyTexture("claim_flag");
        _starGlow = BodyTexture("star_glow");
    }

    private static Texture2D? BodyTexture(string name)
    {
        var path = $"{BodyArtFolder}/{name}.png";
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    public override void _ExitTree() => FreeBodyVisuals();

    private void FreeBodyVisuals()
    {
        foreach (var visual in _bodyVisuals.Values) RenderingServer.FreeRid(visual.Item);
        _bodyVisuals.Clear();
        _bodiesShown.Clear();
    }

    /// <summary>The starfield and the orbits: under everything else.</summary>
    private void DrawBackdrop()
    {
        DrawStarfield(_backdrop);
        if (InMenu || (_mapMode && _galaxyView)) return;
        var renderTimeS = RenderTimeS;
        foreach (var body in _sector.Bodies)
        {
            // Skip orbits too small to see, and huge ones (from close up they're a straight line off-screen).
            // A star's place among the stars isn't an orbit to draw.
            if (body.Kind == BodyKind.Star || body.Orbit is not { } orbit) continue;
            var orbitPx = (float)orbit.RadiusM * _pixelsPerMetre;
            if (orbitPx is > 3f and < 200_000f)
                _backdrop.DrawArc(ToPixels(_sector.GetPositionAt(orbit.Parent, renderTimeS)), orbitPx, 0, Mathf.Tau, 512, OrbitColor);
        }
    }

    /// <summary>Interpolated time: smooth motion between 20 Hz ticks without touching the state.</summary>
    private double RenderTimeS => (_sector.Tick + _warp.Alpha) * FixedStepClock.TickSeconds;

    /// <summary>Hides every body (menus and the galaxy view).</summary>
    private void HideBodies()
    {
        foreach (var visual in _bodyVisuals.Values) SetVisible(visual, false);
    }

    private static void SetVisible(BodyVisual visual, bool visible)
    {
        if (visual.Visible == visible) return;
        visual.Visible = visible;
        RenderingServer.CanvasItemSetVisible(visual.Item, visible);
    }

    private void DrawBodies(double renderTimeS)
    {
        if (!ReferenceEquals(_bodyVisualsOf, _sector)) // a new or loaded game
        {
            FreeBodyVisuals();
            _bodyVisualsOf = _sector;
        }
        _bodiesShown.Clear();
        foreach (var body in _sector.Bodies) DrawBody(body, renderTimeS);
        foreach (var (id, visual) in _bodyVisuals)
            if (!_bodiesShown.Contains(id)) SetVisible(visual, false); // off-screen, or dismantled
    }

    private static Color StarTint(CelestialBodyState star) =>
        star.StarClass switch { StarClass.Red => new Color(1f, 0.55f, 0.4f), StarClass.Blue => new Color(0.6f, 0.75f, 1f), _ => StarColor };

    /// <summary>Which surface a body has: by its kind and bulk, never by its ore (which a survey has to find, #74).</summary>
    private static string SurfaceOf(CelestialBodyState body) => body.Kind switch
    {
        BodyKind.Star => "star_surface",
        BodyKind.GasGiant => "gas_bands",
        _ when body.BulkMaterialId == CelestialBodyState.IceMaterial => "ice",
        BodyKind.Planet => "planet",
        BodyKind.Moon => "regolith",
        _ => "rock",
    };

    /// <summary>A fixed number 0..1 from a body's id, different for each <paramref name="salt"/> (looks only).</summary>
    private static float BodyVariation(BodyId id, uint salt) =>
        (float)new DeterministicRandom((ulong)id.Value * 0x9E3779B97F4A7C15UL ^ salt).NextDouble();

    /// <summary>A body's own colour: its kind's, shifted a little so neighbours differ (#71).</summary>
    private static Color BodyColor(CelestialBodyState body)
    {
        if (body.Kind == BodyKind.Star) return StarTint(body);
        var color = SurfaceOf(body) switch
        {
            "gas_bands" => GasGiantColor,
            "ice" => IceColor,
            "planet" => body.IsMineable ? RockyPlanetColor : PlanetColor, // Home keeps its blue
            "regolith" => MoonColor,
            _ => AsteroidColor,
        };
        if (body.Kind == BodyKind.Planet && !body.IsMineable) return color;
        var hue = color.H + (BodyVariation(body.Id, 1) - 0.5f) * 0.05f;
        return Color.FromHsv(hue - Mathf.Floor(hue), Mathf.Clamp(color.S * (0.8f + 0.4f * BodyVariation(body.Id, 2)), 0, 1),
            Mathf.Clamp(color.V * (0.88f + 0.22f * BodyVariation(body.Id, 3)), 0, 1));
    }

    private void DrawBody(CelestialBodyState body, double renderTimeS)
    {
        if (!MapShows(body)) return; // filtered out of the map (#104)
        var centre = ToPixels(_sector.GetPositionAt(body.Id, renderTimeS));
        var isStar = body.Kind == BodyKind.Star;
        var radiusPx = Mathf.Max(isStar ? 3f : 2f, (float)body.RadiusM * _pixelsPerMetre);
        var half = GetViewportRect().Size / 2;
        var view = new Rect2(-half, half * 2);
        var reachPx = isStar ? radiusPx * CoronaRadii + 2 : radiusPx + 1;
        var screen = new Rect2(centre - new Vector2(reachPx, reachPx), new Vector2(reachPx, reachPx) * 2).Intersection(view);
        if (screen.HasArea())
        {
            if (_bodyShader is null || !_surfaces.ContainsKey(SurfaceOf(body)) || (radiusPx < MinShadedRadiusPx && !isStar))
                DrawCircle(centre, radiusPx, BodyColor(body)); // a dot, or no art
            else ShadeBody(body, centre, radiusPx, screen, renderTimeS);
        }
        // Labels and markers outside the disc can be on screen when the disc isn't.
        if (!view.Grow(radiusPx + 400).HasPoint(centre)) return;
        DrawAnimals(body, centre, radiusPx, renderTimeS);
        DrawSites(body, centre, radiusPx);
        DrawClaim(body, centre, radiusPx);

        // Zoomed out, a moon's or rock's name would sit on top of its planet's: only label bodies whose
        // orbit is big enough on screen to tell apart (and always the target).
        var orbitPx = body.Orbit is { } o && !isStar ? (float)o.RadiusM * _pixelsPerMetre : 0f;
        var isTarget = _player is { } p && p.TargetBodyId == body.Id;
        if (body.Orbit is not null && orbitPx < 28f && !isTarget) return;
        var name = body.AnySiteSurveyed ? body.Name : $"{body.Name} (unsurveyed)";
        if (SitesLabelled(body, radiusPx)) // the rim carries site labels (#70): the name goes in the middle instead
        {
            var at = centre - new Vector2(ThemeDB.FallbackFont.GetStringSize(name, HorizontalAlignment.Left, -1, 14).X / 2, -5);
            DrawString(ThemeDB.FallbackFont, at + Vector2.One, name, HorizontalAlignment.Left, -1, 14, new Color(0, 0, 0, 0.8f)); // readable on any surface
            DrawString(ThemeDB.FallbackFont, at, name, HorizontalAlignment.Left, -1, 14, NameColor);
        }
        else DrawString(ThemeDB.FallbackFont, centre + new Vector2(radiusPx + 4, 4), name, HorizontalAlignment.Left, -1, 12, NameColor);
    }

    /// <summary>Sends one visible body to its canvas item.</summary>
    private void ShadeBody(CelestialBodyState body, Vector2 centre, float radiusPx, Rect2 screen, double renderTimeS)
    {
        _bodiesShown.Add(body.Id);
        if (!_bodyVisuals.TryGetValue(body.Id, out var visual))
        {
            visual = new BodyVisual { Item = RenderingServer.CanvasItemCreate(), Material = new ShaderMaterial { Shader = _bodyShader }, Visible = true };
            RenderingServer.CanvasItemSetParent(visual.Item, _bodyLayer.GetCanvasItem());
            RenderingServer.CanvasItemSetMaterial(visual.Item, visual.Material.GetRid());
            RenderingServer.CanvasItemSetDrawIndex(visual.Item, (int)body.Id.Value);
            visual.Material.SetShaderParameter("kind", body.Kind switch { BodyKind.Star => 2, BodyKind.GasGiant => 1, _ => 0 });
            visual.Material.SetShaderParameter("base_color", Rgb(BodyColor(body)));
            visual.Material.SetShaderParameter("tile_offset", new Vector2(Mathf.Floor(BodyVariation(body.Id, 4) * 128), Mathf.Floor(BodyVariation(body.Id, 5) * 128)));
            if (_surfaces.TryGetValue("ore_veins", out var veins)) visual.Material.SetShaderParameter("veins", veins.Texture);
            _bodyVisuals[body.Id] = visual;
        }
        SetVisible(visual, true);
        var material = visual.Material;
        var surface = SurfaceOf(body);
        if (visual.Surface != surface)
        {
            visual.Surface = surface;
            material.SetShaderParameter("surface", _surfaces[surface].Texture);
            material.SetShaderParameter("surface_mean", _surfaces[surface].Mean);
        }
        material.SetShaderParameter("centre_px", centre);
        material.SetShaderParameter("ppm", _pixelsPerMetre);
        material.SetShaderParameter("radius_m", radiusPx / _pixelsPerMetre);
        if (body.Kind != BodyKind.Star && _sector.StarOf(body.Id) is { } star)
        {
            var d = _sector.GetPositionAt(star.Id, renderTimeS) - _sector.GetPositionAt(body.Id, renderTimeS);
            var length = System.Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (length > 0) material.SetShaderParameter("light_dir", new Vector2((float)(d.X / length), (float)(d.Y / length)));
        }
        SendSites(body, visual);
        SendCraters(body, visual, centre);
        RenderingServer.CanvasItemClear(visual.Item);
        RenderingServer.CanvasItemAddRect(visual.Item, screen, Colors.White);
    }

    private static Vector3 Rgb(Color c) => new(c.R, c.G, c.B);

    private Color MaterialColor(string id) => _materialColors.GetValueOrDefault(id, Colors.Gray);

    /// <summary>The ore under surveyed sites (#74), biggest two deposits; only resent when it changes.</summary>
    private void SendSites(CelestialBodyState body, BodyVisual visual)
    {
        if (body.Sites.Count == 0) return;
        var hash = new System.HashCode();
        foreach (var site in body.Sites)
        {
            hash.Add(site.Surveyed);
            hash.Add((int)(site.Flora * 64));
            if (!site.Surveyed) continue;
            var (first, second) = TopOres(site);
            hash.Add(first);
            hash.Add(second);
        }
        var state = hash.ToHashCode() & int.MaxValue;
        if (visual.Sites == state) return;
        visual.Sites = state;
        var ore = new Vector4[ShaderSites];
        var ore2 = new Vector4[ShaderSites];
        for (var i = 0; i < Mathf.Min(body.Sites.Count, ShaderSites); i++)
        {
            if (!body.Sites[i].Surveyed || TopOres(body.Sites[i]) is not ({ } first, var second)) continue;
            ore[i] = OreVector(first);
            ore2[i] = OreVector(second ?? first);
        }
        visual.Material.SetShaderParameter("site_count", Mathf.Min(body.Sites.Count, ShaderSites));
        visual.Material.SetShaderParameter("site_ore", ore);
        var flora = new float[ShaderSites]; // native plants (#92)
        for (var i = 0; i < Mathf.Min(body.Sites.Count, ShaderSites); i++) flora[i] = (float)body.Sites[i].Flora;
        visual.Material.SetShaderParameter("has_flora", body.HasLife);
        visual.Material.SetShaderParameter("site_flora", flora);
        visual.Material.SetShaderParameter("flora_color", Rgb(Color.FromHsv(0.2f + 0.3f * BodyVariation(body.Id, 6), 0.55f, 0.55f)));
        visual.Material.SetShaderParameter("site_ore2", ore2);

        Vector4 OreVector(string material)
        {
            var c = MaterialColor(material);
            return new Vector4(c.R, c.G, c.B, 1);
        }
    }

    /// <summary>A site's two largest deposits by mass.</summary>
    private static (string? First, string? Second) TopOres(SurfaceSite site)
    {
        string? first = null, second = null;
        long firstKg = 0, secondKg = 0;
        foreach (var (material, kg) in site.DepositsKg)
        {
            if (kg > firstKg) { (second, secondKg) = (first, firstKg); (first, firstKg) = (material, kg); }
            else if (kg > secondKg) (second, secondKg) = (material, kg);
        }
        return (first, second);
    }

    /// <summary>The craters (#72) big enough to see and on screen: each shows its site's ore, or the body's own rock or ice.</summary>
    private void SendCraters(CelestialBodyState body, BodyVisual visual, Vector2 centre)
    {
        var count = 0;
        if (body.Craters.Count > 0)
        {
            var half = GetViewportRect().Size / 2;
            var view = new Rect2(-half, half * 2);
            foreach (var crater in body.Craters)
            {
                var radius = (float)body.CraterRadiusM(crater.MassKg);
                var radiusPx = radius * _pixelsPerMetre;
                if (radiusPx < 1.5f || count >= ShaderCraters) continue; // too small to see at this zoom
                var (sin, cos) = Mathf.SinCos((float)crater.BearingRad);
                var at = new Vector2(cos, sin) * (float)body.RadiusM;
                if (!view.Grow(radiusPx * 1.2f).HasPoint(centre + at * _pixelsPerMetre)) continue;
                var ore = body.SiteAt(crater.BearingRad) is { Surveyed: true } site ? TopOres(site).First : null;
                var color = ore is null ? Colors.Black : MaterialColor(ore);
                visual.CraterData[count] = new Vector4(at.X, at.Y, radius, 0);
                visual.CraterColors[count] = new Vector4(color.R, color.G, color.B, ore is null ? 0 : 0.8f);
                count++;
            }
        }
        if (count == 0 && visual.Craters == 0) return;
        visual.Craters = count;
        visual.Material.SetShaderParameter("crater_count", count);
        if (count == 0) return;
        visual.Material.SetShaderParameter("craters", visual.CraterData);
        visual.Material.SetShaderParameter("crater_colors", visual.CraterColors);
    }

    private static readonly Color SiteTickColor = new(1f, 1f, 1f, 0.45f);
    private static readonly Color SiteLabelColor = new(0.85f, 0.9f, 1f, 0.85f);
    private static readonly Color SiteUnknownColor = new(0.7f, 0.7f, 0.75f, 0.55f);

    /// <summary>Sites get labels once each is wide enough on screen.</summary>
    private static bool SitesLabelled(CelestialBodyState body, float radiusPx) =>
        body.Sites.Count > 0 && radiusPx >= 40f && radiusPx * (float)body.Sites[0].WidthRad > 70f;

    /// <summary>
    /// Surface sites (#70, #74): a pixel stake on the rim where each site starts (dim until that site is surveyed),
    /// and the site's letter with its ore (or "?" until surveyed) just outside the rim, once a site is wide
    /// enough on screen to label.
    /// </summary>
    private void DrawSites(CelestialBodyState body, Vector2 centre, float radiusPx)
    {
        if (body.Sites.Count == 0 || radiusPx < 40f) return;
        var view = new Rect2(-GetViewportRect().Size / 2, GetViewportRect().Size).Grow(40);
        var labelled = SitesLabelled(body, radiusPx);
        var size = radiusPx < 120f ? 8f : 16f;
        foreach (var site in body.Sites)
        {
            var (sin, cos) = Mathf.SinCos((float)site.StartRad);
            var edge = new Vector2(cos, sin);
            var at = centre + edge * radiusPx;
            if (view.HasPoint(at))
            {
                if (_siteMarker is { } marker)
                {
                    DrawSetTransform(at, (float)site.StartRad + Mathf.Pi / 2, Vector2.One); // up is away from the body
                    DrawTextureRect(marker, new Rect2(-size / 2, -size + size / 8, size, size), false, site.Surveyed ? Colors.White : new Color(0.6f, 0.6f, 0.65f, 0.7f));
                    DrawSetTransform(Vector2.Zero, 0, Vector2.One);
                }
                else DrawLine(at - edge * 8, at + edge * 12, SiteTickColor, 2);
            }
            if (!labelled) continue;
            var (cs, cc) = Mathf.SinCos((float)site.CentreRad);
            var labelAt = centre + new Vector2(cc, cs) * (radiusPx + 22);
            if (!view.HasPoint(labelAt)) continue;
            var ore = !site.Surveyed ? "?" : (site.DepositsKg.Count == 0 ? "no ore" : string.Join(", ", site.DepositsKg.Keys.Select(k => k.Replace("_ore", ""))))
                                              + (site.IsHot ? ", HOT" : "") // geothermal hot spot (#77)
                                              + (site.Flora > 0 ? $", plants {site.Flora * 100:0}%" : "")
                                              + (site.Fauna > 0 ? $", {body.AnimalName}s {site.Fauna * 100:0}%" : ""); // #92, #93
            DrawString(ThemeDB.FallbackFont, labelAt - new Vector2(20, 0), $"{SurfaceSite.Letter(site.Index)}: {ore}",
                HorizontalAlignment.Left, -1, 12, site.Surveyed ? SiteLabelColor : SiteUnknownColor);
        }
    }

    /// <summary>Animals close up (#93): a few little shapes wandering near the rim of each site, more where there are more. View only.</summary>
    private void DrawAnimals(CelestialBodyState body, Vector2 centre, float radiusPx, double renderTimeS)
    {
        if (!body.HasAnimals || _pixelsPerMetre < 0.4f) return;
        var view = new Rect2(-GetViewportRect().Size / 2, GetViewportRect().Size).Grow(10);
        var hue = BodyVariation(body.Id, 7);
        foreach (var site in body.Sites)
        {
            if (site.Fauna <= 0) continue;
            var count = 3 + (int)(site.Fauna * 60);
            for (var i = 0; i < count; i++)
            {
                var wander = renderTimeS / (40 + i % 13) + i * 1.7;
                var along = 0.5 + 0.48 * System.Math.Sin(i * 2.399963 + 0.15 * System.Math.Sin(wander));
                var depth = 12 + i * 37 % 260 + 10 * System.Math.Sin(wander * 0.7);
                var angle = site.StartRad + along * site.WidthRad;
                var at = centre + new Vector2((float)System.Math.Cos(angle), (float)System.Math.Sin(angle)) * (radiusPx - (float)depth * _pixelsPerMetre);
                if (!view.HasPoint(at)) continue;
                var size = Mathf.Clamp(2.2f * _pixelsPerMetre, 2f, 9f);
                var color = Color.FromHsv(hue, 0.35f, i % 3 == 0 ? 0.95f : 0.8f);
                var facing = System.Math.Cos(wander) >= 0 ? 1 : -1;
                DrawRect(new Rect2((at - new Vector2(size, size / 2)).Round(), new Vector2(size * 2, size)), new Color(0.06f, 0.06f, 0.08f));
                DrawRect(new Rect2((at - new Vector2(size, size / 2)).Round() + Vector2.One * 0.5f, new Vector2(size * 2 - 1, size - 1)), color);
                DrawRect(new Rect2((at + new Vector2(facing * size * 0.9f - size * 0.3f, -size * 0.8f)).Round(), new Vector2(size * 0.8f, size * 0.8f)), color);
            }
        }
    }

    /// <summary>A small pennant above bodies in a civilization's system (#36, #74), tinted by how it feels about you.</summary>
    private void DrawClaim(CelestialBodyState body, Vector2 centre, float radiusPx)
    {
        if (_claimFlag is not { } flag || radiusPx < 6f || _sector.ClaimantOf(body.Id) is not { } owner) return;
        var tint = owner.Disposition switch
        {
            Disposition.Hostile => Palette.Bad,
            Disposition.Wary => Palette.Warning,
            Disposition.Friendly => Palette.Good,
            _ => Colors.White,
        };
        var size = radiusPx < 40f ? 8f : 16f;
        DrawTextureRect(flag, new Rect2((centre + new Vector2(-size / 4, -radiusPx - size - 2)).Round(), new Vector2(size, size)), false, tint);
    }

    /// <summary>A star in the galaxy view (#73): a pixel glow in its class colour, gently pulsing.</summary>
    private void DrawGalaxyStar(CelestialBodyState star, Vector2 at, float radiusPx)
    {
        if (_starGlow is not { } glow) { DrawCircle(at, radiusPx, StarTint(star)); return; }
        var pulse = 0.94f + 0.06f * Mathf.Sin(Time.GetTicksMsec() / 1000f * Mathf.Tau / 6 + star.Id.Value);
        var size = Mathf.Round(radiusPx / 10 * glow.GetWidth()); // the disc is 10 of the art's pixels in radius
        DrawTextureRect(glow, new Rect2(at - new Vector2(size, size) / 2, new Vector2(size, size)), false, StarTint(star) * new Color(pulse, pulse, pulse));
    }

    /// <summary>
    /// Dev/test hook: <c>--view-body=Cinder [--view-bearing=45]</c> centres the view on a body (or on its rim at
    /// that bearing), <c>--survey-all</c> surveys everything, <c>--dig</c> adds test craters (no mass removed).
    /// </summary>
    private void SetUpBodyView(string[] args)
    {
        if (System.Array.IndexOf(args, "--add-life") >= 0) foreach (var b in _sector.Bodies.Where(b => b.IsMineable && b.Kind == BodyKind.Planet)) _sector.DebugAddLife(b.Id); // #92
        if (System.Array.IndexOf(args, "--survey-all") >= 0) _sector.DebugSurveyAll();
        if (args.FirstOrDefault(a => a.StartsWith("--view-body=")) is not { } arg) return;
        var name = arg["--view-body=".Length..];
        // "first-system" is the star of the first system beyond home (with --demo-civ), "+name" a body by the end of its name.
        _devViewBody = _sector.Bodies.FirstOrDefault(b => b.Name == name)
                       ?? (name == "first-system" ? _sector.Stars.LastOrDefault() : _sector.Bodies.LastOrDefault(b => b.Name.EndsWith(name.TrimStart('+'))));
        if (args.FirstOrDefault(a => a.StartsWith("--view-bearing=")) is { } bearing
            && double.TryParse(bearing["--view-bearing=".Length..], System.Globalization.CultureInfo.InvariantCulture, out var degrees))
            _devViewBearing = degrees * System.Math.PI / 180;
        if (System.Array.IndexOf(args, "--dig") < 0 || _devViewBody is not { } body) return;
        var at = _devViewBearing ?? 0;
        var mass = body.RadiusM * body.RadiusM * body.RadiusM * body.DensityKgPerM3;
        _sector.DebugDigCrater(body.Id, at, (long)(mass * 0.0005));
        _sector.DebugDigCrater(body.Id, at + 0.12, (long)(mass * 0.0003)); // overlaps the first
        _sector.DebugDigCrater(body.Id, at - 0.35, (long)(mass * 0.0001));
        _sector.DebugDigCrater(body.Id, at + 1.2, (long)(mass * 0.003));
    }

    private CelestialBodyState? _devViewBody;
    private double? _devViewBearing;

    private void ApplyDevView()
    {
        if (_devViewBody is not { } body || InMenu) return;
        _viewCentre = _sector.GetPosition(body.Id);
        if (_devViewBearing is { } b) _viewCentre += new Vector2D(System.Math.Cos(b), System.Math.Sin(b)) * body.RadiusM;
    }
}
