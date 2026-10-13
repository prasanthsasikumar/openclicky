//
//  Motion.cs
//  OpenClicky (Windows)
//
//  SwiftUI's implicit animations, by hand, for views updated every frame from
//  CompositionTarget.Rendering: a spring with SwiftUI's (response, dampingFraction) parameters,
//  and a tween with the easeIn / easeOut / easeInOut / linear curves. Values move toward their
//  target; setting a new target mid-flight continues from where the value is, as SwiftUI does.
//

using System.Diagnostics;

namespace OpenClicky.Windows.Overlay;

/// <summary>Seconds since the app started, for frame-driven animation.</summary>
internal static class MotionClock
{
    private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();
    public static double Now => Stopwatch.Elapsed.TotalSeconds;
}

/// <summary>A damped spring (mass 1): stiffness (2π / response)², damping 4π · dampingFraction / response.</summary>
internal sealed class Spring
{
    private readonly double stiffness;
    private readonly double damping;
    private double velocity;

    public double Value { get; private set; }
    public double Target { get; set; }

    public Spring(double response, double dampingFraction, double initialValue = 0)
    {
        stiffness = Math.Pow(2 * Math.PI / response, 2);
        damping = 4 * Math.PI * dampingFraction / response;
        Value = initialValue;
        Target = initialValue;
    }

    public bool IsSettled => Math.Abs(Value - Target) < 0.01 && Math.Abs(velocity) < 0.01;

    public void Snap(double value)
    {
        Value = value;
        Target = value;
        velocity = 0;
    }

    public double Step(double deltaSeconds)
    {
        // Small fixed substeps keep the integration stable on a slow frame.
        var remaining = Math.Min(deltaSeconds, 0.1);
        const double substep = 1.0 / 240.0;
        while (remaining > 0)
        {
            var step = Math.Min(substep, remaining);
            var acceleration = -stiffness * (Value - Target) - damping * velocity;
            velocity += acceleration * step;
            Value += velocity * step;
            remaining -= step;
        }
        return Value;
    }
}

/// <summary>Two springs for a point.</summary>
internal sealed class Spring2D
{
    private readonly Spring horizontal;
    private readonly Spring vertical;

    public Spring2D(double response, double dampingFraction)
    {
        horizontal = new Spring(response, dampingFraction);
        vertical = new Spring(response, dampingFraction);
    }

    public double X => horizontal.Value;
    public double Y => vertical.Value;

    public void SetTarget(double x, double y)
    {
        horizontal.Target = x;
        vertical.Target = y;
    }

    public void Snap(double x, double y)
    {
        horizontal.Snap(x);
        vertical.Snap(y);
    }

    public void Step(double deltaSeconds)
    {
        horizontal.Step(deltaSeconds);
        vertical.Step(deltaSeconds);
    }
}

internal enum Easing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// <summary>A value that eases from where it is to a target over a duration.</summary>
internal sealed class Tween
{
    private double startValue;
    private double startTime;
    private double duration;
    private Easing easing;

    public double Target { get; private set; }

    public Tween(double initialValue)
    {
        startValue = initialValue;
        Target = initialValue;
    }

    /// <summary>Moves toward <paramref name="target"/>; a zero duration jumps there.</summary>
    public void AnimateTo(double target, double durationSeconds, Easing curve = Easing.EaseInOut)
    {
        if (target == Target && durationSeconds > 0) return;
        startValue = Value;
        Target = target;
        startTime = MotionClock.Now;
        duration = durationSeconds;
        easing = curve;
    }

    public void Snap(double value)
    {
        startValue = value;
        Target = value;
        duration = 0;
    }

    public bool IsAnimating => duration > 0 && MotionClock.Now - startTime < duration;

    public double Value
    {
        get
        {
            if (duration <= 0) return Target;
            var progress = Math.Clamp((MotionClock.Now - startTime) / duration, 0, 1);
            return startValue + (Target - startValue) * Ease(progress, easing);
        }
    }

    /// <summary>SwiftUI's curves are cubic Béziers; these polynomial forms are visually indistinguishable at these lengths.</summary>
    public static double Ease(double progress, Easing curve) => curve switch
    {
        Easing.Linear => progress,
        Easing.EaseIn => progress * progress * progress,
        Easing.EaseOut => 1 - Math.Pow(1 - progress, 3),
        _ => progress < 0.5 ? 4 * progress * progress * progress : 1 - Math.Pow(-2 * progress + 2, 3) / 2,
    };
}
