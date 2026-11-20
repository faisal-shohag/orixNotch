using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace OrixNotch.Shell;

/// <summary>A short, light confetti burst over a canvas (to-do list cleared). Pieces remove themselves.</summary>
public static class Confetti
{
    private static readonly Color[] Colors =
        [Color.FromRgb(0xFD, 0x03, 0x88), Color.FromRgb(0xFC, 0x6F, 0x18), Color.FromRgb(0xFF, 0xC2, 0x33), Color.FromRgb(0x34, 0xD3, 0xA0), Color.FromRgb(0x38, 0xBD, 0xF8)];

    public static void Burst(Canvas canvas, Point from, int pieces = 36)
    {
        if (Motion.Reduced) return; // pure decoration: skipped when Windows' animation effects are off
        var random = Random.Shared;
        for (var i = 0; i < pieces; i++)
        {
            var piece = new Rectangle
            {
                Width = 4 + random.NextDouble() * 4, Height = 3 + random.NextDouble() * 3, RadiusX = 1, RadiusY = 1,
                Fill = new SolidColorBrush(Colors[i % Colors.Length]), IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var move = new TranslateTransform();
            var spin = new RotateTransform(random.Next(360));
            piece.RenderTransform = new TransformGroup { Children = { spin, move } };
            Canvas.SetLeft(piece, from.X);
            Canvas.SetTop(piece, from.Y);
            canvas.Children.Add(piece);

            // Out in a fan, then down: x eases out, y rises briefly then falls.
            var angle = -Math.PI / 2 + (random.NextDouble() - 0.5) * Math.PI * 1.1;
            var speed = 60 + random.NextDouble() * 110;
            var time = TimeSpan.FromMilliseconds(900 + random.Next(500));
            move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, Math.Cos(angle) * speed * 1.6, time) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            var y = new DoubleAnimationUsingKeyFrames { Duration = time };
            y.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Sin(angle) * speed * 0.6, KeyTime.FromPercent(0.3), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            y.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Sin(angle) * speed * 0.6 + 140, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            move.BeginAnimation(TranslateTransform.YProperty, y);
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(spin.Angle, spin.Angle + random.Next(-540, 540), time));
            var fade = new DoubleAnimation(1, 0, time) { BeginTime = TimeSpan.Zero, EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => canvas.Children.Remove(piece);
            piece.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
