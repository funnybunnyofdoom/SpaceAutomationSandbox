using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Side panels never overlap (#96): the open ones stand side by side along the right, each with its own width, and when
/// there's no room for another the one opened longest ago closes. They're solid, so nothing shows through. Settings opens in
/// the middle and closes the others.
/// </summary>
public partial class Main
{
    private const float PanelGap = 8, PanelEdge = 12;

    /// <summary>Panels in the order they were opened.</summary>
    private readonly List<Control> _openPanels = new();
    private readonly Dictionary<Control, float> _panelWidths = new();

    private void ArrangePanels()
    {
        // The blueprint editor takes the whole screen.
        if (EditorOpen) foreach (var panel in SidePanels) if (panel is { Visible: true } && panel != _debugPanel && panel != _settingsPanel) panel.Visible = false;

        foreach (var panel in SidePanels)
        {
            if (panel is null) continue;
            if (!_panelWidths.ContainsKey(panel))
            {
                _panelWidths[panel] = panel == _settingsPanel ? 460 : Mathf.Max(340, panel.OffsetRight - panel.OffsetLeft);
                MakeSolid(panel);
            }
            if (!panel.Visible) _openPanels.Remove(panel);
            else if (!_openPanels.Contains(panel)) _openPanels.Add(panel);
        }
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
            panel.OffsetTop = PanelEdge;
            panel.OffsetBottom = -PanelEdge;
            right -= width + PanelGap;
        }

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

    private static void MakeSolid(Control panel)
    {
        if (panel is not PanelContainer container || container.GetThemeStylebox("panel") is not StyleBoxFlat themed) return;
        var solid = (StyleBoxFlat)themed.Duplicate();
        solid.BgColor = new Color(0.05f, 0.06f, 0.09f, 1f);
        solid.ContentMarginLeft = solid.ContentMarginRight = Mathf.Max(8, solid.ContentMarginLeft);
        solid.ContentMarginTop = solid.ContentMarginBottom = Mathf.Max(6, solid.ContentMarginTop);
        container.AddThemeStyleboxOverride("panel", solid);
    }
}
