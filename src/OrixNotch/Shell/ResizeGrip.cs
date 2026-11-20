using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>
/// The drag handle under a resizable tool (Ask AI, Notifications, Phone, Note, Teleprompter): dragging sets the tool's
/// height and saves it, double-click goes back to the default.
/// </summary>
public sealed class ResizeGrip
{
    private readonly FrameworkElement _view;
    private readonly FrameworkElement _handle;
    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min, _max, _default, _width;
    private bool _dragging;
    private double _startY, _startHeight;

    /// <summary>Raised when a drag ends (the height is saved by then).</summary>
    public event Action? Released;

    public ResizeGrip(FrameworkElement view, FrameworkElement handle, Func<double> get, Action<double> set,
        double min, double max, double @default, double width = ToolRegistry.W)
    {
        (_view, _handle, _get, _set, _min, _max, _default, _width) = (view, handle, get, set, min, max, @default, width);
        handle.MouseLeftButtonDown += OnDown;
        handle.MouseMove += OnMove;
        handle.MouseLeftButtonUp += (_, _) => End();
        handle.LostMouseCapture += (_, _) => End();
    }

    /// <summary>Sizes the notch to the saved height (call when the tool is shown).</summary>
    public void Apply() => NotchWindow.Instance?.SetToolSize(_width, Math.Clamp(_get(), _min, _max));

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _set(_default);
            Apply();
            SettingsService.Save(notify: false);
            return;
        }
        _dragging = true;
        _startY = _view.PointToScreen(e.GetPosition(_view)).Y;
        _startHeight = _view.ActualHeight; // what's shown, which may be capped to fit the screen
        _handle.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var dy = (_view.PointToScreen(e.GetPosition(_view)).Y - _startY) / VisualTreeHelper.GetDpi(_view).DpiScaleY;
        var want = Math.Clamp(_startHeight + dy, _min, _max);
        _set(NotchWindow.Instance?.SetToolSize(_width, want) ?? want);
    }

    private void End()
    {
        if (!_dragging) return;
        _dragging = false;
        _handle.ReleaseMouseCapture();
        SettingsService.Save(notify: false);
        Released?.Invoke();
    }
}
