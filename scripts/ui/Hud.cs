using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Core;

namespace SpaceAutomationSandbox.UI;

/// <summary>What the HUD shows this frame, by area.</summary>
public sealed record HudSections(HudCardData Status, HudCardData Ship, HudCardData Stations, HudCardData Target, HudCardData Goals);

/// <summary>
/// The in-flight HUD (#51, graphical since #101): cards on the left, so the side panels (which open on the right) never cover them:
/// <list type="bullet">
/// <item>top left: messages, time speed and debug tuning (only when there is something to say);</item>
/// <item>under it: the ship you fly with its amounts as bars, then shipyards and stations;</item>
/// <item>bottom left: target and nearest body, and a one-line hint; H (or F1) opens the full key help.</item>
/// </list>
/// A card folds to its heading when clicked; the choice is kept in the settings file.
/// </summary>
public partial class Hud : Control
{
    private const float CardWidth = 320, Edge = 12;

    private readonly GameSettings _settings;
    private readonly Action _saveSettings;
    private HudCard _status = null!, _ship = null!, _stations = null!, _target = null!, _goals = null!;
    private PanelContainer _help = null!;
    private VBoxContainer _left = null!, _bottom = null!;

    /// <summary>How far the HUD reaches from the left edge: side panels keep clear of it (#96).</summary>
    public float LeftWidth => Visible ? _left.Position.X + Mathf.Max(_left.Size.X, _bottom.Position.X + _bottom.Size.X - _left.Position.X) : 0;
    private Label _helpLabel = null!;
    private Label _hintLabel = null!;

    public Hud(GameSettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        Build();
    }

    public Hud() : this(new GameSettings(), () => { }) { }

    /// <summary>Something is drawn over the HUD: its cards hide until it closes.</summary>
    public bool Covered { set { _left.Visible = !value; _bottom.Visible = !value; } }

    /// <summary>The cards are showing.</summary>
    public bool Shown => Visible && _left.Visible;

    /// <summary>The menu bar's left and bottom edges (#106): the cards start below it when they'd reach it.</summary>
    public void KeepClearOf(float barLeft, float barBottom)
    {
        var width = CardWidth * _settings.TextScale;
        _left.CustomMinimumSize = _bottom.CustomMinimumSize = new Vector2(width, 0);
        _left.Position = new Vector2(Edge, barBottom > 0 && Edge + width + 8 > barLeft ? barBottom + 6 : Edge);
        _left.Size = new Vector2(width, 0);
    }

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

        _left = new VBoxContainer { Position = new Vector2(12, 12), MouseFilter = MouseFilterEnum.Ignore };
        _left.AddThemeConstantOverride("separation", 6);
        AddChild(_left);
        HudCard Card(string title) => new(title, () => IsCollapsed(title), () => SetCollapsed(title, !IsCollapsed(title)));
        _left.AddChild(_status = Card("Messages"));
        _left.AddChild(_ship = Card("Ship"));
        _left.AddChild(_stations = Card("Stations"));
        _left.AddChild(_goals = Card("Goals")); // new-player goal list (#65)

