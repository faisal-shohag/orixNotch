using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace OrixNotch.Shell;

/// <summary>
/// Attached options for the text field templates in Theme.xaml (TextBox and PasswordBox):
/// <list type="bullet">
/// <item><c>Icon</c>: leading icon inside the field (search, location…).</item>
/// <item><c>Clearable</c>: shows an × that clears the text while the field is non-empty.</item>
/// <item><c>HasError</c>: red border; clears itself as soon as the user edits the field.</item>
/// <item><c>Chrome</c>: false for inline fields that sit inside their own row (no fill, border or glow).</item>
/// <item><c>IsEmpty</c>: maintained for PasswordBox (which has no bindable Password) so its template can show the Tag hint.</item>
/// </list>
/// </summary>
public static class Field
{
    public static readonly RoutedCommand ClearCommand = new(nameof(ClearCommand), typeof(Field));

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(AppIcon?), typeof(Field), new PropertyMetadata(null));

    public static readonly DependencyProperty ClearableProperty = DependencyProperty.RegisterAttached(
        "Clearable", typeof(bool), typeof(Field), new PropertyMetadata(false));

    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(Field), new PropertyMetadata(false));

    public static readonly DependencyProperty ChromeProperty = DependencyProperty.RegisterAttached(
        "Chrome", typeof(bool), typeof(Field), new PropertyMetadata(true));

    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.RegisterAttached(
        "IsEmpty", typeof(bool), typeof(Field), new PropertyMetadata(true));

    public static AppIcon? GetIcon(DependencyObject d) => (AppIcon?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, AppIcon? value) => d.SetValue(IconProperty, value);
    public static bool GetClearable(DependencyObject d) => (bool)d.GetValue(ClearableProperty);
    public static void SetClearable(DependencyObject d, bool value) => d.SetValue(ClearableProperty, value);
    public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject d, bool value) => d.SetValue(HasErrorProperty, value);
    public static bool GetChrome(DependencyObject d) => (bool)d.GetValue(ChromeProperty);
    public static void SetChrome(DependencyObject d, bool value) => d.SetValue(ChromeProperty, value);
    public static bool GetIsEmpty(DependencyObject d) => (bool)d.GetValue(IsEmptyProperty);
    public static void SetIsEmpty(DependencyObject d, bool value) => d.SetValue(IsEmptyProperty, value);

    /// <summary>TemplateBinding helper: a nullable enum can't feed <see cref="HeroIcon.Kind"/>.</summary>
    public static readonly IValueConverter IconKind = new IconKindConverter();

    private sealed class IconKindConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is AppIcon kind ? kind : AppIcon.Magnifier;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>Flags the field as invalid until the user next edits it.</summary>
    public static void ShowError(Control field)
    {
        SetHasError(field, true);
        field.Focus();
    }

    static Field()
    {
        CommandManager.RegisterClassCommandBinding(typeof(TextBox), new CommandBinding(ClearCommand, (s, e) =>
        {
            var tb = (TextBox)s;
            tb.Clear();
            tb.Focus();
            e.Handled = true;
        }));
        EventManager.RegisterClassHandler(typeof(TextBox), TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((s, _) => ClearError((DependencyObject)s)));
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler((s, _) =>
            {
                var box = (PasswordBox)s;
                SetIsEmpty(box, box.Password.Length == 0);
                ClearError(box);
            }));
    }

    private static void ClearError(DependencyObject d)
    {
        if (GetHasError(d)) d.ClearValue(HasErrorProperty);
    }

    /// <summary>Called from App startup so the class handlers above are registered before any field exists.</summary>
    public static void Register() { }
}
