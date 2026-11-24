using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Metadata;
using Avalonia.Utilities;
using Avalonia.VisualTree;

namespace SatoriInsight.Controls;

public sealed class SortColumn : TableViewColumn
{
    public static readonly DirectProperty<SortColumn, string?> SortIndicatorProperty =
        AvaloniaProperty.RegisterDirect<SortColumn, string?>(nameof(SortIndicator), column => column.SortIndicator);

    [AssignBinding]
    public BindingBase? SortBinding { get; set; }

    public string? SortIndicator
    {
        get;
        private set => SetAndRaise(SortIndicatorProperty, ref field, value);
    }

    internal ListSortDirection? Direction
    {
        get;
        set
        {
            field = value;
            SortIndicator = value switch
            {
                ListSortDirection.Ascending => "↑",
                ListSortDirection.Descending => "↓",
                _ => null
            };
        }
    }
}

public sealed class SortableTableView : TableView, IWeakEventSubscriber<NotifyCollectionChangedEventArgs>
{
    public static readonly StyledProperty<IEnumerable?> SourceProperty =
        AvaloniaProperty.Register<SortableTableView, IEnumerable?>(nameof(Source));

    private readonly List<SortColumn> _sortColumns = [];

    public SortableTableView()
    {
        Tapped += HeaderTapped;
        DoubleTapped += HeaderTapped;
    }

    public IEnumerable? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TableView);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            if (change.OldValue is INotifyCollectionChanged previous)
            {
                WeakEvents.CollectionChanged.Unsubscribe(previous, this);
            }

            if (Source is INotifyCollectionChanged current)
            {
                WeakEvents.CollectionChanged.Subscribe(current, this);
            }

            UpdateRows();
        }
    }

    void IWeakEventSubscriber<NotifyCollectionChangedEventArgs>.OnEvent(object? sender, WeakEvent ev, NotifyCollectionChangedEventArgs e)
    {
        if (_sortColumns.Count > 0)
        {
            UpdateRows();
        }
    }

    private void HeaderTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Thumb>(includeSelf: true) is null
            && source.FindAncestorOfType<TableViewColumnHeader>(includeSelf: true)?.Column is SortColumn column)
        {
            SortBy(column, e.KeyModifiers);
            e.Handled = true;
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && e.Source is TableViewColumnHeader { Column: SortColumn column })
        {
            SortBy(column, e.KeyModifiers);
            e.Handled = true;
        }
        else if (Selection.Count > 0 && Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy.Any(gesture => gesture.Matches(e)) == true
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            e.Handled = true;
            var cells = Columns.Where(column => column.IsVisible).Select(column => column.Binding is { } binding ? ReadValue(binding) : null).ToArray();
            var rows = Selection.SelectedIndexes.Order().Select(index => string.Join('\t', cells.Select(read => read?.Invoke(ItemsView[index]))));
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, rows));
        }
        else
        {
            base.OnKeyDown(e);
        }
    }

    private void SortBy(SortColumn column, KeyModifiers modifiers)
    {
        var direction = column.Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        if (!modifiers.HasFlag(KeyModifiers.Shift))
        {
            foreach (var previous in _sortColumns)
            {
                previous.Direction = null;
            }

            _sortColumns.Clear();
        }

        if (!modifiers.HasFlag(Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers))
        {
            if (!_sortColumns.Contains(column))
            {
                _sortColumns.Add(column);
            }

            column.Direction = direction;
        }

        UpdateRows();
    }

    private void UpdateRows()
    {
        var rows = Source is not null && _sortColumns.Count > 0 ? SortRows(Source) : Source;
        // Apply the items and their selection together. A transient deselection
        // would also clear the object details or the process chosen for attachment.
        BeginInit();

        try
        {
            ItemsSource = rows;
        }
        finally
        {
            EndInit();
        }
    }

    private object?[] SortRows(IEnumerable source)
    {
        var rows = source.Cast<object?>();
        IOrderedEnumerable<object?>? sorted = null;

        foreach (var column in _sortColumns)
        {
            var key = ReadValue(column.SortBinding ?? column.Binding!);
            sorted = column.Direction == ListSortDirection.Ascending
                ? sorted?.ThenBy(key) ?? rows.OrderBy(key)
                : sorted?.ThenByDescending(key) ?? rows.OrderByDescending(key);
        }

        return sorted!.ToArray();
    }

    private static Func<object?, object?> ReadValue(BindingBase binding)
    {
        var cell = new ContentControl();
        cell.Bind(ContentControl.ContentProperty, binding);
        return item =>
        {
            cell.DataContext = item;
            return cell.Content;
        };
    }
}
