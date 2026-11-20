using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Subscriptions: what you pay for regularly, when each renews, and the monthly total.</summary>
public partial class SubscriptionsView : UserControl, IToolView
{
    private static SubscriptionService Service => SubscriptionService.Instance;
    private static readonly string LocalCurrency = RegionInfo.CurrentRegion.ISOCurrencySymbol;

    public SubscriptionsView()
    {
        InitializeComponent();
        PriceInput.Tag = LocalCurrency.Length > 0 ? LocalCurrency : "Price";
        PriceInput.ToolTip = $"Price per payment, in {LocalCurrency}";
        foreach (var cycle in SubscriptionService.Cycles) CycleBox.Items.Add(cycle);
        CycleBox.SelectedIndex = 0;
        Service.Changed += Render;
        Render();
    }

    public void OnShown() => Render(); // "in 3 days" moves with the clock

    public void OnHidden()
    {
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAdd(sender, e);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (name.Length == 0)
        {
            Field.ShowError(NameInput);
            return;
        }
        if (!NumberInput.TryParse(PriceInput.Text.Trim(), out decimal price) || price < 0)
        {
            Field.ShowError(PriceInput);
            return;
        }
        if (DateInput.SelectedDate is not { } next)
        {
            DateInput.Focus();
            DateInput.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); // open the calendar
            return;
        }
        Service.Add(new Subscription
        {
            Name = name,
            Price = price,
            Currency = LocalCurrency,
            Cycle = (string)CycleBox.SelectedItem,
            Next = next,
        });
        NameInput.Clear();
        PriceInput.Clear();
        DateInput.Clear();
        NameInput.Focus();
    }

    private void Render()
    {
        List.Children.Clear();
        var items = Service.Items.OrderBy(s => s.Next).ToList();
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var s in items) List.Children.Add(Row(s));

        Totals.Children.Clear();
        foreach (var (currency, total) in Service.MonthlyTotals())
        {
            var line = new TextBlock { Text = $"{currency} {total.ToString("#,0.##", CultureInfo.CurrentCulture)}".Trim(), FontSize = 20, FontWeight = FontWeights.SemiBold };
            Totals.Children.Add(line);
        }
        if (Totals.Children.Count == 0) Totals.Children.Add(new TextBlock { Text = "–", FontSize = 20, FontWeight = FontWeights.SemiBold });
        CountText.Text = items.Count == 1 ? "1 subscription" : $"{items.Count} subscriptions";
    }

    private Grid Row(Subscription s)
    {
        var row = new Grid { Margin = new Thickness(4, 2, 0, 2), Height = 30 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock { Text = s.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var price = new TextBlock { Text = $"{s.PriceLabel} / {Per(s.Cycle)}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
        price.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        Grid.SetColumn(price, 1);

        var days = (s.Next.Date - DateTime.Today).Days;
        var when = new TextBlock
        {
            Text = days == 0 ? "Renews today" : days == 1 ? "Tomorrow" : days < 7 ? $"In {days} days" : s.Next.ToString("MMM d", CultureInfo.CurrentCulture),
            FontSize = 12, FontWeight = days <= 2 ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = s.Next.ToString("D", CultureInfo.CurrentCulture),
        };
        when.SetResourceReference(TextBlock.ForegroundProperty, days <= 2 ? "AccentBrush" : "SubTextBrush");
        Grid.SetColumn(when, 2);

        var remove = new Button { Style = (Style)FindResource("IconButton"), Width = 24, Height = 24, ToolTip = "Remove", Content = new HeroIcon { Kind = AppIcon.XMark, Width = 12, Height = 12 } };
        remove.SetResourceReference(ForegroundProperty, "SubTextBrush");
        remove.Click += (_, _) => Service.Remove(s.Id);
        Grid.SetColumn(remove, 3);

        row.Children.Add(name);
        row.Children.Add(price);
        row.Children.Add(when);
        row.Children.Add(remove);
        return row;
    }

    private static string Per(string cycle) => cycle switch { "Weekly" => "week", "Yearly" => "year", _ => "month" };
}
