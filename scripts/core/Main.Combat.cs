using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>What combat (#84, #85) looks like: shots, laser beams, enemy and wreck markers, and the ship's weapons line.</summary>
public partial class Main
{
    private static readonly Color ShotColor = new(1f, 0.9f, 0.5f);
    private static readonly Color BeamColor = new(0.5f, 0.9f, 1f, 0.9f);
    private static readonly Color EnemyBeamColor = new(1f, 0.35f, 0.5f, 0.9f);
    private static readonly Color WreckColor = new(0.6f, 0.6f, 0.6f);

    private static Color CraftTint(CraftState craft) =>
        craft.IsWreck ? WreckColor : SectorState.IsPlayers(craft) ? MarkerColor : Palette.Bad;

    /// <summary>Name tags say what it is in words too, not by colour alone (#68).</summary>
    private static string CraftTag(CraftState craft) => craft.IsWreck ? $"{craft.Name} (wreck)" : SectorState.IsPlayers(craft) ? craft.Name : $"! {craft.Name}";

    private void DrawCombat()
    {
        foreach (var beam in _sector.Beams)
            DrawLine(ToPixels(beam.From), ToPixels(beam.To), beam.FactionId == 0 ? BeamColor : EnemyBeamColor, 2f);
        foreach (var shot in _sector.Shots)
        {
            var at = shot.Position - shot.Velocity * ((1 - _warp.Alpha) * FixedStepClock.TickSeconds); // between ticks
            var px = ToPixels(at);
            var length = Mathf.Clamp((float)shot.Velocity.Length * 0.02f * _pixelsPerMetre, 3f, 24f);
            var direction = new Vector2((float)shot.Velocity.X, (float)shot.Velocity.Y).Normalized();
            DrawLine(px - direction * length, px, ShotColor, 2f);
        }
        // A ring round each enemy on screen, so a raid can be found in a crowd.
        foreach (var (craft, at, radius) in _visibleCrafts)
            if (!SectorState.IsPlayers(craft) && !craft.IsWreck)
                DrawArc(at, Mathf.Max(10f, radius + 4), 0, Mathf.Tau, 24, Palette.Bad, 1.5f);
    }

    /// <summary>"weapons: 1 gun (200 kg shells), 1 laser; holding fire" for the HUD; empty for unarmed craft.</summary>
    private string WeaponsLine(CraftState craft)
    {
        if (craft.IsWreck) return "WRECK: fly another craft (Fleet)\n";
        var weapons = craft.Stats.WeaponList;
        if (weapons.Count == 0) return "";
        var guns = weapons.Count(w => w.Weapon.Kind == WeaponKind.Gun);
        var shells = craft.Inventory[SectorState.ShellMaterial];
        var parts = new System.Collections.Generic.List<string>();
        if (guns > 0) parts.Add($"{guns} gun{(guns == 1 ? "" : "s")} ({(shells > 0 ? $"{shells} kg shells" : "NO SHELLS")})");
        if (weapons.Count > guns) parts.Add($"{weapons.Count - guns} laser{(weapons.Count - guns == 1 ? "" : "s")}");
        return $"weapons: {string.Join(", ", parts)}{(craft.HoldFire ? " - HOLDING FIRE" : "")} ({InputActions.Key1(InputActions.HoldFire)})\n";
    }

    private void ToggleHoldFire()
    {
        if (_player is null) return;
        if (_player.Stats.WeaponList.Count == 0) { ShowStatus($"{_player.Name} has no weapons."); return; }
        _player.HoldFire = !_player.HoldFire;
        _changedSinceSave = true;
        ShowStatus(_player.HoldFire ? $"{_player.Name}: holding fire." : $"{_player.Name}: weapons free. They fire at enemies by themselves; your target comes first.");
    }

    /// <summary>Dev/test hook: <c>--demo-raid</c> makes the player's ship a gunship facing a raid on a far gate (nothing is saved).</summary>
    private void DemoRaid(string[] args)
    {
        if (System.Array.IndexOf(args, "--demo-raid") < 0 || _catalog is null || _player is null) return;
        DemoWarp();
        for (var i = 0; i < 60 * FixedStepClock.TicksPerSecond && _sector.Stars.Count < 2; i++) _sector.Step();
        if (_sector.Crafts.LastOrDefault(SectorState.IsGate) is not { } gate || _sector.StarAt(gate.Position) is not { } star || _sector.Stars.Count < 2) return;
        var faction = _sector.MeetCivilization(star, cultureId: "pacifist", baseline: -80);
        var design = new ShipDesign("demo_gunship", "Demo gunship", SectorState.RaiderDesign.Cells);
        var outward = gate.Position - _sector.GetPosition(_sector.NearestBody(gate.Position).Body.Id);
        var at = gate.Position + outward * (1_200 / outward.Length); // between the gate and the raiders, who arrive 12 km out
        var ship = new CraftState(_sector.AllocateCraftId(), "Gunship", design, design.ComputeStats(_catalog), at, System.Math.Atan2(outward.X, -outward.Y)) { Velocity = gate.Velocity };
        _sector.AddCraft(ship);
        _sector.DebugAddStock(ship.Id, SectorState.ShellMaterial, 400);
        Pilot(ship.Id);
        _sector.SetTargetCraft(ship.Id, gate.Id); // the brake holds it beside the gate
        _sector.StartRaid(faction, gate, 3);
        for (var i = 0; i < 300 * FixedStepClock.TicksPerSecond && (_sector.Beams.Count == 0 || _sector.Shots.Count == 0); i++)
        {
            ship.Controls = new ShipControls(0, 0, true);
            _sector.Step();
        }
    }
}
