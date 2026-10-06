using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using OrixNotch.Services;

namespace OrixNotch.Tools;

public sealed partial class TodoItem : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _starred;
    public DateTime Created { get; set; } = DateTime.Now;
}

public partial class TodosView : UserControl
{
    private const string FileName = "todos.json";
    private readonly ObservableCollection<TodoItem> _items;

    public TodosView()
    {
        InitializeComponent();
        _items = new ObservableCollection<TodoItem>(Storage.Load<List<TodoItem>>(FileName));
        foreach (var item in _items) item.PropertyChanged += OnItemChanged;

        // Open items first, starred on top, newest first.
        var view = new ListCollectionView(_items) { IsLiveSorting = true };
        view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Done), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Starred), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(TodoItem.Created), ListSortDirection.Descending));
        view.LiveSortingProperties.Add(nameof(TodoItem.Done));
        view.LiveSortingProperties.Add(nameof(TodoItem.Starred));
        List.ItemsSource = view;

        _items.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        Save();
        Refresh();
    }

    private void Refresh()
    {
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var open = _items.Count(i => !i.Done);
        Summary.Text = _items.Count == 0 ? "" : $"{open} open · {_items.Count - open} done";
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
