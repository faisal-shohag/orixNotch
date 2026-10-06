using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

public partial class QuickNoteView : UserControl, IToolView
{
    private static readonly string NotePath = Storage.PathOf("note.txt");
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _loading;
    private bool _dirty;

    public QuickNoteView()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => Save();

        _loading = true;
        try
        {
            if (File.Exists(NotePath)) Editor.Text = File.ReadAllText(NotePath);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        _loading = false;
        UpdateStatus("");
    }

    public void OnShown()
    {
    }

    public void OnHidden() => Save();

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateStatus("Editing…");
    }

    private void Save()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        try
        {
            File.WriteAllText(NotePath, Editor.Text);
            _dirty = false;
            UpdateStatus("Saved");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            UpdateStatus("Could not save");
        }
    }

    private void UpdateStatus(string state)
    {
        var words = Editor.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Status.Text = string.IsNullOrEmpty(state) ? $"{words} words" : $"{state} · {words} words";
    }

    private void OnCopy(object sender, RoutedEventArgs e) => ClipboardService.Instance.CopyQuiet(Editor.Text);
}
