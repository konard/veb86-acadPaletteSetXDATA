using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.UI;

/// <summary>
/// The «Свойства» tab (TZXDATA 8): the control panel and one virtualized table-tree over
/// <see cref="PropertyGridViewModel.Rows"/>. The view only maps columns, scrolling, keys and the editors of the selected
/// row to the model: Enter or leaving the editor writes, Esc restores the value (TZXDATA 8.3, 8.5).
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
        Rows.PreviewMouseLeftButtonUp += OnMouseUp;
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
        if (model == null) return;
        if (EditorOf(args.OriginalSource as DependencyObject) is Control editor)
        {
            // Inside an editor the keys edit the text: Enter writes it, Esc shows the value again. An open list handles
            // both keys itself.
            if (editor is ComboBox { IsDropDownOpen: true } || editor.DataContext is not PropertyRow edited) return;
            if (args.Key == Key.Enter) model.Commit(edited);
            else if (args.Key == Key.Escape) model.Cancel(edited);
            else return;
            args.Handled = true;
            FocusRowLater();
            return;
        }
        if (!(Rows.SelectedItem is PropertyRow row)) return;
        // F2, or Enter on a row without children, moves the focus to the editor; Space clicks a check box.
        if ((args.Key == Key.F2 || args.Key == Key.Enter && !row.IsExpandable) && FocusEditor(row)) { args.Handled = true; return; }
        if (args.Key == Key.Space && !row.IsExpandable && row.CanSetFlag) { model.SetFlag(row, row.Flag != true); args.Handled = true; return; }
        if (!row.IsExpandable) return;
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

    /// <summary>A click on the value of the selected row puts the caret into its editor.</summary>
    private void OnMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (model == null || args.OriginalSource is not DependencyObject source || !(Rows.SelectedItem is PropertyRow row)) return;
        var editor = EditorOf(source);
        if (editor?.IsKeyboardFocusWithin == true || editor == null && !Up(source).OfType<FrameworkElement>().Any(element => Equals(element.Tag, "ValueCell"))) return;
        if (ItemsControl.ContainerFromElement(Rows, source) is ListViewItem { DataContext: PropertyRow clicked } && ReferenceEquals(clicked, row))
            FocusEditor(row);
    }

    /// <summary>Leaving the editor writes it after the focus has settled, so a click on another row selects it first.</summary>
    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (model == null || sender is not Control editor || editor.DataContext is not PropertyRow row) return;
        var grid = model;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (editor.IsKeyboardFocusWithin || editor is ComboBox { IsDropDownOpen: true }) return;
            grid.Commit(row);
        }));
    }

    /// <summary>A value picked in the list is written at once.</summary>
    private void OnChoiceClosed(object? sender, EventArgs args)
    {
        if (model == null || (sender as FrameworkElement)?.DataContext is not PropertyRow { IsDirty: true } row) return;
        if (model.Commit(row)) FocusRowLater();
    }

    /// <summary>A check box of column 2 or 3 writes the clicked state to the whole selection.</summary>
    private void OnFlagClick(object sender, RoutedEventArgs args)
    {
        if (model == null || sender is not CheckBox box || box.DataContext is not PropertyRow row) return;
        args.Handled = true;
        // A failed write keeps the rows, so the box shows the stored state again.
        if (!model.SetFlag(row, row.Flag != true)) box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private void OnDeleteRow(object sender, RoutedEventArgs args)
    {
        if (model != null && RowOf(sender) is PropertyRow row) model.DeleteRow(row);
    }

    private void OnEditRow(object sender, RoutedEventArgs args)
    {
        if (model != null && RowOf(sender) is PropertyRow row) model.EditRow(row);
    }

    /// <summary>«+ Добавить»: the key cell of the new row gets the focus for renaming.</summary>
    private void OnAddRow(object sender, RoutedEventArgs args)
    {
        if (model != null && RowOf(sender) is PropertyRow row && model.AddRow(row)) FocusEditorLater();
    }

    /// <summary>«+ Параметр»: a menu of the parameters of the schema that are not shown yet (TZXDATA 8.4).</summary>
    private void OnAddParameter(object sender, RoutedEventArgs args)
    {
        if (model == null) return;
        var grid = model;
        ParameterMenu.Items.Clear();
        foreach (var option in grid.AvailableParameters)
        {
            // A text block keeps the underscores of the ids, which a string header takes for access keys.
            var item = new MenuItem { Header = new TextBlock { Text = option.Caption }, Tag = option.Key };
            item.Click += (_, _) => { if (grid.AddParameter(option.Key)) FocusEditorLater(); };
            ParameterMenu.Items.Add(item);
        }
        if (ParameterMenu.Items.Count == 0) ParameterMenu.Items.Add(new MenuItem { Header = "Все параметры схемы уже показаны", IsEnabled = false });
        ParameterMenu.PlacementTarget = AddParameterButton;
        ParameterMenu.IsOpen = true;
    }

    /// <summary>
    /// «Шаблон ▾» and «Назначить шаблон» (TZXDATA 6.4, 6.6, 6.2): the templates of the schema that suit the selected
    /// entity types, «Без шаблона» and «Зафиксировать значения по умолчанию»; each item is one transaction.
    /// </summary>
    private void OnTemplateMenu(object sender, RoutedEventArgs args)
    {
        if (model == null) return;
        var grid = model;
        TemplateMenu.Items.Clear();
        foreach (var option in grid.AvailableTemplates)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = option.Caption }, Tag = option.Key,
                IsChecked = string.Equals(grid.TemplateKey, option.Key, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => grid.AssignTemplate(option.Key);
            TemplateMenu.Items.Add(item);
        }
        if (TemplateMenu.Items.Count == 0)
            TemplateMenu.Items.Add(new MenuItem { Header = grid.CanEdit ? "Нет шаблонов для выбранных объектов" : "Только чтение", IsEnabled = false });
        TemplateMenu.Items.Add(new Separator());
        var none = new MenuItem { Header = "Без шаблона", Tag = "", IsEnabled = grid.CanEdit && grid.HasTemplate };
        none.Click += (_, _) => grid.AssignTemplate("");
        TemplateMenu.Items.Add(none);
        var defaults = new MenuItem { Header = "Зафиксировать значения по умолчанию", Tag = "defaults", IsEnabled = grid.HasDefaults };
        defaults.Click += (_, _) => grid.CommitDefaults();
        TemplateMenu.Items.Add(defaults);
        TemplateMenu.PlacementTarget = (UIElement)sender;
        // «Назначить шаблон» is at the right edge of the panel: the menu opens leftwards under it and stays inside the palette.
        TemplateMenu.Placement = ReferenceEquals(sender, AssignTemplateButton) ? PlacementMode.Custom : PlacementMode.Bottom;
        TemplateMenu.CustomPopupPlacementCallback = (popup, target, _) =>
            new[] { new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height), PopupPrimaryAxis.Horizontal) };
        TemplateMenu.IsOpen = true;
    }

    private static PropertyRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as PropertyRow;

    /// <summary>The text box or list of the value column that contains <paramref name="source"/>.</summary>
    private Control? EditorOf(DependencyObject? source)
    {
        Control? editor = null;
        foreach (var element in Up(source))
        {
            if (element is ListViewItem || ReferenceEquals(element, Rows)) break;
            if (element is TextBox || element is ComboBox) editor = (Control)element;
        }
        return editor;
    }

    private static IEnumerable<DependencyObject> Up(DependencyObject? element)
    {
        for (; element != null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            yield return element;
    }

    /// <summary>Moves the focus to the editor of the selected row; false when the row has no text editor or list.</summary>
    private bool FocusEditor(PropertyRow? row)
    {
        if (row == null || !(row.IsTextEditor || row.IsChoiceEditor)) return false;
        Rows.ScrollIntoView(row);
        Rows.UpdateLayout();
        if (!(Rows.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem item)) return false;
        var cell = Find<FrameworkElement>(item, element => Equals(element.Tag, "ValueCell"));
        if (cell == null) return false;
        Control? editor = row.IsChoiceEditor ? Find<ComboBox>(cell) : Find<TextBox>(cell);
        if (editor == null || !editor.IsVisible) return false;
        var text = editor as TextBox ?? (editor as ComboBox)?.Template.FindName("PART_EditableTextBox", editor) as TextBox;
        (text ?? editor).Focus();
        text?.SelectAll();
        return editor.IsKeyboardFocusWithin;
    }

    /// <summary>After a write the rows are rebuilt; the selected row gets the keyboard focus back once they are shown.</summary>
    private void FocusRowLater() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        if (Rows.SelectedItem == null) { Rows.Focus(); return; }
        Rows.ScrollIntoView(Rows.SelectedItem);
        Rows.UpdateLayout();
        if (Rows.ItemContainerGenerator.ContainerFromItem(Rows.SelectedItem) is ListViewItem item) item.Focus();
        else Rows.Focus();
    }));

    private void FocusEditorLater() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusEditor(Rows.SelectedItem as PropertyRow)));

    private void OnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (model == null || args.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(Rows, source) is ListViewItem { DataContext: PropertyRow row } && row.IsExpandable)
        {
            model.Toggle(row);
            args.Handled = true;
        }
    }

    private static T? Find<T>(DependencyObject parent, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found && (match == null || match(found))) return found;
            var nested = Find(child, match);
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
