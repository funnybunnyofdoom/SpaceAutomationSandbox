using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>What combat (#84, #85) looks like: shots, laser beams, enemy and wreck markers, and the ship's weapons line.</summary>
public partial class Main
{
    private static readonly Color ShotColor = new(1f, 0.9f, 0.5f);
    private static readonly Color MissileColor = new(1f, 0.55f, 0.35f);
    private static readonly Color ShieldColor = new(0.5f, 0.6f, 1f);
    private static readonly Color BeamColor = new(0.5f, 0.9f, 1f, 0.9f);
    private static readonly Color EnemyBeamColor = new(1f, 0.35f, 0.5f, 0.9f);
    private static readonly Color WreckColor = new(0.6f, 0.6f, 0.6f);

    private static readonly Color NeutralColor = new(0.85f, 0.85f, 0.95f);

    private Color CraftTint(CraftState craft) =>
        craft.IsWreck ? WreckColor : SectorState.IsPlayers(craft) ? MarkerColor : _sector.IsEnemy(craft) ? Palette.Bad : NeutralColor;

    /// <summary>Name tags say what it is in words too, not by colour alone (#68).</summary>
    private string CraftTag(CraftState craft) => craft.IsWreck ? $"{craft.Name} (wreck)" : SectorState.IsPlayers(craft) ? craft.Name
        : _sector.IsEnemy(craft) ? $"! {craft.Name}" : $"{craft.Name} ({_sector.FactionById(craft.FactionId)?.Disposition.ToString().ToLowerInvariant() ?? "unknown"})";

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
            DrawLine(px - direction * length, px, shot.IsMissile ? MissileColor : ShotColor, shot.IsMissile ? 4f : 2f);
            if (shot.IsMissile) DrawLine(px - direction * (length + 10), px - direction * length, FlameColor, 2f);
        }
        // Shields (#90): a faint dashed bubble, brighter just after a hit, flickering when the power fails.
        foreach (var (craft, at, radius) in _visibleCrafts)
        {
            if (craft.ShieldCharge <= 0 || radius < 4f) continue;
            var share = (float)(craft.ShieldCharge / System.Math.Max(craft.Stats.ShieldCapacity, 1e-9));
            var sinceHit = _sector.Tick - craft.ShieldHitTick;
            var alpha = 0.15f + 0.25f * share + (sinceHit < 8 ? 0.4f * (1 - sinceHit / 8f) : 0f);
            if (craft.ShieldPowerless && _sector.Tick / 3 % 2 == 0) alpha *= 0.3f;
            for (var i = 0; i < 12; i++)
                DrawArc(at, radius + 3, Mathf.Tau * i / 12, Mathf.Tau * (i + 0.75f) / 12, 5, new Color(ShieldColor, alpha), 2f);
        }
        foreach (var (tick, where, reach) in _sector.Blasts) // antimatter (#81)
        {
            var age = (_sector.Tick - tick + _warp.Alpha) / FixedStepClock.TicksPerSecond;
            var blast = (float)(reach * _pixelsPerMetre * Mathf.Min(1f, 0.2f + 0.8f * (float)age));
            DrawCircle(ToPixels(where), blast, new Color(1f, 0.8f, 1f, 0.5f * Mathf.Max(0f, 1f - (float)age)));
            DrawArc(ToPixels(where), blast, 0, Mathf.Tau, 64, new Color(1f, 0.5f, 1f, Mathf.Max(0f, 1f - (float)age)), 3f);
        }
        // A ring round each enemy on screen, so a raid can be found in a crowd.
        foreach (var (craft, at, radius) in _visibleCrafts)
            if (!craft.IsWreck && _sector.IsEnemy(craft))
                DrawArc(at, Mathf.Max(10f, radius + 4), 0, Mathf.Tau, 24, Palette.Bad, 1.5f);
    }

    /// <summary>"weapons: 1 gun (200 kg shells), 1 laser; holding fire" for the HUD; empty for unarmed craft.</summary>
    private string WeaponsLine(CraftState craft)
    {
        if (craft.IsWreck) return "WRECK: fly another craft (Fleet)\n";
        var weapons = craft.Stats.WeaponList;
        var shield = craft.Stats.ShieldCapacity > 0 // #90
            ? $"shield {craft.ShieldCharge / craft.Stats.ShieldCapacity * 100:0}%{(craft.ShieldPowerless ? "  NO POWER: SHIELD FAILING" : "")}\n" : "";
        if (weapons.Count == 0) return shield;
        var guns = weapons.Count(w => w.Weapon.Kind == WeaponKind.Gun);
        var shells = craft.Inventory[SectorState.ShellMaterial];
        var parts = new System.Collections.Generic.List<string>();
        if (guns > 0) parts.Add($"{guns} gun{(guns == 1 ? "" : "s")} ({(shells > 0 ? $"{shells} kg shells" : "NO SHELLS")})");
        var launchers = weapons.Count(w => w.Weapon.Kind == WeaponKind.Missile);
        var missiles = craft.Inventory[SectorState.MissileMaterial];
        if (launchers > 0) parts.Add($"{launchers} missile launcher{(launchers == 1 ? "" : "s")} ({(missiles > 0 ? $"{missiles} kg missiles" : "NO MISSILES")})");
        var defense = weapons.Count(w => w.Weapon.PointDefense);
        if (defense > 0) parts.Add($"{defense} point defense");
        var lasers = weapons.Count - guns - launchers - defense;
        if (lasers > 0) parts.Add($"{lasers} laser{(lasers == 1 ? "" : "s")}");
        return $"{shield}weapons: {string.Join(", ", parts)}{(craft.HoldFire ? " - HOLDING FIRE" : "")} ({InputActions.Key1(InputActions.HoldFire)})\n";
    }

    /// <summary>Antimatter containment and the quantum drive, for the HUD (#81, #82).</summary>
    private string FutureTechLine(CraftState craft)
    {
        var line = "";
        var antimatter = craft.Inventory[SectorState.AntimatterMaterial];
        if (antimatter > 0 || craft.Stats.AntimatterCapacityKg > 0)
            line += $"antimatter {antimatter} / {craft.Stats.AntimatterCapacityKg:0} kg"
                    + (craft.ContainmentFailS > 0 ? $"  DANGER: CONTAINMENT FAILS IN {System.Math.Max(0, SectorState.ContainmentGraceS - craft.ContainmentFailS):0} s" : "") + "\n";
        if (craft.Stats.QuantumDriveKW <= 0) return line;
        var need = SectorState.QuantumChargeNeededKJ(craft);
        return line + $"quantum drive {craft.QuantumChargeKJ / need * 100:0}%"
               + (_sector.Tick < craft.QuantumReadyTick ? $", cooling down {(craft.QuantumReadyTick - _sector.Tick) * FixedStepClock.TickSeconds:0} s" : craft.QuantumRequested ? ", charging to jump" : "")
               + $" ({InputActions.Key1(InputActions.Quantum)})\n";
    }

    private void ToggleHoldFire()
    {
        if (_player is null) return;
        if (_player.Stats.WeaponList.Count == 0) { ShowStatus($"{_player.Name} has no weapons."); return; }
        // Targeting a civilization's ship that isn't an enemy: an attack needs a yes first (#89).
        if (_sector.Crafts.FirstOrDefault(c => c.Id == _player.TargetCraftId) is { IsWreck: false } target && !SectorState.IsPlayers(target)
            && !_sector.IsEnemy(target) && _sector.FactionById(target.FactionId) is { } faction)
        {
            _confirm.Ask($"Attack {target.Name}? The {faction.Name} are {faction.Disposition.ToString().ToLowerInvariant()} now; they will become hostile and stay so for hours.",
                ("Attack", () =>
                {
                    _player.HoldFire = false;
                    _changedSinceSave = true;
                    ShowStatus(_sector.DeclareAttack(faction.Id).Message);
                }));
            return;
        }
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
        var design = new ShipDesign("demo_gunship", "Demo gunship", SectorState.RaiderDesign.Cells.Concat(new[]
        {
            new DesignCell(new GridPos(-2, 0), "missile_launcher"), new DesignCell(new GridPos(2, 0), "point_defense"), new DesignCell(new GridPos(0, 2), "shield_generator"),
            new DesignCell(new GridPos(-1, -2), "armour_plate"), new DesignCell(new GridPos(1, -2), "armour_plate"),
        }));
        var outward = gate.Position - _sector.GetPosition(_sector.NearestBody(gate.Position).Body.Id);
        var at = gate.Position + outward * (1_200 / outward.Length); // between the gate and the raiders, who arrive 12 km out
        var ship = new CraftState(_sector.AllocateCraftId(), "Gunship", design, design.ComputeStats(_catalog), at, System.Math.Atan2(outward.X, -outward.Y)) { Velocity = gate.Velocity };
        _sector.AddCraft(ship);
        _sector.DebugAddStock(ship.Id, SectorState.ShellMaterial, 400);
        _sector.DebugAddStock(ship.Id, SectorState.MissileMaterial, 400);
        Pilot(ship.Id);
        _sector.SetTargetCraft(ship.Id, gate.Id); // the brake holds it beside the gate
        _sector.StartRaid(faction, gate, SectorState.MissileRaidShips);
        for (var i = 0; i < 300 * FixedStepClock.TicksPerSecond && (_sector.Beams.Count == 0 || _sector.Shots.Count == 0); i++)
        {
            ship.Controls = new ShipControls(0, 0, true);
            _sector.Step();
        }
    }
}
