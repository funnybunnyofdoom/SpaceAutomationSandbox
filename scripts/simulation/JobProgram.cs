using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>What a step of a program does (#120).</summary>
public enum StepKind { FlyTo, Dock, Load, Unload, Mine, Wait, Recharge, Refuel, Repair, Message, If }

/// <summary>What a step does when it can't be done for long enough.</summary>
public enum StepFallback { KeepTrying, NextStep, FirstStep, StopJob }

/// <summary>What a test looks at: the ship itself, or a station.</summary>
public enum TestKind { CargoPercent, CargoTonnes, PowerPercent, FuelTonnes, HealthPercent, StepMinutes, StationStockTonnes, StationFreePorts }

/// <summary>One test: something below, or at least, a value.</summary>
public sealed record ProgramTest
{
    public TestKind Kind { get; init; }

    /// <summary>For the tests of a station: which.</summary>
    public uint StationId { get; init; }
    public string Material { get; init; } = "";
    public bool Below { get; init; }
    public double Value { get; init; }
}

/// <summary>Tests of which all, or any, must hold.</summary>
public sealed record ProgramCondition
{
    public bool Any { get; init; }
    public List<ProgramTest> Tests { get; init; } = new();
}

/// <summary>A step. Its id stays with it when the program is edited, so ships keep their place.</summary>
public sealed record ProgramStep
{
    public int Id { get; init; }
    public StepKind Kind { get; init; }

    /// <summary>The station it's about; 0: for repairs the nearest shipyard, else wherever the ship is docked.</summary>
    public uint StationId { get; init; }
    public uint BodyId { get; init; }

    /// <summary>What to load, unload (empty: everything but fuel) or mine.</summary>
    public string Material { get; init; } = "";

    /// <summary>Load and refuel: until this much is aboard (0: until the hold is full). Unload: until this much is left.</summary>
    public double Tonnes { get; init; }

    /// <summary>Mine: until the hold is this full. Recharge: until the battery is.</summary>
    public double Percent { get; init; } = 100;

    /// <summary>Wait: this long at most (0: no limit). Recharge: no longer than this (0: no limit).</summary>
    public double Seconds { get; init; }

    /// <summary>Load and unload: stay until it's all there (or gone), instead of taking what there is.</summary>
    public bool UntilDone { get; init; }

    /// <summary>The message's words.</summary>
    public string Text { get; init; } = "";

    /// <summary>The step is done only if this holds; otherwise it's passed over.</summary>
    public ProgramCondition? OnlyIf { get; init; }

    /// <summary>Wait: until this holds. If: what's tested.</summary>
    public ProgramCondition? Until { get; init; }

    /// <summary>If: the steps for yes and for no.</summary>
    public List<ProgramStep> Then { get; init; } = new();
    public List<ProgramStep> Otherwise { get; init; } = new();

    /// <summary>When it can't be done for this long (0: for ever), what then.</summary>
    public double GiveUpSeconds { get; init; }
    public StepFallback Fallback { get; init; }
}

/// <summary>
/// A job as steps (#120): they run from the top and start again. Any number of ships share one, each at its own place; changing the
/// program changes it for all. Immutable data, no code: every step and test is of a fixed kind.
/// </summary>
public sealed class JobProgram
{
    public const int MaxSteps = 64, MaxDepth = 4, MaxTests = 6, MaxName = 40, MaxText = 120;

    private readonly Dictionary<int, int> _places = new();

    public JobProgram(int id, string name, IReadOnlyList<ProgramStep> steps)
    {
        Id = id;
        Name = name;
        Steps = steps;
        var ops = new List<ProgramOp>();
        Compile(steps, ops);
        Ops = ops;
        for (var i = 0; i < ops.Count; i++) if (ops[i].Step is { } step) _places.TryAdd(step.Id, i);
    }

    public int Id { get; }
    public string Name { get; }
    public IReadOnlyList<ProgramStep> Steps { get; }

    /// <summary>The steps in a row, with jumps for ifs.</summary>
    internal IReadOnlyList<ProgramOp> Ops { get; }

    /// <summary>How many steps there are, inner ones too.</summary>
    public int Count => _places.Count;

    /// <summary>A step's number, counting from 1 in the order shown.</summary>
    public int NumberOf(int stepId) => AllSteps().TakeWhile(s => s.Id != stepId).Count() + 1;

    internal int PlaceOf(int stepId) => _places.GetValueOrDefault(stepId, -1);

    public IEnumerable<ProgramStep> AllSteps() => All(Steps);

    public static IEnumerable<ProgramStep> All(IEnumerable<ProgramStep> steps) => steps.SelectMany(s => new[] { s }.Concat(All(s.Then)).Concat(All(s.Otherwise)));

    private static void Compile(IReadOnlyList<ProgramStep> steps, List<ProgramOp> ops)
    {
        foreach (var step in steps)
        {
            var at = ops.Count;
            ops.Add(new ProgramOp(step, -1));
            if (step.Kind != StepKind.If) continue;
            Compile(step.Then, ops);
            if (step.Otherwise.Count == 0) { ops[at] = new ProgramOp(step, ops.Count); continue; }
            var jump = ops.Count;
            ops.Add(new ProgramOp(null, -1)); // over the otherwise
            ops[at] = new ProgramOp(step, ops.Count);
            Compile(step.Otherwise, ops);
            ops[jump] = new ProgramOp(null, ops.Count);
        }
    }

    /// <summary>Why this isn't a program, or null.</summary>
    public static string? Problem(string? name, IReadOnlyList<ProgramStep>? steps, MaterialCatalog materials)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the program a name.";
        if (name.Length > MaxName) return $"A program's name has at most {MaxName} characters.";
        if (steps is null || steps.Count == 0) return "A program needs at least one step.";
        var ids = new HashSet<int>();
        var count = 0;
        return Check(steps, 1);

        string? Check(IReadOnlyList<ProgramStep>? list, int depth)
        {
            if (list is null) return "A list of steps is missing.";
            if (list.Count > 0 && depth > MaxDepth) return $"Ifs can be inside each other {MaxDepth - 1} deep at most.";
            foreach (var step in list)
            {
                if (step is null) return "A step is empty.";
                if (++count > MaxSteps) return $"A program has at most {MaxSteps} steps.";
                if (step.Id <= 0 || !ids.Add(step.Id)) return "Two steps have the same number.";
                if (!Enum.IsDefined(step.Kind) || !Enum.IsDefined(step.Fallback)) return "A step is of an unknown kind.";
                if (!Ok(step.Tonnes) || !Ok(step.Seconds) || !Ok(step.GiveUpSeconds) || !(step.Percent >= 0 && step.Percent <= 100)) return "A step has a number out of range.";
                if (step.Text is null || step.Text.Length > MaxText) return $"A message has at most {MaxText} characters.";
                if (step.Material is null || step.Material.Length > 0 && !materials.Contains(step.Material)) return $"Unknown material '{step.Material}'.";
                if (step.Kind == StepKind.If && step.Until is null) return "An if has nothing to test.";
                if (step.Kind != StepKind.If && (step.Then is not { Count: 0 } || step.Otherwise is not { Count: 0 })) return "Only an if has steps inside.";
                foreach (var condition in new[] { step.OnlyIf, step.Until })
                    if (condition is not null && Tests(condition) is { } wrong) return wrong;
                if (step.Kind == StepKind.If && (Check(step.Then, depth + 1) ?? Check(step.Otherwise, depth + 1)) is { } inner) return inner;
            }
            return null;
        }

        string? Tests(ProgramCondition condition)
        {
            if (condition.Tests is not { Count: > 0 }) return "A condition has no tests.";
            if (condition.Tests.Count > MaxTests) return $"A condition has at most {MaxTests} tests.";
            foreach (var test in condition.Tests)
            {
                if (test is null || !Enum.IsDefined(test.Kind)) return "A test is of an unknown kind.";
                if (!Ok(test.Value)) return "A test has a number out of range.";
                if (test.Material is null || test.Material.Length > 0 && !materials.Contains(test.Material)) return $"Unknown material '{test.Material}'.";
            }
            return null;
        }

        static bool Ok(double value) => value is >= 0 and <= 1e9;
    }

    // --- Files ---

    public const int FileVersion = 1;
    public const string FileKind = "program";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>A file to keep or share: the stations and bodies it names are written by name too, to be found in another game.</summary>
    public string ToFile(Func<uint, string?> stationName, Func<uint, string?> bodyName)
    {
        var all = AllSteps().ToList();
        var stations = all.Select(s => s.StationId).Concat(all.SelectMany(s => new[] { s.OnlyIf, s.Until }).SelectMany(c => c?.Tests ?? new()).Select(t => t.StationId)).Where(i => i != 0).Distinct().OrderBy(i => i);
        return JsonSerializer.Serialize(new ProgramFile
        {
            SchemaVersion = FileVersion, Kind = FileKind, Name = Name, Steps = Steps.ToList(),
            Stations = stations.ToDictionary(i => i.ToString(), i => stationName(i) ?? ""), Bodies = all.Select(s => s.BodyId).Where(i => i != 0).Distinct().OrderBy(i => i).ToDictionary(i => i.ToString(), i => bodyName(i) ?? ""),
        }, JsonOptions);
    }

    /// <summary>Reads such a file; what its stations and bodies are called is looked up here, and what isn't found is left to be chosen.</summary>
    /// <exception cref="FormatException">It can't be used; the message says why.</exception>
    public static (string Name, List<ProgramStep> Steps) FromFile(string json, MaterialCatalog materials, Func<string, uint> station, Func<string, uint> body)
    {
        if (json.Length > 1_000_000) throw new FormatException("The file is too big to be a program.");
        ProgramFile? file;
        try { file = JsonSerializer.Deserialize<ProgramFile>(json, JsonOptions); }
        catch (JsonException) { throw new FormatException("It isn't a program file."); }
        if (file?.Kind != FileKind) throw new FormatException("It isn't a program file.");
        if (file.SchemaVersion != FileVersion) throw new FormatException($"The file is version {file.SchemaVersion}; this build reads {FileVersion}.");
        if (Problem(file.Name, file.Steps, materials) is { } why) throw new FormatException(why);
        uint Station(uint id) => id == 0 ? 0 : station(file.Stations?.GetValueOrDefault(id.ToString()) ?? "");
        uint Body(uint id) => id == 0 ? 0 : body(file.Bodies?.GetValueOrDefault(id.ToString()) ?? "");
        return (file.Name!, file.Steps!.Select(s => Map(s, Station, Body)).ToList());
    }

    private static ProgramStep Map(ProgramStep step, Func<uint, uint> station, Func<uint, uint> body)
    {
        ProgramCondition? Condition(ProgramCondition? c) => c is null ? null : c with { Tests = c.Tests.Select(t => t with { StationId = station(t.StationId) }).ToList() };
        return step with
        {
            StationId = station(step.StationId), BodyId = body(step.BodyId), OnlyIf = Condition(step.OnlyIf), Until = Condition(step.Until),
            Then = step.Then.Select(s => Map(s, station, body)).ToList(), Otherwise = step.Otherwise.Select(s => Map(s, station, body)).ToList(),
        };
    }

    private sealed class ProgramFile
    {
        public int SchemaVersion { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public List<ProgramStep>? Steps { get; set; }
        public Dictionary<string, string>? Stations { get; set; }
        public Dictionary<string, string>? Bodies { get; set; }
    }
}

/// <summary>A step to do, or (without one) a jump. For an if, the target is where "no" goes.</summary>
internal readonly record struct ProgramOp(ProgramStep? Step, int Target);

/// <summary>A ship running a program: which, and where in it.</summary>
public sealed class ProgramJob : ShipJob
{
    public ProgramJob(int programId) => ProgramId = programId;

    public int ProgramId { get; }

    /// <summary>The step it's at (0: the first), and whether it has begun it.</summary>
    public int StepId { get; internal set; }
    public bool Begun { get; internal set; }
    public long BegunTick { get; internal set; }

    /// <summary>Since when the step can't be done (0: it can).</summary>
    public long BlockedSince { get; internal set; }

    /// <summary>It's holding still, by the program or because it has to.</summary>
    public bool Holding { get; internal set; }

    /// <summary>A repair has been asked for, so that it isn't again.</summary>
    public bool RepairAsked { get; internal set; }

    public long LastMessageTick { get; internal set; } = -1;

    /// <summary>The mining it's doing, flown as a mining job is.</summary>
    public MiningJob? Mining { get; internal set; }

    /// <summary>What it's doing, in words, set by the simulation. Not saved.</summary>
    internal string Doing { get; set; } = "";

    public override string Kind => "program";

    protected override int StateKey => StepId * 2 + (Begun ? 1 : 0);

    protected override string JobStatus => !Active ? "stopped" : WaitReason is not null ? Waits(WaitReason) : Doing.Length > 0 ? Doing : "starting";
}

/// <summary>Changes to a list of steps, by step id; each gives a new list (#120). Plain code, so it's tested without a view.</summary>
public static class ProgramEdit
{
    public static int NextId(IEnumerable<ProgramStep> steps) => JobProgram.All(steps).Select(s => s.Id).DefaultIfEmpty(0).Max() + 1;

    public static ProgramStep? Find(IEnumerable<ProgramStep> steps, int id) => JobProgram.All(steps).FirstOrDefault(s => s.Id == id);

    private static List<ProgramStep> Each(IReadOnlyList<ProgramStep> steps, Func<List<ProgramStep>, List<ProgramStep>> change) =>
        change(steps.Select(s => s.Kind != StepKind.If ? s : s with { Then = Each(s.Then, change), Otherwise = Each(s.Otherwise, change) }).ToList());

    public static List<ProgramStep> Replace(IReadOnlyList<ProgramStep> steps, ProgramStep with) =>
        Each(steps, list => list.Select(s => s.Id == with.Id ? with : s).ToList());

    public static List<ProgramStep> Remove(IReadOnlyList<ProgramStep> steps, int id) => Each(steps, list => list.Where(s => s.Id != id).ToList());

    /// <summary>Adds a step after another, at the end without one.</summary>
    public static List<ProgramStep> AddAfter(IReadOnlyList<ProgramStep> steps, int after, ProgramStep step)
    {
        if (Find(steps, after) is null) return steps.Append(step).ToList();
        return Each(steps, list =>
        {
            var at = list.FindIndex(s => s.Id == after);
            if (at >= 0) list.Insert(at + 1, step);
            return list;
        });
    }

    /// <summary>Adds a step at the end of an if's yes, or no.</summary>
    public static List<ProgramStep> AddInside(IReadOnlyList<ProgramStep> steps, int ifId, bool otherwise, ProgramStep step) =>
        Find(steps, ifId) is not { Kind: StepKind.If } at ? steps.ToList()
            : Replace(steps, otherwise ? at with { Otherwise = at.Otherwise.Append(step).ToList() } : at with { Then = at.Then.Append(step).ToList() });

    /// <summary>Moves a step up or down in its list; at an end it stays.</summary>
    public static List<ProgramStep> Move(IReadOnlyList<ProgramStep> steps, int id, int by) => Each(steps, list =>
    {
        var at = list.FindIndex(s => s.Id == id);
        if (at < 0 || at + by < 0 || at + by >= list.Count) return list;
        (list[at], list[at + by]) = (list[at + by], list[at]);
        return list;
    });

    /// <summary>A copy of a step, inner steps too, with ids of its own, after it.</summary>
    public static List<ProgramStep> Copy(IReadOnlyList<ProgramStep> steps, int id)
    {
        if (Find(steps, id) is not { } step) return steps.ToList();
        var next = NextId(steps);
        ProgramStep New(ProgramStep s) => s with { Id = next++, Then = s.Then.Select(New).ToList(), Otherwise = s.Otherwise.Select(New).ToList() };
        return AddAfter(steps, id, New(step));
    }

    /// <summary>A plain job as a program to start from, or null if there's no such program.</summary>
    public static (string Name, List<ProgramStep> Steps)? FromJob(ShipJob? job) => job switch
    {
        MiningJob m => ("Mining", new List<ProgramStep>
        {
            new() { Id = 1, Kind = StepKind.Mine, BodyId = m.SourceBodyId.Value, Material = m.MaterialId, Percent = m.ReturnFraction * 100 },
            new() { Id = 2, Kind = StepKind.Unload, StationId = m.DestinationId.Value, Material = m.MaterialId, UntilDone = true },
            new() { Id = 3, Kind = StepKind.Recharge, StationId = m.DestinationId.Value, Percent = SectorState.RechargeShare * 100, Seconds = SectorState.RechargeMaxS },
        }),
        HaulJob h => ("Freight", new List<ProgramStep>
        {
            new() { Id = 1, Kind = StepKind.Wait, Until = new() { Tests = new() { new() { Kind = TestKind.StationStockTonnes, StationId = h.PickupId.Value, Material = h.MaterialId, Value = h.MinPickupKg / 1000.0 } } } },
            new() { Id = 2, Kind = StepKind.Load, StationId = h.PickupId.Value, Material = h.MaterialId },
            new()
            {
                Id = 3, Kind = StepKind.Unload, StationId = h.DestinationId.Value, Material = h.MaterialId, UntilDone = true,
                OnlyIf = h.KeepStockedKg <= 0 ? null : new() { Tests = new() { new() { Kind = TestKind.StationStockTonnes, StationId = h.DestinationId.Value, Material = h.MaterialId, Below = true, Value = h.KeepStockedKg / 1000.0 } } },
            },
            new() { Id = 4, Kind = StepKind.Recharge, StationId = h.DestinationId.Value, Percent = SectorState.RechargeShare * 100, Seconds = SectorState.RechargeMaxS },
        }),
        _ => null,
    };
}
