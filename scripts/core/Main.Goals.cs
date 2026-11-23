using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// The goal list for new players (#65): an optional checklist of the core loop in the HUD's Goals
/// area, ticked from the simulation state (see <see cref="GoalList"/>). Chosen on the new-game screen,
/// turned off or restarted from the pause menu, and kept in the save header.
/// </summary>
public partial class Main
{
    private bool _goalsOn;
    private readonly HashSet<string> _goalsDone = new();
    private double _sinceGoalCheckS;

    /// <summary>Checks the goals about once a second (cheap enough, and not tied to the frame rate).</summary>
    private void UpdateGoals(double delta)
    {
        if (!_goalsOn || InMenu || _sector is null) return;
        _sinceGoalCheckS += delta;
        if (_sinceGoalCheckS < 1) return;
        _sinceGoalCheckS = 0;
        var before = GoalList.Current(_goalsDone);
        var designs = BlueprintLibrary.List().Count(e => !e.IsBuiltIn);
        if (!GoalList.Update(_goalsDone, _sector, _player?.Id ?? CraftId.None, designs)) return;
        _changedSinceSave = true;
        var now = GoalList.Current(_goalsDone);
        ShowStatus(now is null ? "All goals done. The rest is yours to build." : before is not null && now != before ? $"Goal done: {before.Title}." : "Goal done.");
    }

    private string GoalsText()
    {
        if (!_goalsOn) return "";
        var current = GoalList.Current(_goalsDone);
        var text = new StringBuilder();
        foreach (var step in GoalList.Steps)
        {
            var mark = _goalsDone.Contains(step.Id) ? "[x]" : step == current ? "-> " : "[ ]";
            text.Append(mark).Append(' ').Append(step.Title).Append('\n');
            if (step == current) text.Append("     ").Append(WithKeys(step.Hint)).Append('\n');
        }
        if (current is null) text.Append("All done. Hide this list from the pause menu.\n");
        return text.ToString();
    }

    /// <summary>Replaces [ActionName] in a hint with that action's current key.</summary>
    private static string WithKeys(string hint) => Regex.Replace(hint, @"\[(\w+)\]", m =>
        typeof(InputActions).GetField(m.Groups[1].Value)?.GetValue(null) is string action ? InputActions.Key1(action) : m.Value);
}
