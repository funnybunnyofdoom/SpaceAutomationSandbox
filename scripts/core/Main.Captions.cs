using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>Captions for important sounds (#129): a few words in a corner, each for a few seconds, when they're on in Settings.</summary>
public partial class Main
{
    private const double CaptionS = 3;
    private const int MostCaptions = 4;

    private readonly List<(string Text, double Until)> _captionLines = new();
    private Label? _captionLabel;

    private void ShowCaption(string text)
    {
        if (_captionLabel is null)
        {
            _captionLabel = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, MouseFilter = Control.MouseFilterEnum.Ignore, ThemeTypeVariation = "HudText",
            };
            _captionLabel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            _captionLabel.GrowHorizontal = Control.GrowDirection.Begin;
            _captionLabel.GrowVertical = Control.GrowDirection.Begin;
            _captionLabel.OffsetRight = -16;
            _captionLabel.OffsetBottom = -16;
            _captionLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
            _captionLabel.AddThemeConstantOverride("outline_size", 4);
            _uiRoot.AddChild(_captionLabel);
        }
        var now = Time.GetTicksMsec() / 1000.0;
        _captionLines.RemoveAll(l => l.Text == text || l.Until < now);
        _captionLines.Add((text, now + CaptionS));
        if (_captionLines.Count > MostCaptions) _captionLines.RemoveAt(0);
        UpdateCaptions();
    }

    /// <summary>Once a frame: drops captions that have been up long enough.</summary>
    private void UpdateCaptions()
    {
        if (_captionLabel is null) return;
        var now = Time.GetTicksMsec() / 1000.0;
        _captionLines.RemoveAll(l => l.Until < now);
        var text = string.Join("\n", _captionLines.Select(l => $"[{l.Text}]"));
        if (_captionLabel.Text != text) _captionLabel.Text = text;
        _captionLabel.Visible = _captionLines.Count > 0 && !InMenu;
    }
}
