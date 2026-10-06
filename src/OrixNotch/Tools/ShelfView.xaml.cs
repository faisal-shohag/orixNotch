using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrixNotch.Services;

namespace OrixNotch.Tools;

public partial class ShelfView : UserControl
{
    private Point _dragStart;
    private static ShelfService Shelf => ShelfService.Instance;

    public ShelfView()
    {
        InitializeComponent();
        List.ItemsSource = Shelf.Items;
        Shelf.Items.CollectionChanged += (_, _) => UpdateState();
        UpdateState();
    }

    private void UpdateState()
    {
        EmptyState.Visibility = Shelf.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DropOutline.Visibility = EmptyState.Visibility;
        CountText.Text = Shelf.Items.Count == 0 ? "" : $"{Shelf.Items.Count} item{(Shelf.Items.Count == 1 ? "" : "s")}";
    }

    private static ShelfItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        if (e.ClickCount == 2 && ItemOf(sender) is { } item)
        {
            Open(item.Path);
            e.Handled = true;
        }
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ItemOf(sender) is not { } item) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!item.Exists) return;

        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

        // A move by the drop target removes the source file; drop it from the shelf too.
        if (!item.Exists) Shelf.Remove(item);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Open(item.Path);
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        try
        {
            var list = new StringCollection { item.Path };
            Clipboard.SetFileDropList(list);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Shelf.Remove(item);
    }

    private void OnClear(object sender, RoutedEventArgs e) => Shelf.Clear();

    private static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
