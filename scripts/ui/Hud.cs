using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>What the HUD shows this frame, by area. Empty strings hide an area.</summary>
public sealed record HudSections(string Status, string Ship, string Stations, string Target);

/// <summary>
/// The in-flight HUD (#51), in separate areas with a translucent background so they stay readable
/// over bodies and orbits, all on the left so the side panels (which open on the right) never cover
/// them:
/// <list type="bullet">
/// <item>top left: messages, time speed and debug tuning (only when there is something to say);</item>
/// <item>under it: the ship you fly, then shipyards and stations;</item>
/// <item>bottom left: target and nearest body;</item>
/// <item>bottom: a one-line hint; H (or F1) opens the full key help.</item>
/// </list>
/// The ship, stations and target areas collapse to their title when clicked; the choice is kept in
/// the settings file.
/// </summary>
public partial class Hud : Control
{
    private static readonly Color Background = new(0.02f, 0.03f, 0.07f, 0.62f);
    private const float TextWidth = 520;

    private readonly GameSettings _settings;
    private readonly Action _saveSettings;
    private Section _status = null!, _ship = null!, _stations = null!, _target = null!;
    private PanelContainer _help = null!;
    private Label _helpLabel = null!;
    private Label _hintLabel = null!;

    public Hud(GameSettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        Build();
    }

    public Hud() : this(new GameSettings(), () => { }) { }

    /// <summary>True while the key help is open.</summary>
    public bool HelpOpen => _help.Visible;

    public void ToggleHelp()
    {
        _helpLabel.Text = HelpText();
        _help.Visible = !_help.Visible;
    }

    public void CloseHelp() => _help.Visible = false;

    /// <summary>The bottom-left hint names the current Help and Close keys (call after rebinding, #63).</summary>
    public void RefreshHint() =>
        _hintLabel.Text = $"{InputActions.Key1(InputActions.Help)}  keys and help      {InputActions.Key1(InputActions.ClosePanels)}  close panels";

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        var left = new VBoxContainer { Position = new Vector2(12, 12), MouseFilter = MouseFilterEnum.Ignore };
        left.AddThemeConstantOverride("separation", 6);
        AddChild(left);
        _status = new Section(null, this);
        _ship = new Section("Ship", this);
        _stations = new Section("Stations", this);
        left.AddChild(_status.Panel);
        left.AddChild(_ship.Panel);
        left.AddChild(_stations.Panel);

