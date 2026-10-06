using System.IO;
using System.Windows;
using System.Windows.Interop;
using OrixNotch.Services;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace OrixNotch.Tools;

/// <summary>
/// The teleprompter's scrolling text, drawn in a small native (GDI+) window placed exactly over the
/// text area of the notch.
///
/// Why not WPF: the notch is a per-pixel transparent WPF window and every WPF frame — even in a
/// separate, opaque WPF window — cost ~3 ms of CPU on test hardware. Here the script is rendered once
/// into a bitmap and each scroll step just blits a slice of it (well under a millisecond), so the
/// prompter can scroll continuously without dragging the rest of the PC down.
///
/// See-through mode (opacity &lt; 1) uses a color key: the background becomes fully transparent so the
/// translucent notch behind shows through, while the text stays opaque and crisp.
/// </summary>
public sealed class PrompterSurface : Forms.Form
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const double LineSpacing = 1.35;
    private const int SideMarginDip = 24;

    private readonly IntPtr _owner;
    private Drawing.Bitmap? _textBitmap;
    private Drawing.Bitmap? _fadeTop;
    private Drawing.Bitmap? _fadeBottom;
    private Drawing.Bitmap? _buffer;
    private string _text = "";
    private double _fontSizeDip = 20;
    private double _dpi = 1;
    private int _y;
    private bool _mirrored;
    private bool _showPaused;
    private bool _seeThrough;
    private Drawing.Color _background = Drawing.Color.Black;
    private Drawing.Color _foreground = Drawing.Color.White;
    private Drawing.Color _subText = Drawing.Color.Gray;

    private static Drawing.Text.PrivateFontCollection? _fonts;

    /// <summary>Left click toggles play/pause; the wheel nudges the position.</summary>
    public event Action? Clicked;
    public event Action<int>? Wheel;

    public PrompterSurface(Window owner)
    {
        _owner = new WindowInteropHelper(owner).Handle;
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        Location = new Drawing.Point(-10000, -10000);
        Size = new Drawing.Size(10, 10);
        Text = "OrixNotch Teleprompter";
        Cursor = Forms.Cursors.Hand;
        SetStyle(Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.UserPaint |
                 Forms.ControlStyles.Opaque, true);
        MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
        };
        MouseWheel += (_, e) => Wheel?.Invoke(e.Delta);
        ThemeService.Changed += ApplyBackground;
        ApplyBackground();
    }

    /// <summary>Owned by the notch (stays above it), never activated (clicks don't close the notch).</summary>
    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            cp.Parent = _owner; // for a popup this sets the owner, not a parent
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public bool IsShown => Visible;

    /// <summary>Height of the wrapped script in DIPs (how far it can scroll).</summary>
    public double TextHeightDip => _textBitmap is null ? 0 : _textBitmap.Height / _dpi;

    public bool Mirrored
    {
        set
        {
            if (_mirrored == value) return;
            _mirrored = value;
            Invalidate();
        }
    }

    public bool ShowPaused
    {
        set
        {
            if (_showPaused == value) return;
            _showPaused = value;
            Invalidate();
        }
    }

    /// <summary>Scroll position: where the top of the text sits, in DIPs from the top of the stage.</summary>
    public double YDip
    {
        set
        {
            var y = (int)Math.Round(value * _dpi);
            if (y == _y) return;
            _y = y;
            Invalidate();
        }
    }

    public void SetContent(string text, double fontSizeDip)
    {
        if (text == _text && Math.Abs(fontSizeDip - _fontSizeDip) < 0.01 && _textBitmap is not null) return;
        _text = text;
        _fontSizeDip = fontSizeDip;
        Rebuild();
    }

    /// <summary>Moves/sizes the window to a rectangle in physical pixels and shows it without activating.</summary>
    public void PlaceAt(Rect screenPixels, double dpiScale)
    {
        var resized = (int)Math.Round(screenPixels.Width) != ClientSize.Width || Math.Abs(dpiScale - _dpi) > 0.001;
        _dpi = dpiScale;
        SetBounds((int)Math.Round(screenPixels.X), (int)Math.Round(screenPixels.Y),
            (int)Math.Round(screenPixels.Width), (int)Math.Round(screenPixels.Height));
        if (resized || _textBitmap is null) Rebuild();
        if (!Visible) Show();
        Invalidate();
    }

    /// <summary>Opaque notch color with soft edge fades, or color-keyed see-through when opacity &lt; 1.</summary>
    public void ApplyBackground()
    {
        static Drawing.Color From(string key)
        {
            var c = ((Media.SolidColorBrush)ThemeService.Get(key)).Color;
            return Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        var pill = From("PillBrush");
        _foreground = From("TextBrush");
        _subText = From("SubTextBrush");
        _seeThrough = App.Settings.PrompterOpacity < 0.99;
        // Key color: the notch color nudged one step so text anti-aliasing blends toward the real
        // background and nothing else drawn here matches it exactly.
        var key = Drawing.Color.FromArgb(pill.R, pill.G >= 128 ? pill.G - 1 : pill.G + 1, pill.B);
        _background = _seeThrough ? key : pill;
        BackColor = _background;
        TransparencyKey = _seeThrough ? key : Drawing.Color.Empty;
        Rebuild(); // anti-aliasing is baked against the background color
    }

    // ---------------------------------------------------------------- rendering

    private static Drawing.FontFamily UiFontFamily()
    {
        // Prefer SF Pro when the user has it installed, otherwise the bundled Inter (same as the UI).
        foreach (var name in new[] { "SF Pro Text", "SF Pro Display", "SF Pro" })
        {
            try
            {
                var family = new Drawing.FontFamily(name);
                if (family.Name == name) return family;
            }
            catch (ArgumentException)
            {
                // not installed
            }
        }

        if (_fonts is null)
        {
            _fonts = new Drawing.Text.PrivateFontCollection();
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Fonts/Inter-Medium.ttf"));
                if (info is not null)
                {
                    var path = Path.Combine(Storage.Dir("cache"), "Inter-Medium.ttf");
                    if (!File.Exists(path))
                    {
                        using var file = File.Create(path);
                        info.Stream.CopyTo(file);
                    }
                    _fonts.AddFontFile(path);
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        return _fonts.Families.Length > 0 ? _fonts.Families[0] : Drawing.FontFamily.GenericSansSerif;
    }

    /// <summary>Wraps and renders the whole script once into a bitmap the width of the stage.</summary>
    private void Rebuild()
    {
        var width = ClientSize.Width - (int)Math.Round(2 * SideMarginDip * _dpi);
        if (width < 40 || string.IsNullOrEmpty(_text))
        {
            _textBitmap?.Dispose();
            _textBitmap = null;
            Invalidate();
            return;
        }

        var sizePx = (float)(_fontSizeDip * _dpi);
        var lineHeight = (int)Math.Round(sizePx * LineSpacing);
        using var font = new Drawing.Font(UiFontFamily(), sizePx, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel);
        using var format = (Drawing.StringFormat)Drawing.StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= Drawing.StringFormatFlags.MeasureTrailingSpaces;

        // Greedy word wrap per paragraph; blank lines keep their height.
        var lines = new List<string>();
        using (var probe = Drawing.Graphics.FromHwnd(IntPtr.Zero))
        {
            foreach (var paragraph in _text.Replace("\r\n", "\n").Split('\n'))
            {
                var current = "";
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = current.Length == 0 ? word : current + " " + word;
                    if (current.Length > 0 && probe.MeasureString(candidate, font, int.MaxValue, format).Width > width)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else
                    {
                        current = candidate;
                    }
                }
                lines.Add(current);
            }
        }

        var bmp = new Drawing.Bitmap(width, Math.Max(1, lines.Count * lineHeight), Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(_background);
            // Grayscale AA: ClearType fringes would show colored edges against a color key.
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = new Drawing.SolidBrush(_foreground);
            for (var i = 0; i < lines.Count; i++)
            {
                var w = g.MeasureString(lines[i], font, int.MaxValue, format).Width;
                g.DrawString(lines[i], font, brush, (width - w) / 2f, i * lineHeight + (lineHeight - sizePx) / 2f, format);
            }
        }
        _textBitmap?.Dispose();
        _textBitmap = bmp;
        BuildFades();
        Invalidate();
    }

    /// <summary>Edge fades rendered once into small bitmaps; per frame they're just two image copies.</summary>
    private void BuildFades()
    {
        _fadeTop?.Dispose();
        _fadeBottom?.Dispose();
        _fadeTop = _fadeBottom = null;
        if (_seeThrough || ClientSize.Width <= 0) return;

        Drawing.Bitmap Make(int height, bool topEdge)
        {
            var bmp = new Drawing.Bitmap(ClientSize.Width, Math.Max(1, height), Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Drawing.Graphics.FromImage(bmp);
            g.Clear(Drawing.Color.Transparent);
            var solid = _background;
            var clear = Drawing.Color.FromArgb(0, _background);
            using var brush = new Drawing2D.LinearGradientBrush(new Drawing.Rectangle(0, -1, bmp.Width, bmp.Height + 2),
                topEdge ? solid : clear, topEdge ? clear : solid, Drawing2D.LinearGradientMode.Vertical);
            g.FillRectangle(brush, 0, 0, bmp.Width, bmp.Height);
            return bmp;
        }

        _fadeTop = Make((int)Math.Round(44 * _dpi), topEdge: true);
        _fadeBottom = Make((int)Math.Round(36 * _dpi), topEdge: false);
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        // Compose into our own reusable back buffer, then one straight copy to the window. WinForms'
        // built-in OptimizedDoubleBuffer measured several times the cost of everything else here.
        var w = Math.Max(1, ClientSize.Width);
        var h = Math.Max(1, ClientSize.Height);
        if (_buffer is null || _buffer.Width != w || _buffer.Height != h)
        {
            _buffer?.Dispose();
            _buffer = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
        using (var bg = Drawing.Graphics.FromImage(_buffer)) Compose(bg);
        e.Graphics.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
        e.Graphics.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.DrawImageUnscaled(_buffer, 0, 0);
    }

    private void Compose(Drawing.Graphics g)
    {
        g.CompositingQuality = Drawing2D.CompositingQuality.HighSpeed;
        g.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = Drawing2D.PixelOffsetMode.None;
        g.SmoothingMode = Drawing2D.SmoothingMode.None;
        g.Clear(_background);
        if (_mirrored)
        {
            g.TranslateTransform(ClientSize.Width, 0);
            g.ScaleTransform(-1, 1);
        }

        if (_textBitmap is not null)
        {
            // Copy only the visible slice of the pre-rendered script.
            var left = (int)Math.Round(SideMarginDip * _dpi);
            var srcTop = Math.Max(0, -_y);
            var dstTop = Math.Max(0, _y);
            var h = Math.Min(_textBitmap.Height - srcTop, ClientSize.Height - dstTop);
            if (h > 0)
            {
                // The text bitmap already has the background baked in → straight copy, no alpha blending.
                g.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(_textBitmap, new Drawing.Rectangle(left, dstTop, _textBitmap.Width, h),
                    new Drawing.Rectangle(0, srcTop, _textBitmap.Width, h), Drawing.GraphicsUnit.Pixel);
                g.CompositingMode = Drawing2D.CompositingMode.SourceOver;
            }
        }

        if (_fadeTop is not null && _fadeBottom is not null)
        {
            g.DrawImageUnscaled(_fadeTop, 0, 0);
            g.DrawImageUnscaled(_fadeBottom, 0, ClientSize.Height - _fadeBottom.Height);
        }

        if (_showPaused)
        {
            g.ResetTransform();
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Drawing.Font(UiFontFamily(), (float)(11 * _dpi), Drawing.GraphicsUnit.Pixel);
            using var brush = new Drawing.SolidBrush(_subText);
            const string hint = "Paused · click or press Space";
            var size = g.MeasureString(hint, font);
            g.DrawString(hint, font, brush, (ClientSize.Width - size.Width) / 2, ClientSize.Height - size.Height - (float)(2 * _dpi));
        }
    }

    protected override void OnPaintBackground(Forms.PaintEventArgs e)
    {
        // Everything is painted in OnPaint (avoids flicker).
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _textBitmap?.Dispose();
            _fadeTop?.Dispose();
            _fadeBottom?.Dispose();
        }
        base.Dispose(disposing);
    }
}
