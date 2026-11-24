using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>A gun's shot in flight (#85). Saved.</summary>
public sealed class Shot
{
    public Vector2D Position { get; internal set; }
    public Vector2D Velocity { get; init; }
    public long ExpireTick { get; init; }
    public double Damage { get; init; }

    /// <summary>Whose shot: it only hits the other side's craft.</summary>
    public int FactionId { get; init; }
}

/// <summary>A laser hit this tick, for drawing. Not saved.</summary>
public readonly record struct Beam(long Tick, Vector2D From, Vector2D To, int FactionId);

/// <summary>
/// Combat (#84, #85; chosen by the creator). Craft belong to the player (faction 0) or to a hostile
/// civilization's raid; the two sides fire on each other by themselves.
/// <list type="bullet">
/// <item><b>Near the player</b> weapons fire for real: guns shoot shots that fly and can miss, lasers hit at once and
/// weaken with distance; bodies block both. <b>Far away</b> (#17) the same weapons, ranges, arcs, power and ammunition are
/// resolved once a second, with a chance to hit.</item>
/// <item>A hit takes health from a part (tough parts take less); at 0 it breaks off, as after a crash (#34).</item>
/// <item>A ship with no control source left, a gate that lost its warp gate part or every docking port, or a station shot down to a quarter of its
/// mass becomes a <b>wreck</b>: it drifts with whatever it holds and does nothing (salvage: #86).</item>
/// <item>A raid sends real raiders against your gate in the raiders' own system, and never anywhere else. They
/// leave when the gate is gone or the raid has run its time.</item>
/// </list>
/// Costs nothing while no raid is on. Deterministic: rolls come from the seed, the tick and the craft.
/// </summary>
public sealed partial class SectorState
{
    public const string ShellMaterial = "shells";

    /// <summary>How far from the gate raiders arrive, and how long a raid lasts at most.</summary>
    public const double RaidArriveM = 12_000, RaidDurationS = 600;

    /// <summary>Raiders turn on armed craft of yours this close to them before the gate.</summary>
    public const double RaiderEngageM = 1_500;

    /// <summary>Chance that a gun's shot hits in a fight resolved far from the player.</summary>
    public const double FarGunHitChance = 0.6;

    /// <summary>A laser's damage at the end of its range, as a share of its damage point blank.</summary>
    public const double LaserFarShare = 0.4;

    /// <summary>A station with less than this share of its intact mass left is a wreck.</summary>
    public const double WreckMassShare = 0.25;

    private readonly List<Shot> _shots = new();
    private readonly List<Beam> _beams = new();
    private readonly List<CraftState> _raiders = new();

    public IReadOnlyList<Shot> Shots => _shots;

    /// <summary>Laser hits in the last few ticks, for drawing.</summary>
    public IReadOnlyList<Beam> Beams => _beams;

    public void RestoreShot(Shot shot) => _shots.Add(shot);

    /// <summary>The raiders' ship: a gun, a laser and solar power. Their own, so no tech is needed.</summary>
    public static ShipDesign RaiderDesign { get; } = new("raider", "Raider", new[]
    {
        new DesignCell(new GridPos(0, -2), "autocannon"), new DesignCell(new GridPos(-1, -1), "laser"), new DesignCell(new GridPos(0, -1), "cockpit"),
        new DesignCell(new GridPos(1, -1), "solar_panel"), new DesignCell(new GridPos(-1, 0), "solar_panel"), new DesignCell(new GridPos(0, 0), "battery"),
        new DesignCell(new GridPos(1, 0), "solar_panel"), new DesignCell(new GridPos(-1, 1), "thruster"), new DesignCell(new GridPos(0, 1), "hull"),
        new DesignCell(new GridPos(1, 1), "thruster"),
    });

    private const long RaiderShellsKg = 200;

    /// <summary>Is this craft one of the player's (not a raider's, living or wrecked)?</summary>
    public static bool IsPlayers(CraftState craft) => craft.FactionId == 0;

    private static bool CanBeShot(CraftState craft) => !craft.IsWreck && !craft.Cruising && craft.Transit is null;

    // --- Raids ---

    private bool RaidOn(Faction faction) => !faction.RaidGateId.IsNone;

