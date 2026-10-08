using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

public sealed partial class TodoItem : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _starred;
    public DateTime Created { get; set; } = DateTime.Now;
}

public partial class TodosView : UserControl, IToolView
{
    private const string FileName = "todos.json";
    private readonly ObservableCollection<TodoItem> _items;
    private readonly ListCollectionView _view;

    public TodosView()
    {
        InitializeComponent();
        _items = new ObservableCollection<TodoItem>(Storage.Load<List<TodoItem>>(FileName));
        foreach (var item in _items) item.PropertyChanged += OnItemChanged;

        // Open items first, starred on top, newest first.
        var view = _view = new ListCollectionView(_items) { IsLiveSorting = true, IsLiveFiltering = true };
        view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Done), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Starred), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Created), ListSortDirection.Descending));
        view.LiveSortingProperties.Add(nameof(TodoItem.Done));
        view.LiveSortingProperties.Add(nameof(TodoItem.Starred));
        view.LiveFilteringProperties.Add(nameof(TodoItem.Done));
        view.LiveFilteringProperties.Add(nameof(TodoItem.Starred));
        List.ItemsSource = view;

        _items.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        Save();
        Refresh();
    }

    public void OnShown() => Input.Focus();

    public void OnHidden()
    {
    }

    private void Refresh()
    {
        var done = _items.Count(i => i.Done);
        CountAll.Text = _items.Count.ToString();
        CountOpen.Text = (_items.Count - done).ToString();
        CountStarred.Text = _items.Count(i => i.Starred).ToString();
        CountDone.Text = done.ToString();
        ClearButton.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;

        var shown = _items.Count(i => _view.Filter?.Invoke(i) ?? true);
        Empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = _items.Count == 0 ? "Nothing here yet."
            : FilterStarred.IsChecked == true ? "No starred to-dos."
            : FilterDone.IsChecked == true ? "Nothing completed yet."
            : "All done.";
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (_view is null) return; // fires during InitializeComponent
        _view.Filter = FilterOpen.IsChecked == true ? o => !((TodoItem)o).Done
            : FilterStarred.IsChecked == true ? o => ((TodoItem)o).Starred
            : FilterDone.IsChecked == true ? o => ((TodoItem)o).Done
            : null;
        Refresh();
    }

    private void Save() => Storage.Save(FileName, _items.ToList());

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add();
    }

    private void Add()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        // A new item wouldn't show under the Starred / Done filters; jump back to All so it's visible.
        if (FilterStarred.IsChecked == true || FilterDone.IsChecked == true) FilterAll.IsChecked = true;
        var item = new TodoItem { Text = text };
        item.PropertyChanged += OnItemChanged;
        _items.Add(item);
        Input.Clear();
        Save();
    }

    private void OnStar(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TodoItem item) item.Starred = !item.Starred;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem item) return;
        item.PropertyChanged -= OnItemChanged;
        _items.Remove(item);
        Save();
    }

    private void OnClearDone(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items.Where(i => i.Done).ToList())
        {
            item.PropertyChanged -= OnItemChanged;
            _items.Remove(item);
        }
        Save();
    }
}
