using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Double-precision 2D vector for simulation space (metres). Kept separate from
/// Godot's float Vector2 so the simulation compiles and runs without Godot.
/// </summary>
public readonly record struct Vector2D(double X, double Y)
{
    public static readonly Vector2D Zero = new(0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2D operator *(Vector2D a, double s) => new(a.X * s, a.Y * s);

    public double DistanceTo(Vector2D other) => (other - this).Length;
}