    /// <summary>A hostile civilization sends raiders against a gate in its system (#89 will add fleets).</summary>
    public void StartRaid(Faction faction, CraftState gate, int ships)
    {
        if (Parts is null || ships < 1) return;
        var rng = new DeterministicRandom(Seed ^ ((ulong)faction.Id * 0xC2B2AE3D27D4EB4FUL) ^ (ulong)Tick);
        var stats = RaiderDesign.ComputeStats(Parts);
        // From the side away from the planet the gate orbits, so nothing is in the way.
        var (body, _) = NearestBody(gate.Position);
        var outward = gate.Position - GetPosition(body.Id);
        var bearing = Math.Atan2(outward.Y, outward.X) + rng.Range(-0.6, 0.6);
        for (var i = 0; i < ships; i++)
        {
            var angle = bearing + (i - (ships - 1) / 2.0) * 0.02;
            var position = gate.Position + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * RaidArriveM;
            var raider = new CraftState(AllocateCraftId(), $"{faction.Name} raider {i + 1}", RaiderDesign, stats, position, Math.Atan2(-Math.Cos(angle), Math.Sin(angle)))
            {
                FactionId = faction.Id, Velocity = gate.Velocity,
            };
            raider.Inventory.Add(ShellMaterial, RaiderShellsKg);
            AddCraft(raider);
            DiscoveredKg += (long)Math.Round(stats.TotalMassKg) + RaiderShellsKg; // from outside: the mass ledger stays balanced
        }
        faction.RaidGateId = gate.Id;
        faction.RaidEndTick = Tick + (long)(RaidDurationS * FixedStepClock.TicksPerSecond);
        AddEvent($"{ships} {faction.Name} raiders are attacking {gate.Name}!");
    }

    /// <summary>Once a second per raid: is it over?</summary>
    private void StepRaids()
    {
        foreach (var faction in _factions)
        {
            if (!RaidOn(faction)) continue;
            var gate = _craftsById.GetValueOrDefault(faction.RaidGateId);
            var alive = _crafts.Where(c => c.FactionId == faction.Id && !c.IsWreck).ToList();
            if (alive.Count > 0 && gate is not null && IsGate(gate) && !gate.IsWreck && Tick < faction.RaidEndTick) continue;
            if (gate is not null && IsGate(gate) && !gate.IsWreck)
                AddEvent(alive.Count == 0 ? $"The {faction.Name} raided {gate.Name}; your defenses drove them off."
                    : $"The {faction.Name} raid on {gate.Name} is over: their raiders withdrew.");
            foreach (var raider in alive) // they leave the way they came, with what they carry
            {
                DiscoveredKg -= (long)Math.Round(raider.Stats.TotalMassKg) + raider.CargoKg;
                RemoveCraft(raider);
            }
            faction.RaidGateId = CraftId.None;
        }
    }

    /// <summary>Raiders steer themselves: to the gate (or the nearest armed craft of yours), then face it and hold.</summary>
    private void SteerRaiders()
    {
        _raiders.Clear();
        foreach (var craft in _crafts) if (!IsPlayers(craft) && !craft.IsWreck) _raiders.Add(craft);
        foreach (var raider in _raiders)
        {
            if (_factions.FirstOrDefault(f => f.Id == raider.FactionId) is not { } faction) { raider.Controls = ShipControls.Idle; continue; }
            CraftState? target = null;
            var nearest = RaiderEngageM;
            foreach (var craft in _crafts)
            {
                if (!IsPlayers(craft) || craft.Stats.WeaponList.Count == 0 || !CanBeShot(craft) || craft.IsDocked) continue;
                var d = craft.Position.DistanceTo(raider.Position);
                if (d < nearest) { nearest = d; target = craft; }
            }
            target ??= _craftsById.GetValueOrDefault(faction.RaidGateId);
            if (target is null) { raider.Controls = new ShipControls(0, 0, true); continue; }
            SetTargetCraft(raider.Id, target.Id);
            var to = target.Position - raider.Position;
            if (to.Length > 500)
            {
                // A spot short of the target, on its own side of it, a little apart from the others.
                var side = new Vector2D(-to.Y, to.X) * (1 / to.Length) * ((raider.Id.Value % 5 - 2.0) * 60);
                var spot = target.Position - to * (380 / to.Length) + side;
                raider.Controls = NavigateTo(raider, spot, target.Velocity, BodyId.None, JobCruiseSpeedMps, 10, target.Id);
                SetTargetCraft(raider.Id, target.Id);
            }
            else raider.Controls = new ShipControls(0, Autopilot.TurnToward(raider, to), true);
        }
    }

    // --- Firing ---

