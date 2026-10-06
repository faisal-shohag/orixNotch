using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Grid of one-click actions (apps, folders, URLs, Windows settings pages).</summary>
public partial class ShortcutsView : UserControl
{
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ShortcutItem? _editing;
    private static ShortcutsService Service => ShortcutsService.Instance;

    public ShortcutsView()
    {
        InitializeComponent();
        _view = new ListCollectionView(Service.Items) { Filter = o => Matches((ShortcutItem)o) };
        List.ItemsSource = _view;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            Status.Text = "";
        };
    }

    private bool Matches(ShortcutItem item)
    {
        var q = Search.Text.Trim();
        return q.Length == 0 || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutItem item) return;
        if (ShortcutsService.Run(item, out var error)) Flash($"Ran {item.Name}");
        else Flash(error ?? "Could not run", bad: true);
    }

    private void Flash(string text, bool bad = false)
    {
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, bad ? "BadBrush" : "GoodBrush");
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void OnNew(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShortcutItem item) OpenEditor(item);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShortcutItem item) Service.Remove(item);
    }

    private void OpenEditor(ShortcutItem? item)
    {
        _editing = item;
        NameInput.Text = item?.Name ?? "";
        TargetInput.Text = item is null ? "" : string.IsNullOrEmpty(item.Arguments) ? item.Target : $"\"{item.Target}\" {item.Arguments}";
        EditorPanel.Visibility = Visibility.Visible;
        NameInput.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => EditorPanel.Visibility = Visibility.Collapsed;

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose an app or file", Filter = "Apps and files|*.*" };
        bool? picked;
        using (NotchWindow.Instance?.HoldOpen())
            picked = NotchWindow.Instance is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked != true) return;
        TargetInput.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(NameInput.Text))
            NameInput.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        var (target, args) = SplitCommand(TargetInput.Text.Trim());
        if (name.Length == 0 || target.Length == 0) return;

        if (_editing is null)
        {
            Service.Add(new ShortcutItem { Name = name, Target = target, Arguments = args });
        }
        else
        {
            _editing.Name = name;
            _editing.Target = target;
            _editing.Arguments = args;
            Service.Save();
            _view.Refresh();
        }
        EditorPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Splits `"C:\path\app.exe" --flag` or `app.exe --flag` into target + arguments.</summary>
    private static (string Target, string Args) SplitCommand(string text)
    {
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0) return (text[1..end], text[(end + 1)..].Trim());
        }
        if (System.IO.File.Exists(text) || System.IO.Directory.Exists(text) || text.Contains("://") || text.EndsWith(':'))
            return (text, "");
        var space = text.IndexOf(' ');
        return space > 0 ? (text[..space], text[(space + 1)..].Trim()) : (text, "");
    }
}
