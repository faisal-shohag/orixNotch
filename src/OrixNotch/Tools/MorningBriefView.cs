using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>The morning brief: one card per item (weather, next event, to-dos, AI usage, a stock). Shown by NotchWindow.ShowBrief.</summary>
public sealed class MorningBriefView : UserControl
{
    private readonly UniformGrid _cards = new() { Rows = 1, Margin = new Thickness(-5, 4, -5, 0) };

    public MorningBriefView() => Content = _cards;

    public void Show(IReadOnlyList<IdleItem> items, Func<IdleItem, FrameworkElement> glyph)
    {
        _cards.Children.Clear();
        _cards.Columns = Math.Max(1, items.Count);
        foreach (var item in items) _cards.Children.Add(Card(glyph(item), Caption(item.Id), item.Value, item.ValueBrush));
    }

    /// <summary>One card: a small icon and caption over a value (also the end-of-day wrap's cards).</summary>
    internal static Border Card(FrameworkElement icon, string caption, string value, string valueBrush = "TextBrush", string? sub = null)
    {
        var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        icon.Margin = new Thickness(0, 0, 6, 0);
        head.Children.Add(icon);
        var captionText = new TextBlock { Text = caption, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        captionText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        head.Children.Add(captionText);
        stack.Children.Add(head);
        var valueText = new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, valueBrush);
        stack.Children.Add(valueText);
        if (sub is not null)
        {
            var subText = new TextBlock { Text = sub, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            subText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            stack.Children.Add(subText);
        }
        var card = new Border { Child = stack, CornerRadius = Radii.L, Margin = new Thickness(5, 0, 5, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        return card;
    }

    private static string Caption(string id) => id switch
    {
        "nextEvent" => "Next",
        "todos" => "To-dos",
        "stock" => "Markets",
        _ when id.StartsWith("aiUsage") => "AI usage",
        _ => IdleInfoService.Catalog.FirstOrDefault(c => c.Id == id).Name ?? id,
    };
}
