using System.IO;
using OrixNotch.Shell;
using System.Windows;
using System.Windows.Controls;
using OrixNotch.Services;

namespace OrixNotch.Tools;

public sealed record EmojiEntry(string Char, string Name, string Group);

public partial class EmojiView : UserControl
{
    private const int PerRow = 16;
    private const string RecentFile = "emoji-recent.json";
    private const string RecentGroup = "Recent";

    private static readonly (string Group, string Label, AppIcon Icon)[] GroupLabels =
    [
        ("Smileys & Emotion", "Smileys", AppIcon.FaceSmile),
        ("People & Body", "People", AppIcon.User),
        ("Animals & Nature", "Nature", AppIcon.Globe),
        ("Food & Drink", "Food", AppIcon.Cake),
        ("Travel & Places", "Travel", AppIcon.Map),
        ("Activities", "Activities", AppIcon.Trophy),
        ("Objects", "Objects", AppIcon.LightBulb),
        ("Symbols", "Symbols", AppIcon.Heart),
        ("Flags", "Flags", AppIcon.Flag),
    ];

    private static readonly Lazy<List<EmojiEntry>> AllEmoji = new(LoadEmoji);
    private readonly List<string> _recent = Storage.Load<List<string>>(RecentFile);
    private string _group = "SmileyFillys & Emotion";

    public EmojiView()
    {
        InitializeComponent();
        AddGroupChip(RecentGroup, RecentGroup, AppIcon.Clock);
        foreach (var (group, label, icon) in GroupLabels) AddGroupChip(group, label, icon);
        if (_recent.Count > 0) _group = RecentGroup;
        ((RadioButton)Groups.Children[_group == RecentGroup ? 0 : 1]).IsChecked = true;
        Render();
    }

    private void AddGroupChip(string group, string label, AppIcon icon)
    {
        var chip = new RadioButton
        {
            Style = (Style)FindResource("ChipButton"),
            Content = new HeroIcon { Kind = icon, Width = 14, Height = 14 },
            ToolTip = label,
            GroupName = "emojiGroup",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 4, 0),
        };
        chip.Checked += (_, _) =>
        {
            _group = group;
            Search.Clear();
            Render();
        };
        Groups.Children.Add(chip);
    }

    private System.Windows.Threading.DispatcherTimer? _searchPause;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchPause is null)
        {
            _searchPause = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchPause.Tick += (_, _) =>
            {
                _searchPause.Stop();
                Render();
            };
        }
        _searchPause.Stop();
        _searchPause.Start();
    }

    private void Render()
    {
        var q = Search.Text.Trim();
        IEnumerable<EmojiEntry> items;
        if (q.Length > 0)
            items = AllEmoji.Value.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        else if (_group == RecentGroup)
            items = _recent.Select(c => AllEmoji.Value.FirstOrDefault(e => e.Char == c) ?? new EmojiEntry(c, c, RecentGroup));
        else
            items = AllEmoji.Value.Where(e => e.Group == _group);

        var rows = items.Chunk(PerRow).ToList();
        Rows.ItemsSource = rows;
        if (rows.Count > 0) Rows.ScrollIntoView(rows[0]);
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EmojiEntry entry) return;
        ClipboardService.Instance.CopyQuiet(entry.Char);
        Status.Text = $"Copied: {entry.Name}";

        _recent.Remove(entry.Char);
        _recent.Insert(0, entry.Char);
        if (_recent.Count > PerRow * 3) _recent.RemoveAt(_recent.Count - 1);
        Storage.Save(RecentFile, _recent);
    }

    /// <summary>Parses the embedded Unicode emoji-test.txt (fully-qualified, no skin-tone variants).</summary>
    private static List<EmojiEntry> LoadEmoji()
    {
        var list = new List<EmojiEntry>();
        using var stream = typeof(EmojiView).Assembly.GetManifestResourceStream("OrixNotch.Resources.emoji-test.txt");
        if (stream is null) return list;
        using var reader = new StreamReader(stream);
        var group = "";
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# group:", StringComparison.Ordinal))
            {
                group = line[8..].Trim();
                continue;
            }
            if (line.Length == 0 || line[0] == '#' || group == "Component") continue;
            if (!line.Contains("; fully-qualified", StringComparison.Ordinal)) continue;

            var hash = line.IndexOf('#');
            if (hash < 0) continue;
            // "# 😀 E1.0 grinning face"
            var parts = line[(hash + 1)..].Trim().Split(' ', 3);
            if (parts.Length < 3) continue;
            var name = parts[2];
            if (name.Contains("skin tone", StringComparison.Ordinal)) continue;
            // The color renderer cannot draw ZWJ sequences or emoji newer than Windows' font.
            if (parts[0].Contains('‍') || !IsSupportedVersion(parts[1])) continue;
            list.Add(new EmojiEntry(parts[0], name, group));
        }
        return list;
    }

    /// <summary>"E15.0" → supported; anything newer is missing from Segoe UI Emoji.</summary>
    private static bool IsSupportedVersion(string tag) =>
        tag.Length > 1 && double.TryParse(tag[1..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v <= 15.0;
}
