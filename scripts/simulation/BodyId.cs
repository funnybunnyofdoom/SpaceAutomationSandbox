namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Stable identifier of a celestial body. Saves and cross-references use this value,
/// never a scene-tree path or list index. Value 0 is reserved for "none".
/// </summary>
public readonly record struct BodyId(uint Value)
{
    public static readonly BodyId None = new(0);

    public bool IsNone => Value == 0;

    public override string ToString() => $"body:{Value}";
}
