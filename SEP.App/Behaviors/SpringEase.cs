using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace SEP.App.Behaviors;

/// <summary>
/// The spring-like ease-out of SEP's motion (the sidebar selection, segmented thumbs, switches, the page settle): a
/// damped spring that arrives quickly and settles with a barely visible overshoot (about 1 %), the feel of Apple's
/// default spring. Leave <see cref="EasingFunctionBase.EasingMode"/> at its default (EaseOut): the curve below is
/// written so that the base class's EaseOut mapping yields the spring itself.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    // damping ratio and natural frequency: settles by t = 1 with a ~1 % overshoot around t = 0.7
    private const double Zeta = 0.8;
    private const double Omega = 7.5;
    private static readonly double DampedOmega = Omega * Math.Sqrt(1 - Zeta * Zeta);
    private static readonly double EndValue = Spring(1);

    /// <summary>A shared, frozen instance for animations built in code.</summary>
    public static SpringEase Default { get; } = CreateDefault();

    private static SpringEase CreateDefault()
    {
        var ease = new SpringEase();
        ease.Freeze();
        return ease;
    }

    // underdamped step response, normalised to land exactly on 1
    private static double Spring(double t)
    {
        double decay = Math.Exp(-Zeta * Omega * t);
        return 1 - decay * (Math.Cos(DampedOmega * t) + Zeta * Omega / DampedOmega * Math.Sin(DampedOmega * t));
    }

    // EasingFunctionBase.Ease applies EaseOut as 1 - EaseInCore(1 - t); returning 1 - s(1 - t) makes that s(t).
    protected override double EaseInCore(double normalizedTime) => 1 - Spring(1 - normalizedTime) / EndValue;

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
