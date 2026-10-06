using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Clipboard history as a horizontal strip of cards with type filters.</summary>
public partial class ClipboardView : UserControl, IToolView
{
    private static readonly (string Label, ClipKind? Kind)[] FilterDefs =
        [("All", null), ("Text", ClipKind.Text), ("Links", ClipKind.Link), ("Images", ClipKind.Image), ("Files", ClipKind.Files)];

    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private ClipKind? _filter;
    private static ClipboardService Service => ClipboardService.Instance;

    public ClipboardView()
    {
        InitializeComponent();
        _view = new ListCollectionView(Service.Items) { Filter = Matches };
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.Pinned), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.Time), ListSortDirection.Descending));
        List.ItemsSource = _view;

        foreach (var (label, kind) in FilterDefs)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChipButton"),
                Content = label,
                GroupName = "clipFilter",
                IsChecked = kind is null,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = 14,
            };
            chip.Checked += (_, _) =>
            {
                _filter = kind;
                _view.Refresh();
                UpdateEmpty();
            };
            Filters.Children.Add(chip);
        }

        Service.Items.CollectionChanged += (_, _) => UpdateEmpty();
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            Status.Text = "";
        };
        UpdateEmpty();
    }

    public void OnShown()
    {
        _view.Refresh(); // refresh relative timestamps
        Strip.ScrollToHorizontalOffset(0);
    }

    public void OnHidden()
    {
    }

    private bool Matches(object o)
    {
        var item = (ClipItem)o;
        if (_filter is { } kind && item.Kind != kind) return false;
        var q = Search.Text.Trim();
        if (q.Length == 0) return true;
        return item.Preview.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (item.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void UpdateEmpty() =>
        Empty.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateEmpty();
    }

    /// <summary>Mouse wheel scrolls the strip sideways.</summary>
    private void OnStripWheel(object sender, MouseWheelEventArgs e)
    {
        Strip.ScrollToHorizontalOffset(Strip.HorizontalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
        if ((sender as FrameworkElement)?.DataContext is not ClipItem item) return;
        Service.Recopy(item);
        Status.Text = "Copied";
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipItem item) return;
        Service.TogglePin(item);
        _view.Refresh();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) Service.Remove(item);
    }

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        MenuButton.ContextMenu.PlacementTarget = MenuButton;
        MenuButton.ContextMenu.IsOpen = true;
    }

    private void OnClear(object sender, RoutedEventArgs e) => Service.Clear();

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var d = child; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is T match) return match;
        return null;
    }
}
