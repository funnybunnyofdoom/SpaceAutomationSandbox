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
    /// <summary>Editable area is (2*HalfExtent+1) cells square around the origin. Placeholder limit.</summary>
    public const int HalfExtent = 7;

    private const int MaxUndo = 200;

    private readonly PartCatalog _catalog;
    private Dictionary<GridPos, DesignCell> _cells = new();
    private readonly List<Dictionary<GridPos, DesignCell>> _undo = new();
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
        _name = design.Name.Length > 0 ? design.Name : design.Id;
        _undo.Clear();
        IsDirty = false;
    }

    public void New()
    {
        _cells = new Dictionary<GridPos, DesignCell>();
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
        Change(() => _cells[pos] = cell);
        return true;
    }

    public bool Remove(GridPos pos)
    {
        if (!_cells.ContainsKey(pos)) return false;
        Change(() => _cells.Remove(pos));
        return true;
    }

    public bool Undo()
    {
        if (_inStroke || _undo.Count == 0) return false;
        _cells = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        IsDirty = true;
        return true;
    }

    public ShipDesign ToDesign() =>
        new(Id, Name, _cells.Values);

    /// <summary>Stats of the known parts (unknown ones are listed in <see cref="Errors"/>); null when empty.</summary>
    public ShipStats? Stats
    {
        get
        {
            var known = _cells.Values.Where(c => _catalog.TryGet(c.PartId, out _)).ToList();
            return known.Count == 0 ? null : new ShipDesign(Id, Name, known).ComputeStats(_catalog);
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

    /// <summary>
    /// Advice that doesn't block saving. Connectivity is a warning only, because whether parts must
    /// connect is still open (docs/06, question 4).
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = ToDesign().Warnings(_catalog).ToList();
            var groups = CountConnectedGroups();
            if (groups > 1) warnings.Add($"Parts form {groups} separate groups (not edge-connected).");
            return warnings;
        }
    }

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

    /// <summary>Lower-case letters, digits and underscores, max 40 chars; "design" if nothing is left.</summary>
    public static string SlugFromName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var slug = sb.ToString().Trim('_');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('_');
        return slug.Length == 0 ? "design" : slug;
    }

    private void Change(Action apply)
    {
        if (!_inStroke) PushUndo();
        apply();
        _strokeChanged = true;
        IsDirty = true;
    }

    private void PushUndo()
    {
        _undo.Add(new Dictionary<GridPos, DesignCell>(_cells));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }
}