    private void StepCombat(double endS)
    {
        if (_beams.Count > 0) _beams.RemoveAll(b => b.Tick < Tick - 3);
        if (Tick % FixedStepClock.TicksPerSecond == 0) StepRaids();
        StepMagazines();
        if (_raiders.Count > 0 && Parts is not null)
        {
            var count = _crafts.Count; // wrecks stay in the list, so it doesn't change here
            for (var i = 0; i < count; i++) FireWeapons(_crafts[i], endS);
        }
        if (_shots.Count > 0) StepShots(endS);
    }

    private void FireWeapons(CraftState craft, double endS)
    {
        var weapons = craft.Stats.WeaponList;
        if (weapons.Count == 0 || craft.IsWreck || craft.HoldFire || craft.IsDocked || !CanBeShot(craft) || !Operational(craft)) return;
        var far = FarFromObserver(craft.Position);
        if (far && (Tick + craft.Id.Value) % FixedStepClock.TicksPerSecond != 0) return;
        var rng = new DeterministicRandom(Seed ^ ((ulong)craft.Id.Value * 0x9E3779B97F4A7C15UL) ^ ((ulong)Tick * 0xD1B54A32D192ED03UL));
        foreach (var mount in weapons)
        {
            if (mount.Weapon.NeedsArmedGates && IsPlayers(craft) && !LawActive(Laws.ArmedGates)) continue;
            if (craft.IsWreck) return;
            var weapon = mount.Weapon;
            if (far)
            {
                // A second of this weapon at once.
                var shots = Math.Max(1, (int)Math.Round(1 / weapon.IntervalS));
                if (weapon.IntervalS > 1 && rng.NextDouble() > 1 / weapon.IntervalS) continue;
                for (var s = 0; s < shots; s++)
                {
                    if (PickTarget(craft, mount, endS) is not { } target || !PayForShot(craft, weapon)) break;
                    if (weapon.Kind == WeaponKind.Gun && rng.NextDouble() >= FarGunHitChance) continue;
                    var cell = (int)(rng.NextUInt64() % (ulong)target.Design.Cells.Count);
                    HitCell(target, cell, ShotDamage(craft, mount, target));
                }
                continue;
            }
            if (craft.WeaponReadyTicks.GetValueOrDefault(mount.Pos) > Tick) continue;
            if (PickTarget(craft, mount, endS) is not { } aim || !PayForShot(craft, weapon)) continue;
            craft.WeaponReadyTicks[mount.Pos] = Tick + Math.Max(1, (long)Math.Round(weapon.IntervalS * FixedStepClock.TicksPerSecond));
            var muzzle = MountPosition(craft, mount);
            if (weapon.Kind == WeaponKind.Laser)
            {
                var (cell, at) = NearestCell(aim, muzzle);
                _beams.Add(new Beam(Tick, muzzle, at, craft.FactionId));
                HitCell(aim, cell, ShotDamage(craft, mount, aim));
                continue;
            }
            // Lead the target, with a little scatter.
            var relative = aim.Velocity - craft.Velocity;
            var to = aim.Position - muzzle;
            var flight = to.Length / weapon.ShotSpeedMps;
            for (var i = 0; i < 3; i++) flight = (to + relative * flight).Length / weapon.ShotSpeedMps;
            var lead = to + relative * flight;
            var angle = Math.Atan2(lead.Y, lead.X) + rng.Range(-0.012, 0.012);
            _shots.Add(new Shot
            {
                Position = muzzle, Velocity = craft.Velocity + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * weapon.ShotSpeedMps,
                ExpireTick = Tick + (long)Math.Ceiling(1.2 * weapon.RangeM / weapon.ShotSpeedMps * FixedStepClock.TicksPerSecond),
                Damage = weapon.DamagePerShot * mount.Health, FactionId = craft.FactionId,
            });
        }
    }

    /// <summary>The craft's chosen target if this weapon can hit it, else the nearest enemy it can.</summary>
    private CraftState? PickTarget(CraftState craft, WeaponMount mount, double endS)
    {
        var muzzle = MountPosition(craft, mount);
        CraftState? best = null;
        var bestDistance = mount.Weapon.RangeM;
        // The player's craft only look at raiders; raiders look at everything of the player's.
        IReadOnlyList<CraftState> candidates = IsPlayers(craft) ? _raiders : _crafts;
        foreach (var other in candidates)
        {
            if (IsPlayers(other) == IsPlayers(craft) || !CanBeShot(other)) continue;
            var d = other.Position.DistanceTo(muzzle);
            if (d > mount.Weapon.RangeM) continue;
            var chosen = other.Id == craft.TargetCraftId;
            if (!chosen && d >= bestDistance) continue;
            if (!InArc(craft, mount, other.Position - muzzle) || BodyBetween(muzzle, other.Position, endS)) continue;
            if (chosen) return other;
            best = other;
            bestDistance = d;
        }
        return best;
    }

