using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrixNotch.Shell;

/// <summary>
/// One set of animation timings and curves for the whole app, so motion feels like one thing. Everything turns
/// instant when Windows' "Animation effects" is off (Settings › Accessibility › Visual effects).
/// </summary>
public static class Motion
{
    /// <summary>Hovers, presses, small fades.</summary>
    public static TimeSpan Fast => Scale(150);

    /// <summary>Most transitions: a tool sliding in, a card appearing.</summary>
    public static TimeSpan Base => Scale(220);

    /// <summary>Larger moves and staggered entrances.</summary>
    public static TimeSpan Slow => Scale(320);

    /// <summary>The user turned animations off in Windows.</summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    public static TimeSpan Scale(double ms) => TimeSpan.FromMilliseconds(Reduced ? 1 : ms);

    /// <summary>The default curve: quick start, gentle landing.</summary>
    public static IEasingFunction Out { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>A small overshoot for things that "pop" into place (icons, badges).</summary>
    public static IEasingFunction Pop { get; } = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    /// <summary>An entrance: fades in while rising 8 px, after <paramref name="delayMs"/> (for staggered cards).</summary>
    public static void Rise(UIElement element, int delayMs)
    {
        var begin = Scale(delayMs);
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Slow) { BeginTime = begin, EasingFunction = Out });
        var shift = new TranslateTransform(0, 8);
        element.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, Slow) { BeginTime = begin, EasingFunction = Out });
    }

    /// <summary>Quadratic ease in-out for 0–1 progress driven by hand (the corner turns).</summary>
    public static double EaseInOut(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
