using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SpaceAutomationSandbox.Construction;

/// <summary>
/// Editing model behind the blueprint editor UI: a mutable grid of parts (with rotation) with undo, live
/// stats, validation errors and non-blocking warnings. Pure C#, so the editing rules are tested
/// without Godot. It produces immutable <see cref="ShipDesign"/>s; it never touches a live craft.
/// </summary>
public sealed class BlueprintEditor
{
    /// <summary>Editable area is (2*HalfExtent+1) cells square around the origin (21×21). Placeholder limit.</summary>
    public const int HalfExtent = 10;

    private const int MaxUndo = 200;

    private readonly PartCatalog _catalog;
    private Dictionary<GridPos, DesignCell> _cells = new();
    private readonly List<List<(GridPos Pos, DesignCell? Before)>> _undo = new(); // what each step changed, not a copy of everything (#114)
    private ShipDesign? _design;
    private bool _inStroke;
    private bool _strokeChanged;
    private string _name = "New design";

    public BlueprintEditor(PartCatalog catalog) => _catalog = catalog;

    public string Name
    {
        get => _name;
        set
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed == _name) return;
            _name = trimmed;
            _design = null;
            IsDirty = true;
        }
    }

    /// <summary>File/stable id derived from the name (see <see cref="SlugFromName"/>).</summary>
    public string Id => SlugFromName(Name);

    public IReadOnlyDictionary<GridPos, DesignCell> Cells => _cells;

    /// <summary>True when there are edits that haven't been saved since the last <see cref="Load"/> or <see cref="MarkSaved"/>.</summary>
    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public static bool InBounds(GridPos pos) => Math.Abs(pos.X) <= HalfExtent && Math.Abs(pos.Y) <= HalfExtent;

    /// <summary>Replaces the contents with a design (unknown parts are kept and reported as errors).</summary>
    public void Load(ShipDesign design)
    {
        _cells = design.Cells.ToDictionary(c => c.Pos);
        _design = null;
        _name = design.Name.Length > 0 ? design.Name : design.Id;
        _undo.Clear();
        IsDirty = false;
    }

    public void New()
    {
        _cells = new Dictionary<GridPos, DesignCell>();
        _design = null;
        _name = "New design";
        _undo.Clear();
        IsDirty = false;
    }

    public void MarkSaved() => IsDirty = false;

    /// <summary>Groups changes (e.g. one mouse drag) into a single undo step.</summary>
    public void BeginStroke()
    {
        if (_inStroke) return;
        PushUndo();
        _inStroke = true;
        _strokeChanged = false;
    }

    public void EndStroke()
    {
        if (!_inStroke) return;
        _inStroke = false;
        if (!_strokeChanged) _undo.RemoveAt(_undo.Count - 1); // nothing happened: no empty undo step
    }

    /// <summary>
    /// Puts a part in a cell with a rotation (quarter turns clockwise), replacing what's there.
    /// Returns false if nothing changed or it isn't allowed.
    /// </summary>
    public bool Place(GridPos pos, string partId, int rotation = 0)
    {
        if (!InBounds(pos) || !_catalog.TryGet(partId, out _) || rotation is < 0 or > 3) return false;
        var cell = new DesignCell(pos, partId, rotation);
        if (_cells.TryGetValue(pos, out var existing) && existing == cell) return false;
        Change(pos, () => _cells[pos] = cell);
        return true;
    }

    public bool Remove(GridPos pos)
    {
        if (!_cells.ContainsKey(pos)) return false;
        Change(pos, () => _cells.Remove(pos));
        return true;
    }

    public bool Undo()
    {
        if (_inStroke || _undo.Count == 0) return false;
        var step = _undo[^1];
        for (var i = step.Count - 1; i >= 0; i--) // the last change first
        {
            if (step[i].Before is { } before) _cells[step[i].Pos] = before;
            else _cells.Remove(step[i].Pos);
        }
        _design = null;
        _undo.RemoveAt(_undo.Count - 1);
        IsDirty = true;
        return true;
    }

    /// <summary>The layout as a design: the same one until something changes, so what it works out is worked out once (#114).</summary>
    public ShipDesign ToDesign() => _design ??= new ShipDesign(Id, Name, _cells.Values);

    /// <summary>Stats of the known parts (unknown ones are listed in <see cref="Errors"/>); null when empty.</summary>
    public ShipStats? Stats
    {
        get
        {
            var known = _cells.Values.Where(c => _catalog.TryGet(c.PartId, out _)).ToList();
            if (known.Count == 0) return null;
            return (known.Count == _cells.Count ? ToDesign() : new ShipDesign(Id, Name, known)).ComputeStats(_catalog);
        }
    }

    /// <summary>Materials to build the current layout (known parts only).</summary>
    public MaterialBill BillOfMaterials => ToDesign().ComputeBillOfMaterials(_catalog);

    /// <summary>
    /// What this layout is treated as: a ship if it has a cockpit or thrusters (or nothing that makes a
    /// station), otherwise a station. Errors use that role's rules (the same rules as flying or ordering).
    /// </summary>
    public DesignRole Role => ToDesign().SuggestedRole(_catalog);

    /// <summary>Problems that make the design unusable in its <see cref="Role"/>.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            var errors = ToDesign().Validate(_catalog, Role).ToList();
            if (string.IsNullOrWhiteSpace(Name)) errors.Add("Design needs a name.");
            return errors;
        }
    }

    /// <summary>Advice that doesn't block saving. Loose parts are an error since #35 (see <see cref="Errors"/>).</summary>
    public IReadOnlyList<string> Warnings => ToDesign().Warnings(_catalog);

    public int CountConnectedGroups()
    {
        var unvisited = new HashSet<GridPos>(_cells.Keys);
        var groups = 0;
        var stack = new Stack<GridPos>();
        while (unvisited.Count > 0)
        {
            groups++;
            var start = unvisited.OrderBy(p => p.Y).ThenBy(p => p.X).First();
            unvisited.Remove(start);
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                foreach (var n in new[] { new GridPos(p.X + 1, p.Y), new GridPos(p.X - 1, p.Y), new GridPos(p.X, p.Y + 1), new GridPos(p.X, p.Y - 1) })
                    if (unvisited.Remove(n)) stack.Push(n);
            }
        }
        return groups;
    }

    /// <summary>
    /// Lower-case letters, digits and underscores, max 40 chars; "design" if nothing is left.
    /// Accented letters become plain ones (Ü → u, é → e, ß → ss) instead of being dropped.
    /// </summary>
    public static string SlugFromName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in Transliterate((name ?? "").Trim().ToLowerInvariant()))
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var slug = sb.ToString().Trim('_');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('_');
        return slug.Length == 0 ? "design" : slug;
    }

    private static string Transliterate(string text)
    {
        var sb = new StringBuilder();
        // Decompose (é → e + ´) and drop the accent marks; a few letters have no decomposition.
        foreach (var ch in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch { 'ß' => "ss", 'æ' => "ae", 'œ' => "oe", 'ø' => "o", 'đ' => "d", 'ł' => "l", 'þ' => "th", _ => ch.ToString() });
        }
        return sb.ToString();
    }

    private void Change(GridPos pos, Action apply)
    {
        if (!_inStroke) PushUndo();
        _undo[^1].Add((pos, _cells.TryGetValue(pos, out var before) ? before : null));
        _design = null;
        apply();
        _strokeChanged = true;
        IsDirty = true;
    }

    private void PushUndo()
    {
        _undo.Add(new List<(GridPos, DesignCell?)>());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }
}
