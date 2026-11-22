using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Traffic rules for automated craft (#55), so they can collide like everything else (#34) without
/// running into each other:
/// <list type="bullet">
/// <item><b>Lanes:</b> on legs longer than <see cref="LaneMinDistanceM"/> a craft aims
/// <see cref="LaneOffsetM"/> to the right of the straight line, so traffic going out and coming back
/// on the same route passes well apart instead of head-on.</item>
/// <item><b>Giving way:</b> each automated craft looks at the craft near it (a spatial grid) and, if one
/// would pass closer than both hulls plus <see cref="TrafficMarginM"/> within the next few seconds,
/// steers away from all such threats at once (weighted by how soon each would hit). Right of way:
/// craft holding still (waiting jobs), climbing off a surface, or not run by jobs (the player's, idle
/// ones) keep their course; two moving automated craft both steer clear. Decided from saved state
/// only, so a loaded game continues exactly.</item>
/// <item><b>Berths and queues:</b> a craft claims a free port before it approaches (claims are saved),
/// so two never race for the same one. With no port free it waits on a holding ring
/// <see cref="HoldDistanceM"/> out from the hull, clear of the approach lanes ("queued for a berth").</item>
/// </list>
/// Cost stays small: the grid only links craft that can be close, and craft can't overlap, so even
/// thousands crowding one station have few neighbours each.
/// </summary>
public sealed partial class SectorState
{
    public const double LaneOffsetM = 60;
    public const double LaneMinDistanceM = 400;
    public const double TrafficMarginM = 15;
    public const double TrafficHorizonS = 3;
    public const double HoldDistanceM = 150;

    /// <summary>Craft claim a berth only once this close to the station's hull.</summary>
    public const double ClaimRangeM = 1_500;

    /// <summary>Craft moving faster than this relative to what they're flying towards also go in the coarse grid.</summary>
    public const double FastSpeedMps = 20;

    /// <summary>Longest look-ahead for traffic.</summary>
    public const double TrafficMaxHorizonS = 10;

    private const double CoarseCellM = 800;

    /// <summary>Most craft looked at per grid cell: bounds the work in a dense crowd (the nearest few matter).</summary>
    private const int MaxPerCell = 16;


    // Two grids: a fine one with every free-moving craft (neighbours within a couple of hundred metres),
    // and a coarse one with only the fast ones, so fast crossings are seen early without scanning crowds.
    private readonly Dictionary<long, int> _trafficHead = new();
    private int[] _trafficNext = Array.Empty<int>();
    private double _trafficCell = 64;
    private readonly Dictionary<long, int> _fastHead = new();
    private int[] _fastNext = Array.Empty<int>();
    private readonly Dictionary<(CraftId Station, int Port), CraftId> _portClaims = new();

    private static long GridKey(long x, long y) => (x << 32) ^ (y & 0xFFFFFFFF);

    /// <summary>Links every free-moving craft into a grid (once per tick, before jobs run), and gathers berth claims.</summary>
    private void BuildTrafficGrid()
    {
        double maxRadius = 0;
        foreach (var c in _crafts) if (Collides(c) && Movable(c)) maxRadius = Math.Max(maxRadius, c.CollisionRadiusM);
        _trafficCell = Math.Max(64, 2 * maxRadius + 2 * TrafficMarginM + 1);
        if (_trafficNext.Length < _crafts.Count) _trafficNext = new int[Math.Max(_crafts.Count, 2 * _trafficNext.Length)];
        if (_fastNext.Length < _crafts.Count) _fastNext = new int[_trafficNext.Length];
        _trafficHead.Clear();
        _fastHead.Clear();
        _portClaims.Clear();
        for (var i = 0; i < _crafts.Count; i++)
        {
            var c = _crafts[i];
            // A claim lapses once its craft is well away from the station (it left, or went elsewhere first).
            if (!c.DockClaimStation.IsNone && (!_craftsById.TryGetValue(c.DockClaimStation, out var claimed)
                                               || claimed.Position.DistanceTo(c.Position) - claimed.CollisionRadiusM > 2 * ClaimRangeM))
            {
                c.DockClaimStation = CraftId.None;
                c.DockClaimPort = 0;
            }
            if (!c.DockClaimStation.IsNone) _portClaims[(c.DockClaimStation, c.DockClaimPort)] = c.Id;
            if (!Collides(c) || !Movable(c)) continue;
            var key = GridKey((long)Math.Floor(c.Position.X / _trafficCell), (long)Math.Floor(c.Position.Y / _trafficCell));
            _trafficNext[i] = _trafficHead.TryGetValue(key, out var head) ? head : -1;
            _trafficHead[key] = i;
            if ((c.Velocity - ReferenceVelocity(c)).Length <= FastSpeedMps) continue;
            var fastKey = GridKey((long)Math.Floor(c.Position.X / CoarseCellM), (long)Math.Floor(c.Position.Y / CoarseCellM));
            _fastNext[i] = _fastHead.TryGetValue(fastKey, out var fastHead) ? fastHead : -1;
            _fastHead[fastKey] = i;
        }
    }

    /// <summary>
    /// The local frame's velocity: the body whose gravity the craft is in (else the sector). Not the
    /// craft's target: a ship matching a rock that orbits at 100 m/s is still fast to everyone else.
    /// </summary>
    private Vector2D ReferenceVelocity(CraftState craft)
    {
        var index = GravitySourceIndex(craft.Position, TimeS);
        return index >= 0 ? BodyVelocityAt(index, TimeS) : Vector2D.Zero;
    }

    /// <summary>A craft whose job is waiting (holding position).</summary>
    private static bool IsHolding(CraftState craft) =>
        craft.ServiceAt.IsNone // a craft going for repairs is moving, whatever its job's state (#3)
        && craft.Job is { Active: true } and (MiningJob { State: MiningJobState.Waiting } or HaulJob { State: HaulJobState.Waiting }
            or ServeRequestsJob { State: HaulJobState.Waiting } or SurveyJob { State: SurveyJobState.Waiting });

    /// <summary>True if <paramref name="other"/> keeps its course and <paramref name="craft"/> must give way to it.</summary>
    private static bool HasRightOfWay(CraftState other, CraftState craft)
    {
        if (!other.IsAutomated) return true; // the player's ships and idle craft
        if (IsHolding(other)) return true; // holding still
        if (other.Job is MiningJob { State: MiningJobState.Lifting }) return true; // climbing straight out: hard to steer
        return false; // two moving automated craft both steer clear of each other
    }

    /// <summary>
    /// Controls that steer an automated craft out of another craft's way, or null if nothing is in
    /// the way. <paramref name="ignore"/> is the station being docked at (its approach handles it).
    /// </summary>
    /// <param name="nearBody">The body the craft is working close to (its mining rock), if any: dodging never steers into it.</param>
    private ShipControls? GiveWay(CraftState craft, CraftId ignore, BodyId nearBody = default)
    {
        craft.GivingWayTo = CraftId.None;
        if (_trafficHead.Count == 0) return null;
        var accel = craft.Stats.TotalThrustN / craft.MassKg;
        CraftState? worst = null;
        var worstTime = double.PositiveInfinity;
        var away = Vector2D.Zero; // sum of 'away' directions, weighted by how soon each threat would hit

        void Check(CraftState other)
        {
            if (ReferenceEquals(other, craft) || other.Id == ignore || other.DockedToCraftId == craft.Id) return;
            // Everyone steers clear of everyone near it. Craft with right of way (HasRightOfWay: holding,
            // climbing off a surface, the player's) simply don't run this, so they keep their course.
            var offset = other.Position - craft.Position;
            var relative = other.Velocity - craft.Velocity;
            var combined = craft.CollisionRadiusM + other.CollisionRadiusM + TrafficMarginM;
            var speedSq = relative.X * relative.X + relative.Y * relative.Y;
            var speed = Math.Sqrt(speedSq);
            // Look further ahead the faster we close: time to turn half round and to cancel the closing speed.
            var horizon = Math.Min(TrafficMaxHorizonS, TrafficHorizonS + (accel > 0 ? speed / accel : 0) + 0.5 * Math.PI / FlightModel.TurnRateRadPerS);
            if (offset.Length > combined + speed * horizon) return;
            // A bump this gentle is harmless (#34): let the collision push sort it out rather than dithering.
            var closingSpeed = offset.Length > 1e-9 ? -(offset.X * relative.X + offset.Y * relative.Y) / offset.Length : 0;
            // (Craft that won't move are cheap to go round, so only the gentlest bumps with them are left to the push.)
            if (closingSpeed < (HasRightOfWay(other, craft) ? 2 : 0.5 * SafeBumpSpeedMps)) return;
            var t = speedSq > 1e-9 ? Math.Clamp(-(offset.X * relative.X + offset.Y * relative.Y) / speedSq, 0, horizon) : 0;
            var closest = offset + relative * t;
            if (closest.Length >= combined) return;
            if (t < worstTime) { worst = other; worstTime = t; }
            // Away from where it will be; if dead ahead, sideways to our right of its path.
            var direction = closest.Length > 1e-6 ? closest * (-1 / closest.Length)
                : new Vector2D(-relative.Y, relative.X) * (1 / Math.Max(speed, 1e-9));
            away += direction * (1 / (t + 0.5));
        }

        void Scan(Dictionary<long, int> head, int[] next, double cell)
        {
            var cx = (long)Math.Floor(craft.Position.X / cell);
            var cy = (long)Math.Floor(craft.Position.Y / cell);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!head.TryGetValue(GridKey(cx + dx, cy + dy), out var j)) continue;
                for (var n = 0; j >= 0 && n < MaxPerCell; j = next[j], n++) Check(_crafts[j]);
            }
        }

        Scan(_trafficHead, _trafficNext, _trafficCell);
        if (_fastHead.Count > 0) Scan(_fastHead, _fastNext, CoarseCellM);
        if (worst is null) return null;
        // Threats on opposite sides can cancel out: then keep right (sideways to our own motion).
        away = away.Length > 1e-6 ? away * (1 / away.Length)
            : new Vector2D(-craft.Velocity.Y, craft.Velocity.X) * (1 / Math.Max(craft.Velocity.Length, 1e-9));
        // Close to the body it's working at (left out of the autopilot's body checks), never dodge downwards.
        if (!nearBody.IsNone && _byId.TryGetValue(nearBody, out var body))
        {
            var outward = craft.Position - GetPosition(body.Id);
            if (outward.Length - body.RadiusM < 500)
            {
                var up = outward * (1 / Math.Max(outward.Length, 1e-9));
                var down = away.X * up.X + away.Y * up.Y;
                if (down < 0) away -= up * down;
                away = away.Length > 1e-6 ? away * (1 / away.Length) : up;
            }
        }

        craft.GivingWayTo = worst.Id;
        var forward = craft.Forward;
        var facing = forward.X * away.X + forward.Y * away.Y;
        // Roughly facing away: thrust clear. Otherwise brake while turning, relative to the craft in the
        // way (stop closing on it), not the far-off target: near an orbiting station, braking against a
        // rock or a planet swerves the craft tens of m/s across the traffic there.
        if (facing > 0.3) return new ShipControls(1, Autopilot.TurnToward(craft, away), false);
        craft.BrakeAgainst = worst.Id;
        return new ShipControls(0, Autopilot.TurnToward(craft, away), true);
    }

    /// <summary>The goal shifted into the right-hand lane for long legs (the real goal when close).</summary>
    private static Vector2D LaneGoal(CraftState craft, Vector2D goal)
    {
        var toGoal = goal - craft.Position;
        var distance = toGoal.Length;
        if (distance < LaneMinDistanceM) return goal;
        var right = new Vector2D(-toGoal.Y, toGoal.X) * (1 / distance);
        return goal + right * LaneOffsetM;
    }

    /// <summary>
    /// The port this craft may use at <paramref name="station"/>: its own claim if still free, else the
    /// first free port nobody else has claimed (claimed now). −1 if none.
    /// </summary>
    private int ClaimPort(CraftState craft, CraftState station, IReadOnlyList<int> freePorts)
    {
        if (craft.DockClaimStation == station.Id && Contains(freePorts, craft.DockClaimPort)) return craft.DockClaimPort;
        ReleaseClaim(craft);
        foreach (var port in freePorts)
        {
            if (_portClaims.TryGetValue((station.Id, port), out var holder) && holder != craft.Id
                && _craftsById.TryGetValue(holder, out var h) && h.IsAutomated && h.DockClaimStation == station.Id) continue;
            craft.DockClaimStation = station.Id;
            craft.DockClaimPort = port;
            _portClaims[(station.Id, port)] = craft.Id;
            return port;
        }
        return -1;

        static bool Contains(IReadOnlyList<int> list, int value)
        {
            foreach (var x in list) if (x == value) return true;
            return false;
        }
    }

    private void ReleaseClaim(CraftState craft)
    {
        if (craft.DockClaimStation.IsNone) return;
        if (_portClaims.TryGetValue((craft.DockClaimStation, craft.DockClaimPort), out var holder) && holder == craft.Id)
            _portClaims.Remove((craft.DockClaimStation, craft.DockClaimPort));
        craft.DockClaimStation = CraftId.None;
        craft.DockClaimPort = 0;
    }

    /// <summary>Where a craft waits for a berth: on a ring out from the hull, on its own bearing (clear of the approach lanes).</summary>
    private static Vector2D HoldPoint(CraftState craft, CraftState station)
    {
        var bearing = craft.Position - station.Position;
        var unit = bearing.Length > 1e-6 ? bearing * (1 / bearing.Length) : new Vector2D(1, 0);
        return station.Position + unit * (station.CollisionRadiusM + craft.CollisionRadiusM + HoldDistanceM);
    }

    /// <summary>Restores a saved berth claim.</summary>
    public void RestoreDockClaim(CraftId craftId, CraftId stationId, int port)
    {
        var craft = GetCraft(craftId);
        if (stationId.IsNone) { craft.DockClaimStation = CraftId.None; return; }
        var station = GetCraft(stationId);
        if (port < 0 || port >= station.Stats.DockingPorts.Count) throw new ArgumentOutOfRangeException(nameof(port));
        craft.DockClaimStation = stationId;
        craft.DockClaimPort = port;
    }
}