        // Bottom left, growing upwards.
        var bottom = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, GrowVertical = GrowDirection.Begin };
        bottom.SetAnchorsPreset(LayoutPreset.BottomLeft);
        bottom.Position = new Vector2(12, -12);
        bottom.OffsetLeft = 12;
        bottom.OffsetBottom = -12;
        bottom.AddThemeConstantOverride("separation", 6);
        AddChild(bottom);
        _target = new Section("Target", this);
        bottom.AddChild(_target.Panel);
        _hintLabel = new Label { Modulate = new Color(1, 1, 1, 0.75f) };
        RefreshHint();
        var hint = Panel(_hintLabel);
        bottom.AddChild(hint);

        _helpLabel = new Label();
        _help = Panel(_helpLabel);
        ((StyleBoxFlat)_help.GetThemeStylebox("panel")).BgColor = new Color(0.02f, 0.03f, 0.07f, 0.9f); // solid enough to read over anything
        _help.SetAnchorsPreset(LayoutPreset.Center);
        _help.GrowHorizontal = GrowDirection.Both;
        _help.GrowVertical = GrowDirection.Both;
        _help.Visible = false;
        AddChild(_help);
    }

    /// <summary>Shows this frame's text. Cheap when nothing changed.</summary>
    public void Show(HudSections sections)
    {
        _status.Set(sections.Status);
        _ship.Set(sections.Ship);
        _stations.Set(sections.Stations);
        _target.Set(sections.Target);
    }

    private static PanelContainer Panel(Control content)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = Background,
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.AddChild(content);
        return panel;
    }

    private bool IsCollapsed(string title) => _settings.HudCollapsed.Contains(title);

    private void SetCollapsed(string title, bool collapsed)
    {
        if (collapsed) _settings.HudCollapsed.Add(title);
        else _settings.HudCollapsed.Remove(title);
        _saveSettings();
    }

    /// <summary>One HUD area: an optional clickable title (collapses it) and its text.</summary>
    private sealed class Section
    {
        public readonly PanelContainer Panel;
        private readonly string? _title;
        private readonly Hud _hud;
        private readonly Button? _header;
        private readonly Label _body;
        private string _text = "\u0001"; // never equal to a real text: the first Set always shows

        public Section(string? title, Hud hud)
        {
            _title = title;
            _hud = hud;
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", 0);
            if (title is not null)
            {
                _header = new Button { Flat = true, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, Modulate = new Color(0.75f, 0.85f, 1f) };
                _header.Pressed += () =>
                {
                    hud.SetCollapsed(title, !hud.IsCollapsed(title));
                    Refresh();
                };
                box.AddChild(_header);
            }
            _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(TextWidth, 0), MouseFilter = MouseFilterEnum.Ignore };
            box.AddChild(_body);
            Panel = Hud.Panel(box);
            Panel.MouseFilter = title is null ? MouseFilterEnum.Ignore : MouseFilterEnum.Pass;
        }

        public void Set(string text)
        {
            text = text.TrimEnd('\n');
            if (text == _text) return;
            _text = text;
            Refresh();
        }

        private void Refresh()
        {
            Panel.Visible = _text.Length > 0;
            var collapsed = _title is not null && _hud.IsCollapsed(_title);
            if (_header is not null)
            {
                // Collapsed: the title plus the first line, so the essentials stay in view.
                var first = _text.Split('\n')[0];
                _header.Text = collapsed ? $"[+] {_title}:  {first}" : $"[-] {_title}";
            }
            _body.Visible = !collapsed;
            _body.Text = _text;
        }
    }

    /// <summary>The key list on H, from the current bindings (#63), so a rebound key shows as it is.</summary>
    private static string HelpText()
    {
        static string K(string action) => InputActions.KeyText(action);
        return $"""
            KEYS  ({K(InputActions.Help)} to close)      change them in Settings → Controls

            Flying
               thrust: {K(InputActions.Thrust)}      reverse: {K(InputActions.Reverse)}      turn: {K(InputActions.TurnLeft)} / {K(InputActions.TurnRight)}      strafe: {K(InputActions.StrafeLeft)} / {K(InputActions.StrafeRight)}
               brake (matches the target's motion): {K(InputActions.Brake)}
               cruise drive: {K(InputActions.Cruise)}      anchor / release: {K(InputActions.Anchor)}      dock / undock: {K(InputActions.Dock)}      deploy a kit: {K(InputActions.Deploy)}
               warp jump (docked at a gate): {K(InputActions.Jump)}
               cycle target: {K(InputActions.TargetCycle)}      target nearest body: {K(InputActions.TargetNearest)}      zoom: {K(InputActions.ZoomIn)} / {K(InputActions.ZoomOut)}

            Panels  (they open on the right; {K(InputActions.ClosePanels)} closes them)
               blueprint editor: {K(InputActions.Editor)}      shipyard: {K(InputActions.Shipyard)}      fleet: {K(InputActions.Fleet)}      cargo: {K(InputActions.Cargo)}      job: {K(InputActions.Job)}      map: {K(InputActions.Map)}
               research: {K(InputActions.Research)}      settings: {K(InputActions.Settings)}      debug: {K(InputActions.Debug)}      diagnostics: {K(InputActions.Diagnostics)}      fullscreen: {K(InputActions.Fullscreen)}

            Time and game
               slower: {K(InputActions.TimeSlower)}      faster: {K(InputActions.TimeFaster)}      pause: {K(InputActions.TimePause)}
               quicksave: {K(InputActions.QuickSave)}      quickload: {K(InputActions.QuickLoad)}      pause menu (save, load, quit): {K(InputActions.ClosePanels)}      (closing the window autosaves)

            HUD: click Ship, Stations or Target to fold them away.
            """;
    }
}
