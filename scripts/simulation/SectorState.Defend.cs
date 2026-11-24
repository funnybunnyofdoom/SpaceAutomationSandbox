using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Fleet orders (#88): guard, escort and patrol jobs (<see cref="DefendJob"/>). Enemies are those weapons fire at (#84). Guards and
/// escorts engage within <see cref="DefendRangeM"/> of what they protect, patrols within <see cref="PatrolRangeM"/> of
/// themselves; they don't chase further. Badly damaged craft retreat to a shipyard by the usual servicing (#3).
/// </summary>
public sealed partial class SectorState
{
    public const double DefendRangeM = 4_000, PatrolRangeM = 8_000;

    /// <summary>How high above a planet a patrol passes, and how close counts as having been there.</summary>
    public const double PatrolAltitudeM = 4_000, PatrolArriveM = 2_000;

    public ActionResult StartJob(CraftId craftId, DefendJob job)
    {
        var craft = GetCraft(craftId);
        if (craft.IsWreck || craft.Stats.TotalThrustN <= 0) return new(false, $"{craft.Name} can't fly.");
        if (craft.Stats.WeaponList.Count == 0) return new(false, $"{craft.Name} has no weapons.");
        string what;
        if (job.Mode == DefendMode.Patrol)
        {
            if (!_byId.TryGetValue(job.StarId, out var star) || star.Kind != BodyKind.Star) return new(false, "Pick a star system to patrol.");
            if (PatrolStops(job.StarId).Count == 0) return new(false, $"The {star.Name} system has no planets to patrol.");
            what = $"patrolling the {star.Name} system";
        }
        else
        {
            if (!_craftsById.TryGetValue(job.PostId, out var post) || post.Id == craft.Id || post.IsWreck || !IsPlayers(post)) return new(false, "Pick another working craft of yours to protect.");
            if (job.Mode == DefendMode.Guard && Movable(post) && post.Stats.TotalThrustN > 0) return new(false, $"{post.Name} is a ship: escort it instead.");
            what = $"{(job.Mode == DefendMode.Guard ? "guarding" : "escorting")} {post.Name}";
        }
        job.Active = true;
        job.WaitReason = null;
        job.State = DefendState.OnStation;
        job.RearmAt = CraftId.None;
        craft.Job = job;
        return new(true, $"{craft.Name}: {what}.");
    }

    private List<CelestialBodyState> PatrolStops(BodyId star) =>
        _bodies.Where(b => b.Kind is BodyKind.Planet or BodyKind.GasGiant && b.Orbit?.Parent == star).ToList();

    private void WaitDefend(CraftState craft, DefendJob job, string reason)
    {
        job.State = DefendState.Waiting;
        job.WaitReason = reason;
        job.RetryAtTick = Tick + (long)(JobStationRetryS * FixedStepClock.TicksPerSecond);
        craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded);
    }

    /// <summary>The ammunition a craft has run out of, or null: a material one of its weapons fires and it can't fire once more.</summary>
    private static string? OutOfAmmo(CraftState craft)
    {
        foreach (var mount in craft.Stats.WeaponList)
            if (mount.Weapon.AmmoKgPerShot > 0 && craft.Inventory[mount.Weapon.AmmoMaterial!] < mount.Weapon.AmmoKgPerShot) return mount.Weapon.AmmoMaterial;
        return null;
    }

    /// <summary>The nearest of your stations in this system with that ammunition and a docking port.</summary>
    private CraftState? AmmoStation(CraftState craft, string ammo)
    {
        CraftState? best = null;
        var star = StarAt(craft.Position);
        foreach (var c in _crafts)
        {
            if (c.Id == craft.Id || c.IsWreck || !IsPlayers(c) || Movable(c) || c.Stats.DockingPorts.Count == 0 || c.Inventory[ammo] <= 0 || StarAt(c.Position) != star) continue;
            if (best is null || c.Position.DistanceTo(craft.Position) < best.Position.DistanceTo(craft.Position)) best = c;
        }
        return best;
    }

    /// <summary>The nearest enemy to a craft that is within range of a point.</summary>
    private CraftState? NearestEnemy(CraftState craft, Vector2D around, double rangeM)
    {
        CraftState? best = null;
        var bestDistance = double.MaxValue;
        foreach (var enemy in _raiders)
        {
            if (enemy.IsWreck || !CanBeShot(enemy) || enemy.Position.DistanceTo(around) > rangeM) continue;
            var d = enemy.Position.DistanceTo(craft.Position);
            if (d < bestDistance) { best = enemy; bestDistance = d; }
        }
        return best;
    }

    /// <summary>Closes to within reach of a craft's shortest weapon (point defense aside) and faces the target.</summary>
    private void Attack(CraftState craft, CraftState target)
    {
        var reach = double.MaxValue;
        foreach (var mount in craft.Stats.WeaponList) if (!mount.Weapon.PointDefense) reach = Math.Min(reach, mount.Weapon.RangeM);
        if (reach == double.MaxValue) reach = 400; // point defense only
        var standoff = 0.65 * reach;
        SetTargetCraft(craft.Id, target.Id);
        var to = target.Position - craft.Position;
        if (to.Length > 0.85 * reach)
        {
            var side = new Vector2D(-to.Y, to.X) * (1 / Math.Max(to.Length, 1e-9)) * ((craft.Id.Value % 5 - 2.0) * 50);
            craft.Controls = NavigateTo(craft, target.Position - to * (standoff / to.Length) + side, target.Velocity, BodyId.None, JobCruiseSpeedMps, 10, target.Id);
            if (!craft.Cruising) SetTargetCraft(craft.Id, target.Id);
        }
        else craft.Controls = new ShipControls(0, Autopilot.TurnToward(craft, to), true);
    }

    /// <summary>Gets a craft off the ground or out of a dock; true while that takes this tick.</summary>
    private bool TakeOff(CraftState craft)
    {
        if (craft.IsDocked) { TryUndock(craft.Id); craft.Controls = ShipControls.Idle; return true; }
        if (!craft.IsLanded) return false;
        if (craft.IsAnchored) TryRelease(craft.Id);
        var outward = craft.Position - GetPosition(craft.LandedOnBodyId);
        craft.Controls = Autopilot.ThrustAlong(craft, outward * (1 / Math.Max(outward.Length, 1e-9)));
        return true;
    }

    private void StepDefendJob(CraftState craft, DefendJob job)
    {
        if (job.State == DefendState.Waiting)
        {
            if (Tick < job.RetryAtTick) { craft.Controls = new ShipControls(0, 0, !craft.IsDocked && !craft.IsLanded); return; }
            job.State = DefendState.OnStation;
            job.WaitReason = null;
        }
        CraftState? post = null;
        if (job.Mode != DefendMode.Patrol && (!_craftsById.TryGetValue(job.PostId, out post) || post.IsWreck))
        {
            WaitDefend(craft, job, job.Mode == DefendMode.Guard ? "the station it guarded is gone" : "the ship it escorted is gone");
            return;
        }

        // Out of ammunition: fetch some, unless other weapons still have an enemy to fight.
        var around = post?.Position ?? craft.Position;
        var enemy = NearestEnemy(craft, around, job.Mode == DefendMode.Patrol ? PatrolRangeM : DefendRangeM);
        if (job.State == DefendState.Rearming || (OutOfAmmo(craft) is { } && enemy is null))
            if (Rearm(craft, job)) return;

        if (enemy is not null && StarAt(enemy.Position) == StarAt(craft.Position))
        {
            if (TakeOff(craft)) return;
            job.State = DefendState.Engaging;
            job.Doing = $"attacking {enemy.Name}";
            Attack(craft, enemy);
            return;
        }
        if (job.State == DefendState.Engaging) job.CompletedCycles++; // a fight over
        job.State = DefendState.OnStation;
        if (TakeOff(craft)) return;
        var dry = OutOfAmmo(craft) is { } ammo ? $" (no {ammo} at any station here)" : "";
        if (job.Mode == DefendMode.Patrol) { Patrol(craft, job, dry); return; }

        job.Doing = $"{(job.Mode == DefendMode.Guard ? "guarding" : "escorting")} {post!.Name}{dry}";
        SetTargetCraft(craft.Id, post.Id);
        // Its own place on a ring round the craft it protects; above the ground if that has landed.
        var angle = craft.Id.Value * 2.399963229728653 % (2 * Math.PI);
        if (post.IsLanded)
        {
            var up = post.Position - GetPosition(post.LandedOnBodyId);
            angle = Math.Atan2(up.Y, up.X) + (craft.Id.Value % 5 - 2.0) * 0.4;
        }
        var ring = post.CollisionRadiusM + craft.CollisionRadiusM + HoldDistanceM * 2;
        var spot = post.Position + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * ring;
        craft.Controls = NavigateTo(craft, spot, CraftVelocityAt(post, TimeS), post.IsLanded ? post.LandedOnBodyId : BodyId.None, JobCruiseSpeedMps, 0); // round the station it guards, not through it
        if (!craft.Cruising) SetTargetCraft(craft.Id, post.Id);
    }

    private void Patrol(CraftState craft, DefendJob job, string note)
    {
        var stops = PatrolStops(job.StarId);
        if (stops.Count == 0) { WaitDefend(craft, job, "the system has no planets left to patrol"); return; }
        var stop = stops[job.PatrolIndex % stops.Count];
        if (NearPatrolStop(craft, stop))
        {
            job.PatrolIndex = (job.PatrolIndex + 1) % stops.Count;
            stop = stops[job.PatrolIndex];
        }
        job.Doing = $"patrolling to {stop.Name}{note}";
        FlyToPatrolStop(craft, stop);
    }

    /// <summary>One tick of fetching ammunition; false when there's none to fetch or it's done.</summary>
    private bool Rearm(CraftState craft, DefendJob job)
    {
        var ammo = OutOfAmmo(craft);
        var station = _craftsById.GetValueOrDefault(job.RearmAt);
        if (job.State != DefendState.Rearming || station is null || station.IsWreck)
        {
            station = ammo is null ? null : AmmoStation(craft, ammo);
            if (station is null) { job.RearmAt = CraftId.None; job.State = DefendState.OnStation; return false; }
            job.RearmAt = station.Id;
            job.State = DefendState.Rearming;
        }
        // Docked, the magazines fill once a second (StepMagazines): leave when they have, or the station has no more.
        if (craft.DockedToCraftId == station.Id && (ammo is null || station.Inventory[ammo] <= 0 || craft.FreeCargoKg <= 0))
        {
            job.RearmAt = CraftId.None;
            job.State = DefendState.OnStation;
            return false;
        }
        if (ammo is null) { job.RearmAt = CraftId.None; job.State = DefendState.OnStation; return false; }
        job.Doing = $"fetching {ammo} from {station.Name}";
        switch (FlyToDock(craft, station))
        {
            case DockApproach.Docked: craft.Controls = ShipControls.Idle; break;
            case DockApproach.NoFreePort: craft.Controls = new ShipControls(0, 0, true); job.Doing = $"queued for a berth at {station.Name} (for {ammo})"; break;
        }
        return true;
    }
}
