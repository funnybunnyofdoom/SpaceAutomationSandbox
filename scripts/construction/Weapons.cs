using System;
using System.Text.Json.Serialization;

namespace SpaceAutomationSandbox.Construction;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WeaponKind
{
    /// <summary>Fires shots that fly and can miss; uses ammunition.</summary>
    Gun,
    /// <summary>Hits at once; power only, weaker with distance.</summary>
    Laser,
    /// <summary>Fires missiles that steer to their target; point defense can shoot them down (#87).</summary>
    Missile,
}

/// <summary>What a weapon part does (#85), from data/parts/parts.json.</summary>
public sealed record WeaponDefinition
{
    public WeaponKind Kind { get; init; }
    public double RangeM { get; init; }

    /// <summary>Part health (0..1 is a whole ordinary part) one shot takes.</summary>
    public double DamagePerShot { get; init; }

    public double IntervalS { get; init; }
    public double EnergyKJPerShot { get; init; }
    public string? AmmoMaterial { get; init; }
    public long AmmoKgPerShot { get; init; }

    /// <summary>Speed of a gun's shots, relative to the ship.</summary>
    public double ShotSpeedMps { get; init; }

    /// <summary>Full firing arc around the way the part faces; 360 is all round.</summary>
    public double ArcDeg { get; init; } = 360;

    /// <summary>Only fires with the Armed gates law (gate defense batteries, #36).</summary>
    public bool NeedsArmedGates { get; init; }

    /// <summary>Shoots down enemy missiles first, and only then fires at craft (#87).</summary>
    public bool PointDefense { get; init; }

    public void Validate(string partId, MaterialCatalog materials)
    {
        if (!(RangeM > 0) || !(DamagePerShot > 0) || !(IntervalS > 0) || !(EnergyKJPerShot >= 0) || !(ArcDeg > 0) || ArcDeg > 360 || AmmoKgPerShot < 0)
            throw new FormatException($"Weapon '{partId}' has an invalid stat.");
        if (Kind != WeaponKind.Laser && !(ShotSpeedMps > 0)) throw new FormatException($"Weapon '{partId}' needs shotSpeedMps.");
        if (PointDefense && Kind != WeaponKind.Laser) throw new FormatException($"Point defense '{partId}' must be a laser.");
        if (AmmoKgPerShot > 0 && (AmmoMaterial is null || !materials.Contains(AmmoMaterial)))
            throw new FormatException($"Weapon '{partId}' fires unknown material '{AmmoMaterial}'.");
    }
}

/// <summary>One weapon on a craft: where it sits, which way it faces, and how well it works.</summary>
public sealed record WeaponMount(GridPos Pos, int Rotation, WeaponDefinition Weapon, double Health);
