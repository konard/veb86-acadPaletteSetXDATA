using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.UI;

/// <summary>
/// The «Свойства» tab (TZXDATA 8): the control panel and one virtualized table-tree over
/// <see cref="PropertyGridViewModel.Rows"/>. The view only maps columns, scrolling and keys to the model.
/// </summary>
public partial class PropertyGridView : UserControl
{
    private readonly GridViewColumn[] columns;
    private PropertyGridViewModel? model;
    private ScrollViewer? scroll;
    private bool restoring;

    public PropertyGridView()
    {
        InitializeComponent();
        columns = new[] { ActionsColumn, CaptionColumn, ValueColumn, StateColumn, NoteColumn };
        foreach (var column in columns) ((INotifyPropertyChanged)column).PropertyChanged += OnColumnChanged;
        DataContextChanged += (_, _) => Attach(DataContext as PropertyGridViewModel);
        Loaded += (_, _) => { Attach(DataContext as PropertyGridViewModel); RestoreScroll(); };
        Unloaded += (_, _) => Attach(null);
        Rows.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
        Rows.PreviewKeyDown += OnKey;
        Rows.MouseDoubleClick += OnDoubleClick;
    }

    /// <summary>The scroll host of the rows, available after the list is loaded.</summary>
    public ScrollViewer? RowsScroll => scroll ??= Find<ScrollViewer>(Rows);

    private void Attach(PropertyGridViewModel? next)
    {
        if (ReferenceEquals(model, next)) return;
        if (model != null) model.PropertyChanged -= OnModelChanged;
        model = next;
        // The header menu is not in the visual tree and does not inherit the data context.
        ColumnMenu.DataContext = model;
        if (model == null) return;
        model.PropertyChanged += OnModelChanged;
        for (var index = 0; index < columns.Length; index++) columns[index].Width = model.ColumnWidths[index];
        ShowColumns();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(PropertyGridViewModel.ShowActionsColumn):
            case nameof(PropertyGridViewModel.ShowStateColumn):
            case nameof(PropertyGridViewModel.ShowNoteColumn):
                ShowColumns();
                break;
            case nameof(PropertyGridViewModel.Rows):
            case nameof(PropertyGridViewModel.ScrollOffset) when !restoring:
                // A new item list resets the scroll host; the saved position comes back after layout.
                restoring = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RestoreScroll));
                break;
        }
    }

    /// <summary>Columns 0, 3 and 4 are hidden through the header context menu (TZXDATA 8.2).</summary>
    private void ShowColumns()
    {
        if (model == null) return;
        var visible = columns.Where((_, index) =>
            index == 0 ? model.ShowActionsColumn : index == 3 ? model.ShowStateColumn : index == 4 ? model.ShowNoteColumn : true).ToArray();
        if (Columns.Columns.SequenceEqual(visible)) return;
        Columns.Columns.Clear();
        foreach (var column in visible) Columns.Columns.Add(column);
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (model == null || args.PropertyName != nameof(GridViewColumn.Width)) return;
        var index = Array.IndexOf(columns, sender);
        if (index >= 0 && !double.IsNaN(columns[index].Width)) model.ColumnWidths[index] = columns[index].Width;
    }

    private void RestoreScroll()
    {
        restoring = true;
        try
        {
            if (model != null && RowsScroll != null && Math.Abs(RowsScroll.VerticalOffset - model.ScrollOffset) > 0.5)
            {
                RowsScroll.ScrollToVerticalOffset(model.ScrollOffset);
                RowsScroll.UpdateLayout();
            }
        }
        finally { restoring = false; }
    }

    private void OnScroll(object sender, ScrollChangedEventArgs args)
    {
        if (restoring || model == null || args.VerticalChange == 0 || args.ExtentHeightChange != 0) return;
        restoring = true;
        try { model.ScrollOffset = args.VerticalOffset; }
        finally { restoring = false; }
    }

    private void OnKey(object sender, KeyEventArgs args)
    {
        if (model == null || !(Rows.SelectedItem is PropertyRow row) || !row.IsExpandable) return;
        var toggle = args.Key switch
        {
            Key.Right or Key.Add => !row.IsExpanded,
            Key.Left or Key.Subtract => row.IsExpanded,
            Key.Enter or Key.Space => true,
            _ => false
        };
        if (!toggle) return;
        model.Toggle(row);
        args.Handled = true;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (model == null || args.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(Rows, source) is ListViewItem { DataContext: PropertyRow row } && row.IsExpandable)
        {
            model.Toggle(row);
            args.Handled = true;
        }
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = Find<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}

/// <summary>Tree indentation of column 1: 14 px per level.</summary>
public sealed class IndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is int level ? level * 14 : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
