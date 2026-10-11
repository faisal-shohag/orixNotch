using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

public sealed partial class NoteEntry : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Created { get; set; } = DateTime.Now;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] private string _text = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(When))] private DateTime _updated = DateTime.Now;

    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>First non-blank line of the note.</summary>
    [JsonIgnore]
    public string Title
    {
        get
        {
            foreach (var line in Text.Split('\n'))
                if (line.Trim() is { Length: > 0 } t) return t;
            return "New note";
        }
    }

    [JsonIgnore]
    public string When =>
        Updated.Date == DateTime.Today ? Updated.ToString("h:mm tt")
        : Updated.Year == DateTime.Today.Year ? Updated.ToString("MMM d")
        : Updated.ToString("MMM d, yyyy");

    [JsonIgnore]
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Several plain-text notes, newest first, saved to notes.json; the grab handle resizes the notch.</summary>
public partial class QuickNoteView : UserControl, IToolView
{
    private const string FileName = "notes.json";
    private static readonly string LegacyPath = Storage.PathOf("note.txt");

    private readonly ObservableCollection<NoteEntry> _notes;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private NoteEntry _current = null!;
    private bool _loading;
    private bool _dirty;

    public QuickNoteView()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => Save();
        PreviewKeyDown += OnPreviewKeyDown;

        _notes = new ObservableCollection<NoteEntry>(LoadNotes().OrderByDescending(n => n.Updated));
        if (_notes.Count == 0) _notes.Add(new NoteEntry());
        List.ItemsSource = _notes;

        Select(_notes.FirstOrDefault(n => n.Id == App.Settings.LastNoteId) ?? _notes[0]);
    }

    /// <summary>notes.json, or the single note.txt from before multiple notes existed.</summary>
    private static List<NoteEntry> LoadNotes()
    {
        if (File.Exists(Storage.PathOf(FileName))) return Storage.Load<List<NoteEntry>>(FileName);
        try
        {
            if (File.Exists(LegacyPath) && File.ReadAllText(LegacyPath) is { } text && !string.IsNullOrWhiteSpace(text))
            {
                var at = File.GetLastWriteTime(LegacyPath);
                return [new NoteEntry { Text = text, Created = at, Updated = at }];
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return [];
    }

    public void OnShown() => ApplyHeight();

    public void OnHidden()
    {
        Save();
        SettingsService.Save(notify: false);
    }

    // ---------------------------------------------------------------- notes

    private void Select(NoteEntry note)
    {
        if (_current is { } previous && previous != note)
        {
            previous.IsSelected = false;
            // Don't leave empty notes behind when moving to another one.
            if (previous.IsBlank && _notes.Count > 1)
            {
                _notes.Remove(previous);
                _dirty = true;
            }
        }
        Save();

        _current = note;
        note.IsSelected = true;
        App.Settings.LastNoteId = note.Id;

        _loading = true;
        Editor.Text = note.Text;
        Editor.CaretIndex = Editor.Text.Length;
        Editor.ScrollToEnd();
        _loading = false;
        UpdateStatus("");
    }

    private void NewNote()
    {
        if (!_current.IsBlank)
        {
            var note = new NoteEntry();
            _notes.Insert(0, note);
            Select(note);
        }
        Editor.Focus();
    }

    private void Delete(NoteEntry note)
    {
        var index = _notes.IndexOf(note);
        if (index < 0) return;
        _notes.Remove(note);
        if (_notes.Count == 0) _notes.Add(new NoteEntry());
        _dirty = true;
        if (note == _current)
        {
            _current = null!;
            Select(_notes[Math.Min(index, _notes.Count - 1)]);
        }
        Save();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _current.Text = Editor.Text;
        _current.Updated = DateTime.Now;
        var index = _notes.IndexOf(_current);
        if (index > 0) _notes.Move(index, 0);

        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateStatus("Editing…");
    }

    private void Save()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        Storage.Save(FileName, _notes.Where(n => !n.IsBlank || n == _current).ToList());
        _dirty = false;
        UpdateStatus("Saved");
    }

    private void UpdateStatus(string state)
    {
        var words = Editor.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Status.Text = string.IsNullOrEmpty(state) ? $"{words} words" : $"{state} · {words} words";
    }

    private void OnNewNote(object sender, RoutedEventArgs e) => NewNote();

    private void OnRowDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteEntry note) return;
        if (note != _current) Select(note);
        Editor.Focus();
        e.Handled = true;
    }

    private void OnDeleteRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteEntry note) Delete(note);
    }

    private void OnDeleteCurrent(object sender, RoutedEventArgs e) => Delete(_current);

    private void OnCopy(object sender, RoutedEventArgs e) => ClipboardService.Instance.CopyQuiet(Editor.Text);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key != Key.N) return;
        e.Handled = true;
        NewNote();
    }

    // ---------------------------------------------------------------- resize (same handle as Ask AI)

    private const double DefaultHeight = 220;
    private const double MinNoteHeight = 200;
    private const double MaxNoteHeight = 520;
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;

    private static void ApplyHeight() =>
        NotchWindow.Instance?.SetToolSize(720, Math.Clamp(App.Settings.NoteHeight, MinNoteHeight, MaxNoteHeight));

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            App.Settings.NoteHeight = DefaultHeight;
            ApplyHeight();
            SettingsService.Save(notify: false);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStartY = PointToScreen(e.GetPosition(this)).Y;
        _dragStartHeight = ActualHeight; // what's shown, which may be capped to fit the screen
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var dy = (PointToScreen(e.GetPosition(this)).Y - _dragStartY) / dpi;
        var want = Math.Clamp(_dragStartHeight + dy, MinNoteHeight, MaxNoteHeight);
        App.Settings.NoteHeight = NotchWindow.Instance?.SetToolSize(720, want) ?? want;
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Grip.ReleaseMouseCapture();
        SettingsService.Save(notify: false);
    }
}
