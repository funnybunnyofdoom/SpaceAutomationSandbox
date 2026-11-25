using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Side panels never overlap (#96): the open ones stand side by side along the right, each with its own width, and when
/// there's no room for another the one opened longest ago closes. They're translucent, so the game shows through, and nothing
/// else is left beneath them: settings, help, menus and the editor close the panels under them, a menu opened from another
/// hides that one until it's closed, and the HUD hides under anything that would cover it. A question only hides what's under it.
/// </summary>
public partial class Main
{
    private const float PanelGap = 8, PanelEdge = 12;

    /// <summary>Panels in the order they were opened.</summary>
    private readonly List<Control> _openPanels = new();
    private readonly Dictionary<Control, float> _panelWidths = new();

    private void ArrangePanels()
    {
        // The menu bar (#106): while flying, and with settings open over the game.
        var modal = _title is { Visible: true } || ModalOpen || _newGame.Visible || InMenu;
        _topBar.Visible = _settings.MenuBar && !modal && !EditorOpen;
        _topBar.Large = _uiRoot.Size.X >= 1700;
        _hud.KeepClearOf(_topBar.Position.X, TopBarBottom);
        var panelTop = Mathf.Max(PanelEdge, TopBarBottom + 6);

        // The blueprint editor and the menus take the whole screen; settings may open above a menu.
        var inMenu = InMenu || _pause.Visible || _saveScreen.Visible || _controls is { Visible: true };
        if (EditorOpen || inMenu) CloseAll(SidePanels.Where(p => p is { Visible: true } && (EditorOpen || p != _settingsPanel)).Select(p => p!));

        foreach (var panel in SidePanels)
        {
            if (panel is null) continue;
            if (!_panelWidths.ContainsKey(panel))
            {
                _panelWidths[panel] = panel == _settingsPanel ? 460 : Mathf.Max(340, panel.OffsetRight - panel.OffsetLeft);
                AddCloseButton(panel); // #100
            }
            if (!panel.Visible) _openPanels.Remove(panel);
            else if (!_openPanels.Contains(panel))
            {
                _openPanels.Add(panel);
                if (_helpWasOpen) _hud.CloseHelp(); // the newer one stays
            }
        }
        if (_hud.HelpOpen && !_helpWasOpen) CloseAll(_openPanels);
        _helpWasOpen = _hud.HelpOpen;
        if (_settingsPanel is { Visible: true } && _openPanels.Count > 1)
        {
            // Whichever was opened last stays: settings and the side panels don't mix.
            if (_openPanels[^1] == _settingsPanel) CloseAll(_openPanels.Where(p => p != _settingsPanel));
            else CloseAll(new[] { _settingsPanel });
        }

        // Room for the HUD on the left, if anything fits beside it at all.
        var docked = _openPanels.Where(p => p != _settingsPanel).ToList();
        var room = _uiRoot.Size.X - _hud.LeftWidth - 2 * PanelEdge;
        while (docked.Count > 1 && docked.Sum(Width) + PanelGap * (docked.Count - 1) > room)
        {
            CloseAll(new[] { docked[0] });
            docked.RemoveAt(0);
        }
        var right = -PanelEdge;
        foreach (var panel in docked)
        {
            var width = Mathf.Min(Width(panel), _uiRoot.Size.X - 2 * PanelEdge);
            panel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
            panel.GrowHorizontal = Control.GrowDirection.Begin;
            panel.OffsetRight = right;
            panel.OffsetLeft = right - width;
            panel.OffsetTop = panelTop;
            panel.OffsetBottom = -PanelEdge;
            right -= width + PanelGap;
        }

        _panelsWidth = -right; // what the edge arrows keep clear of (#101)

        // Only the topmost menu shows. Those under it keep their state and focus, and come back when it closes.
        var screens = new Control?[] { _title, _pause, _newGame, _raceCreator, _saveScreen, _settingsPanel, _controls, _confirm, _loadingScreen };
        var top = System.Array.FindLastIndex(screens, s => s is { Visible: true });
        for (var i = 0; i < screens.Length; i++)
            if (screens[i] is { } screen && screen != _loadingScreen) SetBeneath(screen == _settingsPanel ? screen : Content(screen), i < top);
        foreach (var panel in SidePanels) if (panel != null && panel != _settingsPanel) SetBeneath(panel, top >= 0);
        _hud.Covered = top >= 0 || _hud.HelpOpen || docked.Count > 0 && docked.Sum(Width) > room;

        // A panel may need more than its usual width (long text): remember it, so the next one keeps clear.
        float Width(Control panel) => Mathf.Max(_panelWidths[panel], panel.GetCombinedMinimumSize().X);

        void CloseAll(IEnumerable<Control> panels)
        {
            foreach (var panel in panels.ToList())
            {
                if (panel == _mapPanel && _mapMode) ToggleMap(); // the map goes with its panel
                panel.Visible = false;
                _openPanels.Remove(panel);
            }
        }
    }

    private bool _helpWasOpen;
    private float _panelsWidth;

    /// <summary>A menu's own box, without the shade behind it (which keeps the mouse off what's below).</summary>
    private static Control Content(Control screen) => screen.GetChildren().OfType<CenterContainer>().FirstOrDefault() ?? screen;

    /// <summary>Hides what's under a menu without losing its state or focus.</summary>
    private static void SetBeneath(Control control, bool beneath)
    {
        control.Modulate = new Color(1, 1, 1, beneath ? 0 : 1);
        control.MouseBehaviorRecursive = beneath ? Control.MouseBehaviorRecursiveEnum.Disabled : Control.MouseBehaviorRecursiveEnum.Inherited;
    }
}