        // Bottom left, growing upwards.
        _bottom = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, GrowVertical = GrowDirection.Begin };
        _bottom.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _bottom.Position = new Vector2(12, -12);
        _bottom.OffsetLeft = 12;
        _bottom.OffsetBottom = -12;
        _bottom.AddThemeConstantOverride("separation", 6);
        AddChild(_bottom);
        _bottom.AddChild(_target = Card("Target"));
        _hintLabel = new Label { ThemeTypeVariation = "HudText", Modulate = new Color(1, 1, 1, 0.75f) };
        RefreshHint();
        var hint = Panel(_hintLabel);
        _bottom.AddChild(hint);

        _helpLabel = new Label();
        _help = Panel(_helpLabel);
        _help.SetAnchorsPreset(LayoutPreset.Center);
        _help.GrowHorizontal = GrowDirection.Both;
        _help.GrowVertical = GrowDirection.Both;
        _help.Visible = false;
        _help.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_help);
    }

    /// <summary>Shows this frame's state. Cheap when nothing changed.</summary>
    public void Show(HudSections sections)
    {
        _status.Set(sections.Status);
        _ship.Set(sections.Ship);
        _stations.Set(sections.Stations);
        _target.Set(sections.Target);
        _goals.Set(sections.Goals);
        // On a short screen the two columns would meet (#96): goals give way first, then the stations, then the target.
        if (Size.Y <= 0 || Time.GetTicksMsec() < _nextSqueezeMs) return;
        _nextSqueezeMs = Time.GetTicksMsec() + 250;
        Squeeze();
    }

    private ulong _nextSqueezeMs;

    private void Squeeze()
    {
        var width = CardWidth * _settings.TextScale;
        var order = new[] { _goals, _stations, _target };
        var room = Size.Y - _left.Position.Y - Edge - _bottom.GetChild<Control>(1).GetCombinedMinimumSize().Y;
        var squeezed = 0;
        while (squeezed < order.Length && Needed() > room) squeezed++;
        for (var i = 0; i < order.Length; i++) SetSqueezed(order[i], i < squeezed);

        float Needed()
        {
            var total = 0f;
            foreach (var card in new[] { _status, _ship, _stations, _goals, _target })
                if (card.Visible) total += 6 + card.HeightAt(width, System.Array.IndexOf(order, card) is >= 0 and var at && at < squeezed);
            return total;
        }
    }

    private static void SetSqueezed(HudCard card, bool squeezed)
    {
        if (card.Squeezed == squeezed) return;
        card.Squeezed = squeezed;
        card.Set(card.Data);
    }

    private static PanelContainer Panel(Control content)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
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
               next target: {K(InputActions.TargetCycle)}      previous: {K(InputActions.TargetPrevious)}      other kind: {K(InputActions.TargetKindNext)}      ahead: {K(InputActions.TargetAhead)}      clear: {K(InputActions.TargetClear)}
               click: target      right click: target, fly to      target nearest body: {K(InputActions.TargetNearest)}      zoom: {K(InputActions.ZoomIn)} / {K(InputActions.ZoomOut)}

            Panels  (they open on the right; {K(InputActions.ClosePanels)} closes them)
               blueprint editor: {K(InputActions.Editor)}      shipyard: {K(InputActions.Shipyard)}      fleet: {K(InputActions.Fleet)}      cargo: {K(InputActions.Cargo)}      job: {K(InputActions.Job)}      map: {K(InputActions.Map)}
               research: {K(InputActions.Research)}      laws and civilizations: {K(InputActions.Laws)}      power: {K(InputActions.Power)}      native life: {K(InputActions.Life)}      milestones: {K(InputActions.Milestones)}      log: {K(InputActions.Log)}
                    settings: {K(InputActions.Settings)}      debug: {K(InputActions.Debug)}      diagnostics: {K(InputActions.Diagnostics)}      fullscreen: {K(InputActions.Fullscreen)}

            Time and game
               slower: {K(InputActions.TimeSlower)}      faster: {K(InputActions.TimeFaster)}      pause: {K(InputActions.TimePause)}
               quicksave: {K(InputActions.QuickSave)}      quickload: {K(InputActions.QuickLoad)}      pause menu (save, load, quit): {K(InputActions.ClosePanels)}      (closing the window autosaves)

            Using panels with keys or a controller: {K(InputActions.PanelFocus)} (controller: {InputActions.PadText(InputActions.PanelFocus)}) moves into the open panel;
               arrows / d-pad move, Enter / A choose, {K(InputActions.PanelFocus)} or B back to flying.

            Menu bar: {K(InputActions.MenuBar)} hides or shows it.      HUD: click a heading to fold its card away; point at a bar for details.
            """;
    }
}
