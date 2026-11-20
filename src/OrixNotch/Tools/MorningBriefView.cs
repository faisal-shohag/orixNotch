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
        foreach (var item in items)
        {
            var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var icon = glyph(item);
            icon.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(icon);
            var caption = new TextBlock { Text = Caption(item.Id), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            head.Children.Add(caption);
            stack.Children.Add(head);
            var value = new TextBlock { Text = item.Value, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis };
            value.SetResourceReference(TextBlock.ForegroundProperty, item.ValueBrush);
            stack.Children.Add(value);
            var card = new Border { Child = stack, CornerRadius = new CornerRadius(12), Margin = new Thickness(5, 0, 5, 0) };
            card.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            _cards.Children.Add(card);
        }
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
