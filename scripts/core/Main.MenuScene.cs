using System;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Menu artwork (#98): behind the title screen a small world of its own runs, a station over a planet with ships about, different each
/// time. Nobody plays it and it's never saved. Saves get a picture, and menus the race's portrait.
/// </summary>
public partial class Main
{
    private const int PictureWidth = 320, PictureHeight = 180;

    private SectorState? _menuScene;
    private CraftId _menuStation = CraftId.None;
    private float _zoomBeforeMenu;
    private double _menuS;

    /// <summary>The title's world is what there is to draw.</summary>
    private bool ShowsMenuScene => InMenu && _menuScene is not null && ReferenceEquals(_sector, _menuScene);

    /// <summary>Starts the title's world. Without it (no data) there are just stars.</summary>
    private void OpenMenuScene()
    {
        if (ShowsMenuScene || _catalog is null || _starterDesign is null || _starterShipyard is null || _player is not null) return;
        try
        {
            var random = new Random();
            var seed = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--menu-seed=")) is { } arg ? ulong.Parse(arg["--menu-seed=".Length..]) : (ulong)random.NextInt64(1, 1_000_000_000); // dev: the same again
            random = new Random((int)(seed % int.MaxValue));
            _sector = _menuScene = MenuScene(seed, random);
            _warp.Paused = false;
            _warp.Speed = 1;
            _zoomBeforeMenu = _pixelsPerMetre;
            _menuS = 0;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException) { GD.PushWarning($"No menu scene: {e.Message}"); }
    }

    private void CloseMenuScene()
    {
        if (_menuScene is null) return;
        _menuScene = null;
        if (_zoomBeforeMenu > 0) _pixelsPerMetre = _zoomBeforeMenu;
    }

    private SectorState MenuScene(ulong seed, Random random)
    {
        var game = NewGame.Create(seed, _catalog!, _starterDesign!, _starterShipyard!);
        var sector = game.Sector;
        sector.DebugSurveyAll();
        var home = SectorGenerator.HomePlanet(sector);
        var yard = sector.GetCraft(game.ShipyardId);

        // A big station, low over the planet.
        var design = MenuStation(random);
        var station = new CraftState(sector.AllocateCraftId(), "Station", design, design.ComputeStats(_catalog!), Vector2D.Zero, random.Next(4) * Math.PI / 2) { LegacyNoCrew = true, LegacyFreePower = true };
        sector.AddCraft(station);
        var angle = -Math.PI / 2 - 0.75 + 0.9 * random.NextDouble(); // the planet below, or below and to the right
        Orbit(station, 215, angle);
        _menuStation = station.Id;
        sector.PilotCraftId = station.Id; // everything near it is simulated in full
        Orbit(yard, 430, angle + 0.012 + 0.01 * random.NextDouble());

        // Ships passing by at their own pace, and miners leaving for the rocks.
        var rocks = sector.Bodies.Where(b => b.IsMineable && b.Orbit?.Parent == home.Id && b.DepositsKg.Count > 0).ToList();
        var stats = _starterDesign!.ComputeStats(_catalog!);
        for (var i = 0; i < 16; i++)
        {
            var craft = new CraftState(sector.AllocateCraftId(), $"Ship {i}", _starterDesign, stats, station.Position, 0) { LegacyFreePower = true };
            sector.AddCraft(craft);
            var side = (random.NextDouble() - 0.5) * 900 / (home.RadiusM + 400);
            if (i < 10 || rocks.Count == 0)
            {
                Orbit(craft, 180 + 420 * random.NextDouble(), angle + side);
                craft.HeadingRad = craft.PreviousHeadingRad = angle + (random.Next(2) == 0 ? 0 : Math.PI); // along its orbit
                continue;
            }
            var out_ = new Vector2D(Math.Cos(angle + side), Math.Sin(angle + side));
            craft.Position = craft.PreviousPosition = sector.GetPosition(home.Id) + out_ * (home.RadiusM + 320 + 200 * random.NextDouble());
            craft.Velocity = station.Velocity;
            var rock = rocks[random.Next(rocks.Count)];
            sector.StartJob(craft.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25));
        }
        for (var i = 0; i < 2 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        return sector;

        void Orbit(CraftState craft, double altitude, double at)
        {
            var radius = home.RadiusM + altitude;
            // The angle is as of time zero.
            sector.PutOnRails(craft.Id, new CircularOrbit(home.Id, radius, SectorState.CircularOrbitPeriodS(home, radius, NewGame.ShipyardOrbitPeriodS), at));
        }
    }

    /// <summary>A station for the title: a spine with rings of habitats, tanks and solar wings.</summary>
    private static ShipDesign MenuStation(Random random)
    {
        var cells = new System.Collections.Generic.Dictionary<GridPos, string>();
        var wings = 2 + random.Next(3);
        var half = 8 * wings;
        for (var y = -half; y <= half; y++)
            for (var x = -1; x <= 1; x++) cells[new GridPos(x, y)] = x == 0 ? "hull" : (y + x) % 5 == 0 ? "battery" : "hull";
        for (var w = 0; w < wings; w++)
        {
            var y = -half + 8 + w * 16;
            var length = 8 + random.Next(10);
            for (var x = 2; x <= length; x++)
                foreach (var side in new[] { -1, 1 })
                {
                    cells[new GridPos(side * x, y)] = "hull";
                    foreach (var dy in new[] { -2, -1, 1, 2 }) if (x > 4) cells[new GridPos(side * x, y + dy)] = "solar_panel";
                }
            foreach (var side in new[] { -1, 1 })
                for (var i = 0; i < 6; i++)
                    for (var j = 0; j < 3; j++) cells[new GridPos(side * (2 + j), y + 5 + i)] = w % 2 == 0 ? "habitat" : i % 3 == 0 ? "farm" : "storage_silo";
        }
        cells[new GridPos(0, -half - 1)] = "docking_port";
        cells[new GridPos(0, 0)] = "fabricator";
        return new ShipDesign("menu_station", "Station", cells.Select(c => new DesignCell(c.Key, c.Value)));
    }

    /// <summary>Each frame of the title: its world goes on, and the view drifts slowly.</summary>
    private void MenuFrame(double delta)
    {
        if (!ShowsMenuScene) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var due = _warp.TicksDue(delta);
        for (var i = 0; i < due && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4; i++) _sector.Step();
        _menuS += delta;
        _camera.Position = Vector2.Zero;
        var screen = GetViewportRect().Size;
        _pixelsPerMetre = screen.Y / 720f * (1.15f + 0.12f * (float)Math.Sin(_menuS * 0.05));
        if (_sector.Crafts.FirstOrDefault(c => c.Id == _menuStation) is not { } station) return;
        var at = InterpolatedPosition(station);
        // The station to the right of the menu, drifting.
        _viewCentre = at + new Vector2D(-screen.X * 0.2 / _pixelsPerMetre + 18 * Math.Sin(_menuS * 0.031), 14 * Math.Sin(_menuS * 0.043));
    }

    private void DrawMenuScene()
    {
        DrawBodies(RenderTimeS);
        var screen = GetViewportRect().Size;
        var view = new Rect2(-screen / 2, screen).Grow(64);
        _visibleCrafts.Clear();
        foreach (var craft in _sector.Crafts)
            if (view.Grow((float)craft.CollisionRadiusM * _pixelsPerMetre).HasPoint(ToPixels(InterpolatedPosition(craft)))) DrawCraft(craft);
        FlushBatch();
    }

    // --- Pictures ---

    private Image? _lastPicture;

    /// <summary>A small picture of the game as it's drawn now, from its middle (the HUD is at the sides). Null when there's none to take.</summary>
    private Image? TakePicture()
    {
        var shot = GetViewport().GetTexture()?.GetImage();
        if (shot is null || shot.IsEmpty()) return null;
        var (width, height) = (shot.GetWidth(), shot.GetHeight());
        var w = Math.Min(width * 44 / 100, height * 70 / 100 * 16 / 9);
        var h = w * 9 / 16;
        if (w < 32 || h < 18) return null;
        var cut = shot.GetRegion(new Rect2I((width - w) / 2, (height - h) / 2, w, h));
        cut.Resize(PictureWidth, PictureHeight, Image.Interpolation.Lanczos);
        cut.Convert(Image.Format.Rgb8);
        return cut;
    }

    /// <summary>Puts a picture beside a save. A menu in the way: the one from before it opened. Never fails the save.</summary>
    private void SavePicture(string path)
    {
        try
        {
            var picture = ModalOpen || EditorOpen || _settingsPanel is { Visible: true } ? _lastPicture : TakePicture();
            SaveStore.WritePicture(path, picture?.SaveJpgToBuffer(0.85f));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { GD.PushWarning($"No picture for the save: {e.Message}"); }
    }

    private Texture2D? PortraitOf(string raceId) =>
        raceId.Length > 0 && _catalog is { } c && c.Races.TryGet(raceId, out var race) ? RaceLibrary.Portrait(race) : null;

    // --- Loading ---

    private static readonly string[] Tips =
    {
        "Hold [Brake] to match your target's motion. It's the easiest way to land or dock.",
        "Press [TargetSmart] to target what matters most right now.",
        "[Find] finds anything by name, in any system.",
        "Ctrl and a number keeps your target as a favourite; the number targets it again.",
        "Craft of up to 15 parts need no crew.",
        "Mining jobs go for repairs and come back on their own.",
        "Stations can post requests that serve-requests freighters fill by themselves.",
        "Press [TargetAlert] to go to whatever needs you.",
        "A design can be mirrored while you draw it in the blueprint editor.",
        "[TimeFaster] and [TimeSlower] change the speed of time. The result is the same at any speed.",
        "You can rename ships, stations, planets and wrecks by clicking their names.",
        "Solar panels give a quarter of their power in a planet's shadow.",
    };

    private string Tip() => WithKeys(Tips[(int)(GD.Randi() % Tips.Length)]);
}
