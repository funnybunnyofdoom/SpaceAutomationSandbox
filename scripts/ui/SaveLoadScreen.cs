using System;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Save;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Save and load screen (#60), from the main menu and the pause menu. Lists every save newest first
/// with its name, kind, date, play time and tick; broken saves show why. In save mode: name a new save,
/// or overwrite one. In load mode: load. Both: rename and delete. Overwriting, loading over a game
/// with unsaved changes, and deleting ask first (through the owner's confirmation dialog).
/// </summary>
public partial class SaveLoadScreen : Control
{
    public enum ScreenMode { Save, Load }

    private readonly Func<string> _folder;
    private readonly Action<string, (string, Action)[]> _confirm;
    private Label _title = null!, _status = null!;
    private LineEdit _name = null!;
    private HBoxContainer _newRow = null!;
    private VBoxContainer _rows = null!;
    private ScreenMode _mode;
    private Button _saveNew = null!;

    /// <summary>The save being renamed (the name box then renames instead of saving), or null.</summary>
    private (string Path, string Name)? _renaming;

    /// <summary>Save to this path with this name (new or overwrite).</summary>
    public event Action<string, string>? SaveChosen;
    /// <summary>Load this path.</summary>
    public event Action<string>? LoadChosen;
    public event Action? Closed;

    /// <summary>The name a new save gets when none is typed (the game's name, #62).</summary>
    public Func<string> DefaultName { get; set; } = () => "";

    public SaveLoadScreen(Func<string> folder, Action<string, (string, Action)[]> confirm)
    {
        _folder = folder;
        _confirm = confirm;
        Build();
    }

    public SaveLoadScreen() : this(() => "", (_, _) => { }) { }

    private void Build()
    {
        Name = "SaveLoadScreen";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(640, 480) };
        centre.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);

        var top = new HBoxContainer();
        box.AddChild(top);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 20);
        top.AddChild(_title);
        var close = new Button { Text = "Close" };
        close.Pressed += Close;
        top.AddChild(close);

        _newRow = new HBoxContainer();
        box.AddChild(_newRow);
        _name = new LineEdit { PlaceholderText = "Name for a new save", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _name.TextSubmitted += _ => Submit();
        _newRow.AddChild(_name);
        _saveNew = new Button { Text = "Save as new" };
        _saveNew.Pressed += Submit;
        _newRow.AddChild(_saveNew);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 0) };
        box.AddChild(_status);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(620, 360), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_rows);
    }

    public void Open(ScreenMode mode, string? message = null)
    {
        _mode = mode;
        _title.Text = mode == ScreenMode.Save ? "SAVE GAME" : "LOAD GAME";
        _newRow.Visible = mode == ScreenMode.Save;
        _renaming = null;
        _saveNew.Text = "Save as new";
        _name.Text = "";
        _status.Text = message ?? "";
        Refresh();
        Visible = true;
        if (mode == ScreenMode.Save) _name.GrabFocus();
        else (_rows.GetChildCount() > 0 ? _rows.GetChild(0).FindChild("Primary", true, false) as Button : null)?.GrabFocus();
    }

    public void Close()
    {
        Visible = false;
        Closed?.Invoke();
    }

    public void ShowMessage(string message) => _status.Text = message;

    /// <summary>Enter in the name box, or its button: rename (if renaming) or save as new.</summary>
    private void Submit()
    {
        if (_renaming is { } target)
        {
            _renaming = null;
            _saveNew.Text = "Save as new";
            var name = _name.Text.Trim();
            if (name.Length > 0)
            {
                try { SaveStore.Rename(target.Path, name); _status.Text = $"Renamed \"{target.Name}\" to \"{name}\"."; }
                catch (Exception e) { _status.Text = $"Couldn't rename: {e.Message}"; }
            }
            _name.Text = "";
            _newRow.Visible = _mode == ScreenMode.Save;
            Refresh();
            return;
        }
        if (_mode == ScreenMode.Save) SaveNew();
    }

    private void SaveNew()
    {
        var name = _name.Text.Trim();
        var fallback = DefaultName() is { Length: > 0 } game ? $"{game} {DateTime.Now:yyyy-MM-dd HH:mm}" : $"Save {DateTime.Now:yyyy-MM-dd HH:mm}";
        SaveChosen?.Invoke(SaveStore.NewManualPath(_folder(), DateTime.UtcNow), name.Length > 0 ? name : fallback);
    }

    /// <summary>Rebuilds the list from the folder.</summary>
    public void Refresh()
    {
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        var slots = SaveStore.List(_folder());
        if (slots.Count == 0) _rows.AddChild(new Label { Text = "(no saves yet)", Modulate = new Color(1, 1, 1, 0.6f) });
        foreach (var slot in slots)
        {
            var row = new VBoxContainer();
            var kind = slot.Kind switch { SaveKind.Quick => "quicksave", SaveKind.Auto => "autosave", _ => "save" };
            var when = slot.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var info = slot.Broken
                ? $"{slot.Name}  ({kind}, {when})\n  BROKEN: {slot.Error}"
                : $"{slot.Name}  ({kind}, {when})" + (slot.Header is { } h ? $"\n  played {Played(h.PlayTimeS)}, tick {h.Tick}, seed {h.Seed}, format v{slot.SchemaVersion}" : $"\n  format v{slot.SchemaVersion} (no summary: an older save)");
            row.AddChild(new Label { Text = info, Modulate = slot.Broken ? Palette.Bad : Colors.White, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 0) });
            var buttons = new HBoxContainer();
            row.AddChild(buttons);
            var path = slot.Path;
            var name = slot.Name;
            if (!slot.Broken)
            {
                if (_mode == ScreenMode.Load) Add(buttons, "Load", () => LoadChosen?.Invoke(path), primary: true);
                else Add(buttons, "Overwrite", () => _confirm($"Overwrite \"{name}\" with the current game?",
                    new (string, Action)[] { ("Overwrite", () => SaveChosen?.Invoke(path, name)) }), primary: true);
                Add(buttons, "Rename", () => Rename(path, name));
            }
            Add(buttons, "Delete", () => _confirm($"Delete \"{name}\"? This can't be undone.",
                new (string, Action)[] { ("Delete", () => { SaveStore.Delete(path); _status.Text = $"Deleted \"{name}\"."; Refresh(); }) }));
            _rows.AddChild(row);
            _rows.AddChild(new HSeparator());
        }
    }

    private void Rename(string path, string current)
    {
        // Rename in place: the name box takes the new name; Enter (or the button) applies it.
        _renaming = (path, current);
        _newRow.Visible = true;
        _saveNew.Text = "Rename";
        _name.Text = current;
        _name.GrabFocus();
        _name.SelectAll();
        _status.Text = $"Type the new name for \"{current}\" and press Enter.";
    }

    private static void Add(HBoxContainer row, string text, Action pressed, bool primary = false)
    {
        var button = new Button { Text = text, Name = primary ? "Primary" : text };
        button.Pressed += pressed;
        row.AddChild(button);
    }

    private static string Played(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || !@event.IsActionPressed("ui_cancel")) return;
        Close();
        GetViewport().SetInputAsHandled();
    }
}
