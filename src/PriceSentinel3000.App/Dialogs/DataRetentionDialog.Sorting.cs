using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace PriceSentinel3000.App.Dialogs;

public partial class DataRetentionDialog
{
    public static readonly DependencyProperty SortIndicatorProperty = DependencyProperty.RegisterAttached(
        "SortIndicator", typeof(string), typeof(DataRetentionDialog), new PropertyMetadata("↕"));

    public static string GetSortIndicator(DependencyObject element) => (string)element.GetValue(SortIndicatorProperty);
    public static void SetSortIndicator(DependencyObject element, string value) => element.SetValue(SortIndicatorProperty, value);

    private INotifyCollectionChanged? _librarySortChanges;

    private void LocalLibraryGrid_Loaded(object sender, RoutedEventArgs e)
    {
        LocalLibraryGrid_Unloaded(sender, e);
        _librarySortChanges = LocalLibraryGrid.Items.SortDescriptions;
        _librarySortChanges.CollectionChanged += LibrarySortChanged;
        UpdateLibrarySortIndicators();
    }

    private void LocalLibraryGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_librarySortChanges is not null) _librarySortChanges.CollectionChanged -= LibrarySortChanged;
        _librarySortChanges = null;
    }

    private void LibrarySortChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateLibrarySortIndicators();

    private void UpdateLibrarySortIndicators()
    {
        // WPF handles normal and Shift-click sorting. Reflect the actual view order,
        // including secondary columns, whenever it changes or this tab is reopened.
        SortDescription[] sorts = LocalLibraryGrid.Items.SortDescriptions.ToArray();
        foreach (DataGridColumn column in LocalLibraryGrid.Columns)
        {
            int index = Array.FindIndex(sorts, sort => sort.PropertyName == column.SortMemberPath);
            column.SortDirection = index < 0 ? null : sorts[index].Direction;
            SetSortIndicator(column, index < 0 ? "↕" :
                $"{(sorts[index].Direction == ListSortDirection.Ascending ? "↑" : "↓")} {index + 1}");
        }
    }
}