    private bool BodyBetween(Vector2D a, Vector2D b, double timeS)
    {
        var positions = AllBodyPositions(timeS);
        for (var i = 0; i < _bodies.Count; i++)
            if (!SegmentClears(a, b, positions[i], _bodies[i].RadiusM - 5)) return true; // craft on the ground aren't hidden by the ground they stand on
        return false;
    }

    private static Vector2D MountPosition(CraftState craft, WeaponMount mount)
    {
        var x = (mount.Pos.X - craft.Stats.CenterOfMassX) * ShipDesign.CellSizeM;
        var y = (mount.Pos.Y - craft.Stats.CenterOfMassY) * ShipDesign.CellSizeM;
        var (sin, cos) = Math.SinCos(craft.HeadingRad);
        return craft.Position + new Vector2D(x * cos - y * sin, x * sin + y * cos);
    }

    private static bool InArc(CraftState craft, WeaponMount mount, Vector2D to)
    {
        if (mount.Weapon.ArcDeg >= 360) return true;
        var facing = FlightModel.ForwardFromHeading(craft.HeadingRad + mount.Rotation * Math.PI / 2);
        var length = Math.Max(to.Length, 1e-9);
        return (facing.X * to.X + facing.Y * to.Y) / length >= Math.Cos(mount.Weapon.ArcDeg / 2 * Math.PI / 180);
    }

    /// <summary>Takes the shot's power and ammunition, both or neither. Spent ammunition leaves the sector (ejected mass).</summary>
    private bool PayForShot(CraftState craft, WeaponDefinition weapon)
    {
        if (weapon.AmmoKgPerShot > 0 && craft.Inventory[weapon.AmmoMaterial!] < weapon.AmmoKgPerShot) return false;
        if (!TryUsePower(craft, weapon.EnergyKJPerShot)) return false;
        if (weapon.AmmoKgPerShot > 0)
        {
            craft.Inventory.TryRemove(new Dictionary<string, long> { [weapon.AmmoMaterial!] = weapon.AmmoKgPerShot });
            _ejectedKg[weapon.AmmoMaterial!] = _ejectedKg.GetValueOrDefault(weapon.AmmoMaterial!) + weapon.AmmoKgPerShot;
        }
        return true;
    }

    private static double ShotDamage(CraftState craft, WeaponMount mount, CraftState target)
    {
        var damage = mount.Weapon.DamagePerShot * mount.Health;
        if (mount.Weapon.Kind == WeaponKind.Gun) return damage;
        var share = Math.Clamp(target.Position.DistanceTo(craft.Position) / mount.Weapon.RangeM, 0, 1);
        return damage * (1 - (1 - LaserFarShare) * share);
    }

    private (int Cell, Vector2D At) NearestCell(CraftState craft, Vector2D from)
    {
        var count = FillCellPositions(craft, ref _cellsA);
        var best = 0;
        for (var i = 1; i < count; i++)
            if (_cellsA[i].DistanceTo(from) < _cellsA[best].DistanceTo(from)) best = i;
        return (best, _cellsA[best]);
    }

    /// <summary>Moves the shots; each hits the first enemy cell in its way this tick, or the ground.</summary>
    private void StepShots(double endS)
    {
        var positions = AllBodyPositions(endS);
        for (var s = _shots.Count - 1; s >= 0; s--)
        {
            var shot = _shots[s];
            var from = shot.Position;
            var to = from + shot.Velocity * FixedStepClock.TickSeconds;
            shot.Position = to;
            CraftState? hit = null;
            var hitCell = -1;
            var along = double.MaxValue;
            foreach (var craft in _crafts)
            {
                if (IsPlayers(craft) == (shot.FactionId == 0) || !CanBeShot(craft)) continue;
                // The craft moved this tick too: sweep against its motion.
                var a = from + (craft.Position - craft.PreviousPosition);
                if (SegmentClears(a, to, craft.Position, craft.CollisionRadiusM + CellContactRadiusM)) continue;
                var count = FillCellPositions(craft, ref _cellsA);
                for (var i = 0; i < count; i++)
                {
                    if (SegmentClears(a, to, _cellsA[i], CellContactRadiusM)) continue;
                    var d = a.DistanceTo(_cellsA[i]);
                    if (d < along) { along = d; hit = craft; hitCell = i; }
                }
            }
            var spent = hit is not null || Tick >= shot.ExpireTick;
            if (hit is not null) HitCell(hit, hitCell, shot.Damage);
            for (var i = 0; !spent && i < _bodies.Count; i++) spent = positions[i].DistanceTo(to) < _bodies[i].RadiusM;
            if (spent) _shots.RemoveAt(s);
        }
    }

