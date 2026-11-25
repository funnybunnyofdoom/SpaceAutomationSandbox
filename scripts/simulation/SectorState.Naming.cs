using System;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Renaming ships and stations after they're built (#115). Everything refers to a craft by its id, so only what's shown changes.</summary>
public sealed partial class SectorState
{
    public const int MaxCraftName = 32;

    /// <summary>A name as it would be kept: one line, single spaces, no control characters.</summary>
    public static string CleanName(string? name) =>
        string.Join(' ', new string((name ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Why a craft can't have a name, or null.</summary>
    public string? RenameBlocker(CraftId id, string? name)
    {
        if (Crafts.FirstOrDefault(c => c.Id == id) is not { } craft) return "It's gone.";
        if (!IsPlayers(craft) && !craft.IsWreck) return $"{craft.Name} isn't yours to name.";
        var clean = CleanName(name);
        if (clean.Length == 0) return "It needs a name.";
        if (clean.Length > MaxCraftName) return $"At most {MaxCraftName} characters.";
        return Crafts.FirstOrDefault(c => c.Id != id && IsPlayers(c) && !c.IsWreck && string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase)) is { } other
            ? $"{other.Id} is already called {other.Name}." : null;
    }

    /// <summary>Why a body can't have a name, or null. An empty one gives it its own back (#109).</summary>
    public string? RenameBlocker(BodyId id, string? name)
    {
        if (!_byId.TryGetValue(id, out var body)) return "It's gone.";
        var clean = CleanName(name);
        if (clean.Length > MaxCraftName) return $"At most {MaxCraftName} characters.";
        if (clean.Length == 0) clean = body.OriginalName ?? body.Name;
        return _bodies.Any(b => b.Id != id && (string.Equals(b.Name, clean, StringComparison.OrdinalIgnoreCase) || string.Equals(b.OriginalName, clean, StringComparison.OrdinalIgnoreCase)))
            ? $"Another body is called {clean}." : null;
    }

    public ActionResult RenameBody(BodyId id, string? name)
    {
        if (RenameBlocker(id, name) is { } why) return new(false, why);
        var body = GetBody(id);
        var was = body.Name;
        body.Rename(CleanName(name) is { Length: > 0 } clean ? clean : null);
        return new(true, $"{was} is now {body.Name}.");
    }

    public ActionResult RenameCraft(CraftId id, string? name)
    {
        if (RenameBlocker(id, name) is { } why) return new(false, why);
        var craft = GetCraft(id);
        var was = craft.Name;
        craft.Name = CleanName(name);
        return new(true, $"{was} is now {craft.Name}.");
    }
}
