using System.Windows;

namespace OrixNotch.Shell;

/// <summary>The three corner radii (Themes/Theme.xaml has the same as RadiusS/M/L): chips, controls and rows, cards.</summary>
public static class Radii
{
    public static readonly CornerRadius S = new(6);
    public static readonly CornerRadius M = new(10);
    public static readonly CornerRadius L = new(14);
}
