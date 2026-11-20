using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Grid of one-click actions (apps, folders, URLs, Windows settings pages).</summary>
public partial class ShortcutsView : UserControl, IEscapeHandler
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ShortcutItem? _editing;
    private static ShortcutsService Service => ShortcutsService.Instance;

    public ShortcutsView()
    {
        InitializeComponent();
        Service.Items.CollectionChanged += (_, _) => Render();
        Render();
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Render();

    /// <summary>The tiles: every shortcut matching the search, then a "New" tile.</summary>
    private void Render()
    {
        Tiles.Children.Clear();
        foreach (var item in Service.Items.Where(Matches)) Tiles.Children.Add(Tile(item));
        if (Search.Text.Trim().Length == 0) Tiles.Children.Add(NewTile());
    }

    private Button Tile(ShortcutItem item)
    {
        var button = TileButton(ShortcutIcon(item), item.Name);
        button.ToolTip = string.IsNullOrEmpty(item.Arguments) ? item.Target : $"{item.Target} {item.Arguments}";
        button.Click += (_, _) => Run(item);
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit" };
        edit.Click += (_, _) => OpenEditor(item);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => Service.Remove(item);
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        button.ContextMenu = menu;
        return button;
    }

    private Button NewTile()
    {
        var plus = new HeroIcon { Kind = AppIcon.Plus, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plus.SetResourceReference(ForegroundProperty, "SubTextBrush");
        var ring = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Child = plus };
        ring.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        var button = TileButton(ring, "New");
        button.ToolTip = "New shortcut: an app, file, folder, web page or Windows settings page";
        button.Click += (_, _) => OpenEditor(null);
        return button;
    }

    /// <summary>Icon over name, on the app's hover/press row style.</summary>
    private Button TileButton(FrameworkElement icon, string name)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var label = new TextBlock
        {
            Text = name, FontSize = 11.5, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 6, 0, 0),
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 86,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        stack.Children.Add(icon);
        stack.Children.Add(label);
        return new Button
        {
            Style = (Style)FindResource("RowButton"), Content = stack, Margin = new Thickness(4, 0, 4, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
    }

    // ---------------------------------------------------------------- icons

    private static readonly Dictionary<string, ImageSource?> FileIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a shortcut shows: the program's own icon when it's an app or file (System32 apps too, by name), else a
    /// symbol for what it opens on a coloured tile, like Settings' sidebar.
    /// </summary>
    private static FrameworkElement ShortcutIcon(ShortcutItem item)
    {
        var target = item.Target.Trim();
        var lower = target.ToLowerInvariant();
        if (!lower.Contains(':') || lower.Length > 2 && lower[1] == ':' && !System.IO.Directory.Exists(target))
        {
            if (FileIconFor(target) is { } image && !lower.Contains("rundll32"))
            {
                var picture = new Image { Source = image, Width = 34, Height = 34, Margin = new Thickness(4) };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                return picture;
            }
        }
        var (glyph, color) = lower switch
        {
            _ when item.Arguments.Contains("LockWorkStation", StringComparison.OrdinalIgnoreCase) => (AppIcon.User, "#636366"),
            _ when lower.StartsWith("ms-screenclip") => (AppIcon.Photo, "#FF9F0A"),
            _ when lower.Contains("bluetooth") => (AppIcon.Bluetooth, "#0A84FF"),
            _ when lower.Contains("wifi") || lower.Contains("network") => (AppIcon.Wifi, "#0A84FF"),
            _ when lower.Contains("sound") || lower.Contains("audio") => (AppIcon.Speaker, "#FF375F"),
            _ when lower.Contains("display") => (AppIcon.Monitor, "#5E5CE6"),
            _ when lower.Contains("quiethours") || lower.Contains("focus") => (AppIcon.Moon, "#5856D6"),
            _ when lower.Contains("battery") || lower.Contains("power") => (AppIcon.BatteryFull, "#30D158"),
            _ when lower.StartsWith("ms-settings") => (AppIcon.Gear, "#8E8E93"),
            _ when lower.StartsWith("http") => (AppIcon.Globe, "#64D2FF"),
            _ when System.IO.Directory.Exists(target) => (AppIcon.Folder, "#0A84FF"),
            _ => (AppIcon.Bolt, "#FF9F0A"),
        };
        var c = (Color)ColorConverter.ConvertFromString(color);
        var lighter = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.12), (byte)(c.G + (255 - c.G) * 0.12), (byte)(c.B + (255 - c.B) * 0.12));
        return new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(12), Background = new LinearGradientBrush(lighter, c, 90),
            Child = new HeroIcon { Kind = glyph, Width = 19, Height = 19, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    /// <summary>The icon of a file, or of an app named on its own ("calc.exe" → System32\calc.exe).</summary>
    private static ImageSource? FileIconFor(string target)
    {
        if (FileIcons.TryGetValue(target, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var path = System.IO.File.Exists(target) ? target
                : System.IO.Path.Combine(Environment.SystemDirectory, target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : target + ".exe");
            if (System.IO.File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or UnauthorizedAccessException)
        {
            // No icon: the symbol tile instead.
        }
        return FileIcons[target] = image;
    }

    private void Run(ShortcutItem item)
    {
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

    private void OpenEditor(ShortcutItem? item)
    {
        _editing = item;
        NameInput.Text = item?.Name ?? "";
        TargetInput.Text = item is null ? "" : string.IsNullOrEmpty(item.Arguments) ? item.Target : $"\"{item.Target}\" {item.Arguments}";
        EditorPanel.Visibility = Visibility.Visible;
        NameInput.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => EditorPanel.Visibility = Visibility.Collapsed;

    /// <summary>Esc closes the editor first; only a second Esc leaves the tool.</summary>
    public bool OnEscape()
    {
        if (EditorPanel.Visibility != Visibility.Visible) return false;
        EditorPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnSave(sender, e);
    }

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
        if (name.Length == 0 || target.Length == 0)
        {
            // Point at the first missing field instead of silently doing nothing.
            Field.ShowError(name.Length == 0 ? NameInput : TargetInput);
            return;
        }

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
            Render();
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
