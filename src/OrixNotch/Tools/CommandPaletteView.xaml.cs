using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>One box for every tool: suggestions from <see cref="CommandRegistry"/>; ↑/↓ pick, Enter runs, Esc clears then closes.</summary>
public partial class CommandPaletteView : UserControl, IToolView, IEscapeHandler
{
    private List<Suggestion> _items = [];
    private int _selected;

    private bool _focusWanted; // the notch shows its content after the open animation starts, so focus waits for that

    public CommandPaletteView()
    {
        InitializeComponent();
        Refresh();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && _focusWanted) Dispatcher.BeginInvoke(FocusInput, System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    private void FocusInput()
    {
        if (!_focusWanted || !IsVisible) return;
        if (Keyboard.Focus(Input) == Input) _focusWanted = false;
    }

    public void OnShown()
    {
        Input.Clear();
        Refresh();
        _focusWanted = true;
        if (IsVisible) Dispatcher.BeginInvoke(FocusInput, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (!ConverterView.HasRates) _ = LoadRatesAsync();
    }

    public void OnHidden() => _focusWanted = false;

    public bool OnEscape()
    {
        if (Input.Text.Length == 0) return false;
        Input.Clear();
        return true;
    }

    private async Task LoadRatesAsync()
    {
        if (await ConverterView.FetchRatesAsync()) Refresh(); // a conversion typed meanwhile gets its answer
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _selected = 0;
        Refresh();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Select(_selected + 1);
                break;
            case Key.Up:
                Select(_selected - 1);
                break;
            case Key.Enter:
            case Key.Tab when _items.ElementAtOrDefault(_selected)?.Complete is not null:
                Choose(_selected);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Select(int index)
    {
        if (_items.Count == 0) return;
        _selected = (index + _items.Count) % _items.Count;
        Render();
    }

    private void Choose(int index)
    {
        if (_items.ElementAtOrDefault(index) is not { } item) return;
        if (item.Complete is { } fill)
        {
            Input.Text = fill;
            Input.CaretIndex = fill.Length;
            Input.Focus();
            return;
        }
        try
        {
            item.Run?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        if (NotchWindow.Instance?.View<CommandPaletteView>(NotchWindow.PaletteId) == this) Input.Clear();
    }

    private void Refresh()
    {
        _items = CommandRegistry.Suggest(Input.Text);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _items.Count - 1));
        Render();
    }

    private void Render()
    {
        List.Children.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var index = i;
            var row = new Grid { Height = 26 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new HeroIcon { Kind = item.Icon, Width = 13, Height = 13, Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            icon.SetResourceReference(ForegroundProperty, i == _selected ? "AccentBrush" : "SubTextBrush");
            var title = new TextBlock { Text = item.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var hint = new TextBlock { Text = item.Hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            Grid.SetColumn(title, 1);
            Grid.SetColumn(hint, 2);
            row.Children.Add(icon);
            row.Children.Add(title);
            row.Children.Add(hint);
            var border = new Border { Child = row, CornerRadius = Radii.S, Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
            if (i == _selected) border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            border.MouseLeftButtonUp += (_, _) => Choose(index);
            List.Children.Add(border);
        }
    }
}
