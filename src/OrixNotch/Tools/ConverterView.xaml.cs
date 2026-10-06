using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OrixNotch.Services;

namespace OrixNotch.Tools;

/// <summary>A unit converts to the category's base unit via ToBase/FromBase.</summary>
public sealed record Unit(string Name, Func<double, double> ToBase, Func<double, double> FromBase)
{
    public static Unit Linear(string name, double factor) => new(name, v => v * factor, v => v / factor);

    // Shown in the ComboBox selection box.
    public override string ToString() => Name;
}

public partial class ConverterView : UserControl
{
    private static readonly Dictionary<string, Unit[]> Catalog = new()
    {
        ["Length"] =
        [
            Unit.Linear("Millimeters", 0.001), Unit.Linear("Centimeters", 0.01), Unit.Linear("Meters", 1),
            Unit.Linear("Kilometers", 1000), Unit.Linear("Inches", 0.0254), Unit.Linear("Feet", 0.3048),
            Unit.Linear("Yards", 0.9144), Unit.Linear("Miles", 1609.344), Unit.Linear("Nautical miles", 1852),
        ],
        ["Mass"] =
        [
            Unit.Linear("Milligrams", 1e-6), Unit.Linear("Grams", 0.001), Unit.Linear("Kilograms", 1),
            Unit.Linear("Tonnes", 1000), Unit.Linear("Ounces", 0.028349523125), Unit.Linear("Pounds", 0.45359237),
            Unit.Linear("Stone", 6.35029318),
        ],
        ["Temperature"] =
        [
            new("Celsius", v => v, v => v),
            new("Fahrenheit", v => (v - 32) * 5 / 9, v => v * 9 / 5 + 32),
            new("Kelvin", v => v - 273.15, v => v + 273.15),
        ],
        ["Volume"] =
        [
            Unit.Linear("Milliliters", 0.001), Unit.Linear("Liters", 1), Unit.Linear("Cubic meters", 1000),
            Unit.Linear("Teaspoons", 0.00492892), Unit.Linear("Tablespoons", 0.0147868),
            Unit.Linear("Fluid ounces", 0.0295735), Unit.Linear("Cups", 0.2365882), Unit.Linear("Pints", 0.473176),
            Unit.Linear("Quarts", 0.946353), Unit.Linear("Gallons", 3.785411784),
        ],
        ["Speed"] =
        [
            Unit.Linear("m/s", 1), Unit.Linear("km/h", 1 / 3.6), Unit.Linear("mph", 0.44704),
            Unit.Linear("Knots", 0.514444), Unit.Linear("ft/s", 0.3048),
        ],
    };

    private static readonly Dictionary<string, (int From, int To)> Defaults = new()
    {
        ["Length"] = (2, 3), // Meters -> Kilometers (matches design: 1 -> 0.001)
        ["Mass"] = (2, 5), // Kilograms -> Pounds
        ["Temperature"] = (0, 1), // Celsius -> Fahrenheit
        ["Volume"] = (1, 9), // Liters -> Gallons
        ["Speed"] = (1, 2), // km/h -> mph
    };

    private string _category = "Length";
    private bool _syncing;
    private string _lastOutput = "";

    public ConverterView()
    {
        InitializeComponent();

        var tabs = new[] { Cat0, Cat1, Cat2, Cat3, Cat4 };
        var names = Catalog.Keys.ToArray();
        for (var i = 0; i < tabs.Length; i++)
        {
            var name = names[i];
            tabs[i].Checked += (_, _) => SelectCategory(name);
        }
        Cat0.IsChecked = true;

        InputBox.TextChanged += (_, _) => Recalculate();
        FromBox.SelectionChanged += (_, _) => { if (!_syncing) Recalculate(); };
        ToBox.SelectionChanged += (_, _) => { if (!_syncing) Recalculate(); };

        // Ready to type as soon as the tool opens: focus the value, caret at the end.
        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        });

        SelectCategory(_category);
    }

    private void SelectCategory(string name)
    {
        _category = name;
        _syncing = true;
        try
        {
            FromBox.ItemsSource = Catalog[name];
            ToBox.ItemsSource = Catalog[name];
            var (from, to) = Defaults[name];
            FromBox.SelectedIndex = from;
            ToBox.SelectedIndex = to;
        }
        finally
        {
            _syncing = false;
        }
        Recalculate();
    }

    private void OnSwap(object sender, RoutedEventArgs e)
    {
        (FromBox.SelectedIndex, ToBox.SelectedIndex) = (ToBox.SelectedIndex, FromBox.SelectedIndex);
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastOutput))
            ClipboardService.Instance.CopyQuiet(_lastOutput);
    }

    private void Recalculate()
    {
        if (InputBox is null || FromBox?.SelectedItem is not Unit from || ToBox?.SelectedItem is not Unit to)
            return;
        var text = InputBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            OutputText.Text = "—";
            _lastOutput = "";
            return;
        }

        var result = to.FromBase(from.ToBase(value));
        var formatted = Format(result);
        OutputText.Text = formatted;
        _lastOutput = formatted;
    }

    private static string Format(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        if (v == 0) return "0";
        var abs = Math.Abs(v);
        if (abs >= 1e9 || abs < 1e-4) return v.ToString("0.###e+0", CultureInfo.CurrentCulture);
        return v.ToString(abs >= 100 ? "#,0.##" : "#,0.#####", CultureInfo.CurrentCulture);
    }
}
