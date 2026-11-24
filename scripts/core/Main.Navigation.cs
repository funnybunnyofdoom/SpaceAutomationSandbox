using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Using panels with the keyboard or a controller (#68), and the interface theme (text size, focus highlight).
/// <para>The side panels don't take focus while you fly (Space and the d-pad stay flying keys). The
/// "use the open panel" action (` or the controller's Back) moves focus into the topmost open panel:
/// d-pad/stick or arrows move, A or Enter choose, and B, ` or Back return to flying (Esc still closes
/// panels). Settings opened from the title or pause menu is used this way at once, and B closes it.
/// While a panel has focus the ship isn't flown from the controls.</para>
/// </summary>
public partial class Main
{
    /// <summary>The panel being used with keys/controller, if any.</summary>
    private Control? _navPanel;

    /// <summary>True when <see cref="_navPanel"/> was opened from a menu: B closes it instead of going back to flying.</summary>
    private bool _navFromMenu;

    private Control? _navLastFocus;
    private readonly List<Control> _navMadeFocusable = new();

    private IEnumerable<Control?> SidePanels => new Control?[]
        { _shipyardPanel, _fleetPanel, _swarmPanel, _cargoPanel, _jobPanel, _debugPanel, _diagnosticsPanel, _settingsPanel, _researchPanel, _lawsPanel, _powerPanel, _mapPanel };

    private bool PanelNavActive => _navPanel != null;

    private void EnterPanelNav(Control panel, bool fromMenu)
    {
        ExitPanelNav();
        _navPanel = panel;
        _navFromMenu = fromMenu;
        MakeFocusable(panel);
        _navLastFocus = FirstFocusable(panel);
        _navLastFocus?.GrabFocus();
    }

    private void ExitPanelNav()
    {
        if (_navPanel == null) return;
        foreach (var control in _navMadeFocusable)
            if (IsInstanceValid(control)) control.FocusMode = Control.FocusModeEnum.None;
        _navMadeFocusable.Clear();
        if (GetViewport().GuiGetFocusOwner() is { } owner && _navPanel.IsAncestorOf(owner)) owner.ReleaseFocus();
        _navPanel = null;
        _navLastFocus = null;
    }

    /// <summary>Buttons, option lists, checkboxes and sliders under the panel take focus while it's used.</summary>
    private void MakeFocusable(Node node)
    {
        if (node is Control { FocusMode: Control.FocusModeEnum.None } control and (BaseButton or Slider or ItemList or LineEdit))
        {
            control.FocusMode = Control.FocusModeEnum.All;
            _navMadeFocusable.Add(control);
        }
        foreach (var child in node.GetChildren()) MakeFocusable(child);
    }

    private static Control? FirstFocusable(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Control { Visible: false }) continue;
            if (child is Control c && c.FocusMode != Control.FocusModeEnum.None && c is not ScrollBar and not LineEdit && !(c is BaseButton { Disabled: true })) return c;
            if (FirstFocusable(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Each frame: keeps focus inside the panel in use (rows get rebuilt), and out of panels otherwise.</summary>
    private void UpdatePanelNav()
    {
        var owner = GetViewport().GuiGetFocusOwner();
        if (_navPanel != null)
        {
            if (!_navPanel.IsVisibleInTree())
            {
                ExitPanelNav();
                if (_pause.Visible && !_confirm.Visible && !_saveScreen.Visible) _pause.FocusFirst();
                return;
            }
            if (owner != null && _navPanel.IsAncestorOf(owner)) _navLastFocus = owner;
            else if (_navLastFocus is { } last && IsInstanceValid(last) && last.IsVisibleInTree() && _navPanel.IsAncestorOf(last)) last.GrabFocus();
            else (_navLastFocus = FirstFocusable(_navPanel))?.GrabFocus();
            return;
        }
        // Flying: a click in a side panel mustn't leave it holding the keys (Space would press it again).
        // Text boxes keep focus so they can be typed in.
        if (owner is not null and not LineEdit && SidePanels.Any(p => p != null && p.IsAncestorOf(owner))) owner.ReleaseFocus();
    }

    /// <summary>Handles the panel-use keys; true when the event was used.</summary>
    private bool PanelNavInput(InputEvent @event)
    {
        bool Pressed(string action) => @event.IsActionPressed(action, allowEcho: false);
        if (_navPanel == null)
        {
            if (InMenu || ModalOpen || EditorOpen || !Pressed(InputActions.PanelFocus)) return false;
            var open = SidePanels.Where(p => p is { Visible: true }).OrderByDescending(p => p!.GetIndex()).FirstOrDefault();
            if (open == null) ShowStatus("Open a panel first, then press it again to use the panel with keys or a controller.");
            else EnterPanelNav(open, fromMenu: false);
            return true;
        }
        if (Pressed(InputActions.ClosePanels)) return false; // Esc closes panels as usual
        if (Pressed(InputActions.PanelFocus) || @event.IsActionPressed("ui_cancel"))
        {
            if (_navFromMenu && _navPanel == _settingsPanel) _settingsPanel!.Close();
            else ExitPanelNav();
            return true;
        }
        // Game keys wait while a panel is in use; the interface has already taken its own keys.
        return @event is InputEventKey or InputEventJoypadButton or InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown };
    }

    /// <summary>The interface theme (#68): text size and a clear focus highlight. Panels set their own titles' sizes.</summary>
    private void ApplyTheme()
    {
        Palette.ColorblindSafe = _settings.ColorblindSafe;
        var theme = new Theme { DefaultFontSize = Mathf.RoundToInt(16 * _settings.TextScale) };
        var focus = new StyleBoxFlat
        {
            DrawCenter = false, BorderColor = Palette.Focus,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ExpandMarginLeft = 2, ExpandMarginRight = 2, ExpandMarginTop = 2, ExpandMarginBottom = 2,
        };
        foreach (var type in new[] { "Button", "OptionButton", "CheckBox", "CheckButton", "LineEdit", "HSlider", "ItemList", "MenuButton" })
            theme.SetStylebox("focus", type, focus);
        _uiRoot.Theme = theme;
    }
}
