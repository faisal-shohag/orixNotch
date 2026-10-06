namespace OrixNotch.Shell;

/// <summary>Damped spring used for the notch's "liquid" open/close motion.</summary>
public sealed class Spring
{
    public double Value;
    public double Target;
    public double Velocity;

    /// <summary>Higher = snappier.</summary>
    public double Stiffness = 420;

    /// <summary>Below 2*sqrt(Stiffness) the spring overshoots slightly, which gives the bounce.</summary>
    public double Damping = 32;

    public Spring(double value)
    {
        Value = value;
        Target = value;
    }

    /// <returns>True while still moving.</returns>
    public bool Step(double dt)
    {
        var force = -Stiffness * (Value - Target) - Damping * Velocity;
        Velocity += force * dt;
        Value += Velocity * dt;
        if (Math.Abs(Value - Target) < 0.15 && Math.Abs(Velocity) < 0.5)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }
}
