using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>
/// Dragging files, an image or text onto the notch (idle, or open on a tool that doesn't take drops)
/// opens it on its drop targets, Shelf, Phone and Ask AI, instead of guessing.
/// </summary>
public partial class NotchWindow
{
    private const double ChooserW = 520;
    private const double ChooserWithAskW = 690;
    private const double ChooserH = 150;

    private void ShowDropChooser()
    {
        if (DropChooser.Visibility == Visibility.Visible) return;
        if (!_expanded) Expand(activate: false);
        DropPhoneSub.Text = PhoneTransferService.Instance.IsRunning ? "Send to your phone" : "Waits until Phone is on";
        ResetChooserTile(DropShelfFrame);
        ResetChooserTile(DropPhoneFrame);
        ResetChooserTile(DropAskFrame);
        var ask = !App.Settings.HiddenTools.Contains("ask");
        DropAsk.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;
        DropAskGap.Width = new GridLength(ask ? 12 : 0);
        DropAskColumn.Width = ask ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ToolHost.Visibility = Visibility.Hidden;
        TabBar.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        TitleText.Text = "Drop to…";
        TitleLogo.Visibility = Visibility.Collapsed;
        DropChooser.Visibility = Visibility.Visible;
        SetToolSize(ask ? ChooserWithAskW : ChooserW, ChooserH, showTabs: false);
        DropChooser.MouseMove += OnChooserMouseMove;
    }

    /// <param name="restore">Bring the current tool back (the drag was abandoned over the notch).</param>
    private void HideDropChooser(bool restore)
    {
        if (DropChooser.Visibility != Visibility.Visible) return;
        DropChooser.MouseMove -= OnChooserMouseMove;
        DropChooser.Visibility = Visibility.Collapsed;
        ToolHost.Visibility = Visibility.Visible;
        if (restore && _expanded) ShowTool(_activeTool);
    }

    /// <summary>A plain mouse move (no drag in progress raises it) means the drag ended without a drop here.</summary>
    private void OnChooserMouseMove(object sender, MouseEventArgs e) => HideDropChooser(restore: true);

    private void OnChooserDragEnter(object sender, DragEventArgs e)
    {
        OnChooserDragOver(sender, e);
        if (FrameOf(sender) is { } frame)
        {
            frame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
            frame.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentSoftBrush");
            frame.Opacity = 1;
        }
    }

    private void OnChooserDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropPayload.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnChooserDragLeave(object sender, DragEventArgs e)
    {
        if (FrameOf(sender) is { } frame) ResetChooserTile(frame);
    }

    private void OnChooserDrop(object sender, DragEventArgs e)
    {
        if (DropPayload.Read(e.Data) is not { } payload) return;
        e.Handled = true;
        DeliverDrop(payload, sender == DropPhone ? DropTarget.Phone : sender == DropAsk ? DropTarget.Ask : DropTarget.Shelf);
    }

    private Rectangle? FrameOf(object sender) =>
        sender == DropShelf ? DropShelfFrame : sender == DropPhone ? DropPhoneFrame : sender == DropAsk ? DropAskFrame : null;

    private static void ResetChooserTile(Rectangle frame)
    {
        frame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SubTextBrush");
        frame.Fill = null;
        frame.Opacity = 0.5;
    }
}