    // --- Being hit ---

    private void HitCell(CraftState craft, int cellIndex, double damage)
    {
        if (craft.IsWreck || Parts is null || cellIndex >= craft.Design.Cells.Count) return;
        var cell = craft.Design.Cells[cellIndex];
        damage /= Math.Max(1, Parts.Get(cell.PartId).Toughness);
        var health = craft.HealthList.GetValueOrDefault(cell.Pos, 1) - damage;
        var wasGate = IsGate(craft);
        var controlled = HasControl(craft);
        if (health > 0)
        {
            craft.HealthList[cell.Pos] = health;
            craft.Stats = craft.Design.ComputeStats(Parts, craft.HealthList);
            return;
        }
        BreakOff(craft, new List<DesignCell> { cell });
        var intact = (craft.IntactDesign ?? craft.Design).ComputeStats(Parts).TotalMassKg;
        if ((controlled && !HasControl(craft)) || (wasGate && !IsGate(craft))
            || craft.Design.Cells.Count == 1 || (!controlled && craft.Stats.TotalMassKg < WreckMassShare * intact))
            MakeWreck(craft, wasGate);
    }

    private bool HasControl(CraftState craft)
    {
        foreach (var cell in craft.Design.Cells) if (Parts!.Get(cell.PartId).IsControlSource) return true;
        return false;
    }

    /// <summary>The craft is done for: it keeps its parts and everything it holds, and drifts (or stays on its orbit).</summary>
    private void MakeWreck(CraftState craft, bool wasGate)
    {
        craft.IsWreck = true;
        craft.Controls = ShipControls.Idle;
        craft.Job = null;
        craft.YardOrder = null;
        craft.YardProgressKg = 0;
        craft.ServiceAt = CraftId.None;
        craft.JumpRequestTick = 0;
        craft.RequestsList.Clear();
        craft.WeaponReadyTicks.Clear();
        ReleaseClaim(craft);
        craft.DockedToCraftId = CraftId.None;
        foreach (var ship in _crafts)
        {
            if (ship.DockedToCraftId == craft.Id) ship.DockedToCraftId = CraftId.None; // left floating there
            if (ship.DockClaimStation == craft.Id) ReleaseClaim(ship);
        }
        AddEvent(IsPlayers(craft) ? $"{craft.Name} was destroyed: its wreck is adrift." : $"{craft.Name} was destroyed.");
        if (!wasGate) return;
        // Its twin stays tied to this system (#36): the next jump sends a new far gate through, from its own stock.
        var star = StarAt(craft.Position);
        var owner = star is null ? null : FactionAtStar(star.Id);
        foreach (var near in _crafts.Where(c => c.GateLinkId == craft.Id).ToList())
        {
            near.GateLinkId = CraftId.None;
            if (star is null) continue;
            near.GateLostStarId = star.Id;
            AddEvent($"The {owner?.Name ?? "enemy"} destroyed {craft.Name}. {near.Name} can send a new gate through to {star.Name} on its next jump, from its own stock.");
        }
        craft.GateLinkId = CraftId.None;
    }

    // --- Ammunition ---

    /// <summary>Armed ships docked at a station take on shells once a second, until every gun's magazine is full.</summary>
    private void StepMagazines()
    {
        if (Tick % FixedStepClock.TicksPerSecond != 0) return;
        foreach (var craft in _crafts)
        {
            if (!craft.IsDocked || craft.Stats.WeaponList.Count == 0 || !_craftsById.TryGetValue(craft.DockedToCraftId, out var station)) continue;
            long wanted = 0;
            foreach (var mount in craft.Stats.WeaponList)
                if (mount.Weapon is { AmmoKgPerShot: > 0, AmmoMaterial: ShellMaterial }) wanted += MagazineKg;
            var kg = Math.Min(Math.Min(wanted - craft.Inventory[ShellMaterial], craft.FreeCargoKg), station.Inventory[ShellMaterial]);
            if (kg > 0) TryTransfer(station.Id, craft.Id, ShellMaterial, kg);
        }
    }

    /// <summary>Shells an armed ship keeps aboard per gun.</summary>
    public const long MagazineKg = 400;
}
