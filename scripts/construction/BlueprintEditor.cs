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
    /// <summary>The whole grid of large-ship building (#111): 101 x 101, about 10,000 cells (docs/09).</summary>
    public const int LargeHalfExtent = 50;

    /// <summary>Editable area is (2*HalfExtent+1) cells square around the origin (21×21). Placeholder limit.</summary>
    public const int HalfExtent = 10;

    private const int MaxUndo = 200;

    private readonly PartCatalog _catalog;
    private Dictionary<GridPos, DesignCell> _cells = new();
    private readonly List<List<(GridPos Pos, DesignCell? Before)>> _undo = new(); // what each step changed, not a copy of everything (#114)
    private ShipDesign? _design;
    private readonly List<List<(GridPos Pos, DesignCell? Before)>> _redo = new();
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

    /// <summary>How far the grid reaches from its middle now: <see cref="HalfExtent"/>, or <see cref="LargeHalfExtent"/> once large-ship building is open.</summary>
    public int Extent { get; set; } = HalfExtent;

    public bool InBounds(GridPos pos) => Math.Abs(pos.X) <= Extent && Math.Abs(pos.Y) <= Extent;

    /// <summary>Why this design can only be looked at, or null.</summary>
    public string? LockedReason => _tooBig ??= _cells.Keys.Any(p => !InBounds(p)) ? $"This design is bigger than your {2 * Extent + 1} x {2 * Extent + 1} grid: it can be looked at, not changed." : "";

    private string? _tooBig;

    /// <summary>It's bigger than the grid there is.</summary>
    public bool IsLocked
    {
        get
        {
            if (_tooBigFor != Extent) (_tooBig, _tooBigFor) = (null, Extent);
            return LockedReason!.Length > 0;
        }
    }

    private int _tooBigFor = HalfExtent;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Drawing is mirrored across the grid's middle: left to right, front to back (#112).</summary>
    public bool MirrorLeftRight { get; set; }
    public bool MirrorFrontBack { get; set; }

    /// <summary>What's selected (#112): only cells that hold something.</summary>
    public HashSet<GridPos> Selection { get; } = new();

    /// <summary>Replaces the contents with a design (unknown parts are kept and reported as errors).</summary>
    public void Load(ShipDesign design)
    {
        _cells = design.Cells.ToDictionary(c => c.Pos);
        _design = null;
        Forget();
        _name = design.Name.Length > 0 ? design.Name : design.Id;
        _undo.Clear();
        IsDirty = false;
    }

    public void New()
    {
        _cells = new Dictionary<GridPos, DesignCell>();
        _design = null;
        Forget();
        _name = "New design";
        _undo.Clear();
        IsDirty = false;
    }

    private void Forget()
    {
        _redo.Clear();
        Selection.Clear();
        _tooBig = null;
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
        if (IsLocked || !InBounds(pos) || !_catalog.TryGet(partId, out _) || rotation is < 0 or > 3) return false;
        var cell = new DesignCell(pos, partId, rotation);
        if (_cells.TryGetValue(pos, out var existing) && existing == cell) return false;
        Change(pos, () => _cells[pos] = cell);
        return true;
    }

    public bool Remove(GridPos pos)
    {
        if (IsLocked || !_cells.ContainsKey(pos)) return false;
        Change(pos, () => _cells.Remove(pos));
        return true;
    }

    public bool Undo()
    {
        if (_inStroke || _undo.Count == 0) return false;
        _redo.Add(Revert(_undo[^1]));
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    /// <summary>Puts back what was undone.</summary>
    public bool Redo()
    {
        if (_inStroke || _redo.Count == 0) return false;
        _undo.Add(Revert(_redo[^1]));
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    /// <summary>Takes a step back; what comes back is the step that would do it again.</summary>
    private List<(GridPos, DesignCell?)> Revert(List<(GridPos Pos, DesignCell? Before)> step)
    {
        var again = new List<(GridPos, DesignCell?)>(step.Count);
        for (var i = step.Count - 1; i >= 0; i--) // the last change first
        {
            again.Add((step[i].Pos, _cells.TryGetValue(step[i].Pos, out var now) ? now : null));
            if (step[i].Before is { } before) _cells[step[i].Pos] = before;
            else _cells.Remove(step[i].Pos);
        }
        _design = null;
        _tooBig = null;
        Selection.RemoveWhere(p => !_cells.ContainsKey(p));
        IsDirty = true;
        return again;
    }

    // --- Many parts at once (#112): each is one undo step (inside a stroke, part of it) ---

    private bool AsOneStep(Func<bool> change)
    {
        var mine = !_inStroke;
        if (mine) BeginStroke();
        var changed = change();
        if (mine) EndStroke();
        Selection.RemoveWhere(p => !_cells.ContainsKey(p));
        return changed;
    }

    /// <summary>Places a part, and its mirror images when mirroring is on.</summary>
    public bool Draw(IEnumerable<GridPos> where, string partId, int rotation) => AsOneStep(() =>
    {
        var changed = false;
        foreach (var pos in where)
            foreach (var cell in BlueprintTools.Mirrored(new DesignCell(pos, partId, rotation), MirrorLeftRight, MirrorFrontBack)) changed |= Place(cell.Pos, cell.PartId, cell.Rotation);
        return changed;
    });

    /// <summary>Removes parts, and the mirror images when mirroring is on.</summary>
    public bool Erase(IEnumerable<GridPos> where) => AsOneStep(() =>
    {
        var changed = false;
        foreach (var pos in where)
            foreach (var cell in BlueprintTools.Mirrored(new DesignCell(pos, ""), MirrorLeftRight, MirrorFrontBack)) changed |= Remove(cell.Pos);
        return changed;
    });

    /// <summary>Places cells as they are. False, and nothing placed, if any would fall off the grid.</summary>
    public bool PlaceAll(IReadOnlyCollection<DesignCell> cells)
    {
        if (IsLocked || cells.Any(c => !InBounds(c.Pos))) return false;
        return AsOneStep(() => cells.Aggregate(false, (changed, c) => Place(c.Pos, c.PartId, c.Rotation) | changed));
    }

    public IReadOnlyList<DesignCell> Selected => Selection.Where(_cells.ContainsKey).Select(p => _cells[p]).OrderBy(c => c.Pos.Y).ThenBy(c => c.Pos.X).ToList();

    /// <summary>Puts something else in the selection's place (moved, turned, mirrored); refused if it wouldn't fit the grid.</summary>
    public bool ReplaceSelection(IReadOnlyCollection<DesignCell> with)
    {
        if (IsLocked || with.Any(c => !InBounds(c.Pos))) return false;
        var old = Selection.ToList();
        var keep = with.Select(c => c.Pos).ToHashSet();
        var changed = AsOneStep(() =>
        {
            var any = false;
            foreach (var pos in old) if (!keep.Contains(pos)) any |= Remove(pos);
            foreach (var c in with) any |= Place(c.Pos, c.PartId, c.Rotation);
            return any;
        });
        Selection.Clear();
        Selection.UnionWith(keep);
        return changed;
    }

    /// <summary>Moves the selection; what it lands on is replaced.</summary>
    public bool MoveSelection(int dx, int dy) => ReplaceSelection(Selected.Select(c => c with { Pos = new GridPos(c.Pos.X + dx, c.Pos.Y + dy) }).ToList());

    /// <summary>Turns or mirrors the selection where it is, its top left corner staying put.</summary>
    public bool ReshapeSelection(Func<IEnumerable<DesignCell>, IReadOnlyList<DesignCell>> reshape)
    {
        var cells = Selected;
        if (cells.Count == 0) return false;
        var (x, y) = (cells.Min(c => c.Pos.X), cells.Min(c => c.Pos.Y));
        return ReplaceSelection(reshape(cells).Select(c => c with { Pos = new GridPos(c.Pos.X + x, c.Pos.Y + y) }).ToList());
    }

    /// <summary>Every part of one kind becomes another, keeping its facing: in the selection, or everywhere if nothing is selected.</summary>
    /// <summary>Swaps every part (or every selected one) for its best tier <paramref name="unlocked"/> allows, as one undo step (#141); how many changed.</summary>
    public int UpgradeTiers(Func<string, bool> unlocked)
    {
        if (IsLocked) return 0;
        var cells = (Selection.Count > 0 ? Selected : _cells.Values.ToList())
            .Select(c => (Cell: c, Better: PartTiers.BestUnlocked(_catalog, c.PartId, unlocked))).Where(x => x.Better is not null).ToList();
        AsOneStep(() => cells.Aggregate(false, (changed, x) => Place(x.Cell.Pos, x.Better!, x.Cell.Rotation) | changed));
        return cells.Count;
    }

    public int ReplaceParts(string from, string to)
    {
        if (IsLocked || from == to) return 0;
        var cells = (Selection.Count > 0 ? Selected : _cells.Values.ToList()).Where(c => c.PartId == from).ToList();
        AsOneStep(() => cells.Aggregate(false, (changed, c) => Place(c.Pos, to, c.Rotation) | changed));
        return cells.Count;
    }

    /// <summary>What a change would add to the mass, and where it would move the centre of mass (cells).</summary>
    public (double MassKg, double ComX, double ComY, double DeltaKg) Preview(IReadOnlyCollection<DesignCell> placed, IReadOnlyCollection<GridPos> removed)
    {
        double Mass(string id) => _catalog.TryGet(id, out var p) ? p!.MassKg : 0;
        double mass = 0, x = 0, y = 0;
        void Add(DesignCell c, double sign)
        {
            var kg = Mass(c.PartId) * sign;
            mass += kg;
            x += kg * c.Pos.X;
            y += kg * c.Pos.Y;
        }
        foreach (var c in _cells.Values) Add(c, 1);
        var before = mass;
        var gone = new HashSet<GridPos>(removed);
        foreach (var c in placed) gone.Add(c.Pos);
        foreach (var p in gone) if (_cells.TryGetValue(p, out var c)) Add(c, -1);
        foreach (var c in placed) Add(c, 1);
        return (mass, mass > 0 ? x / mass : 0, mass > 0 ? y / mass : 0, mass - before);
    }

    /// <summary>What parts about to be placed cost, the biggest materials first: "; 4 t Metal, 1 t Electronics".</summary>
    public string CostText(IReadOnlyCollection<DesignCell> placed)
    {
        var bill = new ShipDesign("preview", "Preview", placed.DistinctBy(c => c.Pos)).ComputeBillOfMaterials(_catalog);
        return string.Concat(bill.Kg.OrderByDescending(kv => kv.Value).Take(3).Select((kv, i) =>
            $"{(i == 0 ? "; " : ", ")}{(kv.Value / 1000.0).ToString("#,0.#", System.Globalization.CultureInfo.InvariantCulture)} t {(_catalog.Materials.Contains(kv.Key) ? _catalog.Materials.Get(kv.Key).Name : kv.Key)}"));
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
        _tooBig = null;
        _redo.Clear();
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
