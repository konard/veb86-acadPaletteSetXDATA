using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using AcadPaletteSetXData.Core;
using Microsoft.Win32;

namespace AcadPaletteSetXData.UI;

/// <summary>
/// The «Навигатор» tab (issue #66): the profile bar and one virtualized multi-column tree over
/// <see cref="NavigatorViewModel.Rows"/>. The first column is the tree; the other columns come from the active profile.
/// The view maps the headers to the model: a drag changes the order of the profile, a resize its widths, a click the
/// sort and the header menu the visibility. Only visibility rebuilds the columns; nothing here rebuilds the data.
/// </summary>
public partial class NavigatorView : UserControl
{
    private const string ProfileFileFilter = "Профиль навигатора (*.json)|*.json";
    private readonly Dictionary<NavigatorColumnViewModel, GridViewColumn> shown = new Dictionary<NavigatorColumnViewModel, GridViewColumn>();
    private NavigatorViewModel? model;
    private ScrollViewer? scroll;
    private bool building;
    private bool reorderPending;

    public NavigatorView()
    {
        InitializeComponent();
        ((INotifyPropertyChanged)TreeColumn).PropertyChanged += OnTreeColumnChanged;
        Columns.Columns.CollectionChanged += OnColumnsMoved;
        DataContextChanged += (_, _) => Attach(DataContext as NavigatorViewModel);
        Loaded += (_, _) => Attach(DataContext as NavigatorViewModel);
        Unloaded += (_, _) => Attach(null);
        Rows.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        Rows.PreviewKeyDown += OnKey;
        Rows.MouseDoubleClick += OnDoubleClick;
        Rows.PreviewMouseLeftButtonDown += OnRowMouseDown;
        Rows.ContextMenuOpening += OnRowMenuOpening;
        ColumnMenu.Opened += (_, _) => FillColumnMenu();
    }

    /// <summary>Margin of the tree cell: <see cref="NavigatorRow.Indent"/> pixels on the left.</summary>
    public static IValueConverter Indent { get; } = new LeftMargin();

    /// <summary>
    /// Confirms profile deletion or replacement. The WPF smoke test replaces the message box.
    /// </summary>
    public static Func<string, bool> ConfirmDialog { get; set; } = text =>
        MessageBox.Show(text, "Навигатор", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>«Сохранить изменения профиля?»: yes, no or cancel (null).</summary>
    public static Func<string, bool?> AskSaveDialog { get; set; } = text =>
        MessageBox.Show(text, "Навигатор", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null
        };

    /// <summary>Shows the JSON file picker. The WPF smoke test supplies a path here.</summary>
    public static Func<FileDialog, Window?, bool?> ShowProfileFileDialog { get; set; } = (dialog, owner) =>
        owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

    /// <summary>The scroll host of the rows, available after the list is loaded.</summary>
    public ScrollViewer? RowsScroll => scroll ??= Find<ScrollViewer>(Rows);

    /// <summary>The data columns shown, in the order of the view (the tree column excluded).</summary>
    public IReadOnlyList<GridViewColumn> DataColumns => Columns.Columns.Where(column => !ReferenceEquals(column, TreeColumn)).ToList();

    private void Attach(NavigatorViewModel? next)
    {
        if (ReferenceEquals(model, next)) return;
        if (model != null)
        {
            model.PropertyChanged -= OnModelChanged;
            model.ColumnsChanged -= OnModelColumnsChanged;
            ((INotifyCollectionChanged)model.Rows).CollectionChanged -= OnRowsChanged;
            model.EditProfileWindow = null;
            model.Confirm = null;
            model.AskSave = null;
        }
        model = next;
        if (model == null) return;
        model.EditProfileWindow = editor => NavigatorProfileWindow.Edit(editor, Theme, this);
        model.Confirm = text => ConfirmDialog(text);
        model.AskSave = text => AskSaveDialog(text);
        model.PropertyChanged += OnModelChanged;
        model.ColumnsChanged += OnModelColumnsChanged;
        ((INotifyCollectionChanged)model.Rows).CollectionChanged += OnRowsChanged;
        model.EnsureLoaded();
        BuildColumns();
    }

    private PaletteTheme Theme => Up(this).OfType<PaletteView>().FirstOrDefault()?.Theme ?? PaletteTheme.Dark;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (model == null) return;
        switch (args.PropertyName)
        {
            case nameof(NavigatorViewModel.TreeWidth):
                if (Math.Abs(TreeColumn.Width - model.TreeWidth) > 0.5) TreeColumn.Width = model.TreeWidth;
                break;
            case nameof(NavigatorViewModel.TreeSortGlyph):
                TreeColumn.Header = TreeHeader;
                break;
        }
    }

    private string TreeHeader => "Объект" + (model?.TreeSortGlyph ?? "");

    private void OnModelColumnsChanged(object? sender, EventArgs args) => BuildColumns();

    /// <summary>The columns of the profile: the tree column first, then the visible columns in the order of the profile.</summary>
    private void BuildColumns()
    {
        if (model == null) return;
        building = true;
        try
        {
            foreach (var pair in shown) ((INotifyPropertyChanged)pair.Value).PropertyChanged -= OnDataColumnChanged;
            shown.Clear();
            Columns.Columns.Clear();
            TreeColumn.Width = model.TreeWidth;
            TreeColumn.Header = TreeHeader;
            Columns.Columns.Add(TreeColumn);
            foreach (var source in model.VisibleColumns)
            {
                var column = new GridViewColumn { Width = source.Width, CellTemplate = CellTemplate(source.Slot) };
                BindingOperations.SetBinding(column, GridViewColumn.HeaderProperty, new Binding(nameof(NavigatorColumnViewModel.Header)) { Source = source, Mode = BindingMode.OneWay });
                ((INotifyPropertyChanged)column).PropertyChanged += OnDataColumnChanged;
                shown[source] = column;
                Columns.Columns.Add(column);
            }
        }
        finally { building = false; }
    }

    private NavigatorColumnViewModel? SourceOf(GridViewColumn column) => shown.FirstOrDefault(pair => ReferenceEquals(pair.Value, column)).Key;

    /// <summary>
    /// The cell of a data column: the text of <c>Cells[slot]</c>; an error is red with its message as a tooltip, an
    /// unknown parameter is grey. Groups have no values: their cells are empty.
    /// </summary>
    private static DataTemplate CellTemplate(int slot)
    {
        var cell = "Cells[" + slot.ToString(CultureInfo.InvariantCulture) + "]";
        var xaml =
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<TextBlock Text='{Binding " + cell + ".Text, Mode=OneTime}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'>" +
            "<TextBlock.Style><Style TargetType='TextBlock'><Style.Triggers>" +
            "<DataTrigger Binding='{Binding " + cell + ".IsError, Mode=OneTime}' Value='True'>" +
            "<Setter Property='Foreground' Value='{DynamicResource PaletteError}' />" +
            "<Setter Property='ToolTip' Value='{Binding " + cell + ".Error, Mode=OneTime}' /></DataTrigger>" +
            "<DataTrigger Binding='{Binding " + cell + ".IsUnknown, Mode=OneTime}' Value='True'>" +
            "<Setter Property='Foreground' Value='{DynamicResource PaletteMuted}' />" +
            "<Setter Property='ToolTip' Value='{Binding " + cell + ".Error, Mode=OneTime}' /></DataTrigger>" +
            "</Style.Triggers></Style></TextBlock.Style></TextBlock></DataTemplate>";
        return (DataTemplate)XamlReader.Parse(xaml);
    }

    // ---- widths, order, sort and visibility of columns ----

    private void OnTreeColumnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (building || model == null || args.PropertyName != nameof(GridViewColumn.Width) || double.IsNaN(TreeColumn.Width)) return;
        model.TreeWidth = TreeColumn.Width;
    }

    private void OnDataColumnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (building || args.PropertyName != nameof(GridViewColumn.Width) || sender is not GridViewColumn column || double.IsNaN(column.Width)) return;
        var source = SourceOf(column);
        if (source != null) source.Width = column.Width;
    }

    /// <summary>A header was dragged: the tree column stays first and the profile takes the order of the other columns.</summary>
    private void OnColumnsMoved(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (building || model == null || args.Action != NotifyCollectionChangedAction.Move || reorderPending) return;
        // The collection cannot be changed while it reports a change.
        reorderPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyOrder));
    }

    /// <summary>Applies the order of the headers to the profile; used after a drag and by the smoke test.</summary>
    public void ApplyOrder()
    {
        reorderPending = false;
        if (model == null) return;
        building = true;
        try
        {
            var index = Columns.Columns.IndexOf(TreeColumn);
            if (index > 0) Columns.Columns.Move(index, 0);
        }
        finally { building = false; }
        var order = DataColumns.Select(SourceOf).Where(source => source != null).Cast<NavigatorColumnViewModel>().ToList();
        for (var target = 0; target < order.Count; target++)
        {
            var current = model.VisibleColumns.ToList().IndexOf(order[target]);
            if (current >= 0 && current != target) model.MoveVisibleColumn(current, target);
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs args)
    {
        if (model == null || args.OriginalSource is not GridViewColumnHeader header || header.Role == GridViewColumnHeaderRole.Padding || header.Column == null) return;
        model.SortCommand.Execute(ReferenceEquals(header.Column, TreeColumn) ? null : SourceOf(header.Column));
        args.Handled = true;
    }

    /// <summary>The header menu: a check box per column of the profile and the settings of the profile.</summary>
    private void FillColumnMenu()
    {
        ColumnMenu.Items.Clear();
        if (model == null) return;
        var target = model;
        var tree = new MenuItem { Header = "Объект (дерево)", IsCheckable = true, IsChecked = true, IsEnabled = false };
        ColumnMenu.Items.Add(tree);
        foreach (var column in target.Columns)
        {
            var item = new MenuItem { Header = column.Caption, Tag = column.Id, IsCheckable = true, IsChecked = column.Visible, StaysOpenOnClick = true };
            var source = column;
            item.Click += (_, _) => source.Visible = item.IsChecked;
            ColumnMenu.Items.Add(item);
        }
        ColumnMenu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Настроить профиль…", Tag = EditCommand };
        edit.Click += (_, _) => target.EditProfile();
        ColumnMenu.Items.Add(edit);
    }

    // ---- the ⚙ menu ----

    /// <summary><see cref="MenuItem.Tag"/> of the items of the ⚙ menu.</summary>
    public const string EditCommand = "edit";
    public const string NewCommand = "new";
    public const string DeleteCommand = "delete";
    public const string SaveCommand = "save";
    public const string ImportCommand = "import";
    public const string ExportCommand = "export";

    private void OnProfileMenu(object sender, RoutedEventArgs args)
    {
        if (model == null) return;
        FillProfileMenu();
        ProfileMenu.PlacementTarget = ProfileMenuButton;
        ProfileMenu.CustomPopupPlacementCallback = (popup, target, _) =>
            new[] { new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height), PopupPrimaryAxis.Horizontal) };
        ProfileMenu.IsOpen = true;
    }

    /// <summary>Fills the ⚙ menu (also used by the smoke test to click its items).</summary>
    public ContextMenu FillProfileMenu()
    {
        ProfileMenu.Items.Clear();
        if (model == null) return ProfileMenu;
        var target = model;
        void Add(string header, string tag, string tip, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, Tag = tag, ToolTip = tip, IsEnabled = enabled };
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += (_, _) => action();
            ProfileMenu.Items.Add(item);
        }
        Add("Настроить профиль…", EditCommand, "Название, выражение дерева и колонки активного профиля", target.Profile != null, () => target.EditProfile());
        Add("Новый профиль…", NewCommand, "Создать профиль на основе активного", true, () => target.NewProfile());
        Add("Удалить профиль…", DeleteCommand, "Удалить файл активного профиля (с подтверждением)", target.Profile != null, () => target.DeleteProfile());
        ProfileMenu.Items.Add(new Separator());
        Add("Сохранить", SaveCommand, "Сохранить ширину, порядок и видимость колонок", target.IsDirty, () => target.Save());
        ProfileMenu.Items.Add(new Separator());
        Add("Импорт JSON…", ImportCommand, "Загрузить профиль навигатора из файла JSON", true, () => ImportProfile(target));
        Add("Экспорт JSON…", ExportCommand, "Сохранить активный профиль навигатора в файл JSON", target.Profile != null, () => ExportProfile(target));
        return ProfileMenu;
    }

    private void ImportProfile(NavigatorViewModel target)
    {
        var dialog = new OpenFileDialog { Filter = ProfileFileFilter, DefaultExt = ".json", Title = "Импорт профиля навигатора" };
        if (ShowProfileFileDialog(dialog, Window.GetWindow(this)) == true) target.ImportProfile(dialog.FileName);
    }

    private void ExportProfile(NavigatorViewModel target)
    {
        var dialog = new SaveFileDialog
        {
            Filter = ProfileFileFilter,
            DefaultExt = ".json",
            Title = "Экспорт профиля навигатора",
            FileName = target.Profile?.Id + ".json"
        };
        if (ShowProfileFileDialog(dialog, Window.GetWindow(this)) == true) target.ExportProfile(dialog.FileName);
    }

    // ---- rows ----

    /// <summary>A rebuilt tree resets the list; the scroll position comes back after layout.</summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action != NotifyCollectionChangedAction.Reset || RowsScroll == null) return;
        var offset = RowsScroll.VerticalOffset;
        if (offset <= 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RowsScroll?.ScrollToVerticalOffset(offset)));
    }

    /// <summary>The row of the element under the mouse or with the focus; null for headers and the empty space.</summary>
    private NavigatorRow? RowAt(object? element) =>
        element is DependencyObject source && ItemsControl.ContainerFromElement(Rows, source) is ListViewItem { DataContext: NavigatorRow row } ? row : null;

    /// <summary>
    /// Issue #68: a click on an object selects it in AutoCAD. Choosing another row does it through
    /// <see cref="NavigatorViewModel.SelectedRow"/>; a click on the row that is already chosen selects the object again
    /// (AutoCAD may have dropped the selection since). The second click of a double click does not: it shows the object.
    /// </summary>
    private void OnRowMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (model == null || args.ClickCount > 1 || args.OriginalSource is not DependencyObject source || Up(source).OfType<ButtonBase>().Any()) return;
        if (RowAt(source) is { IsLeaf: true } row && ReferenceEquals(row, model.SelectedRow)) model.SelectCommand.Execute(row);
    }

    /// <summary><see cref="MenuItem.Tag"/> of «Выделить все устройства группы» of the menu of a group.</summary>
    public const string SelectGroupCommand = "select-group";

    /// <summary>
    /// Issue #68: the right button on a group opens its menu; objects have no menu. The header keeps its own menu of columns.
    /// </summary>
    private void OnRowMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (args.OriginalSource is DependencyObject source && Up(source).OfType<GridViewColumnHeader>().Any()) return;
        if (model == null || RowAt(args.OriginalSource) is not { IsGroup: true } row)
        {
            args.Handled = true;
            return;
        }
        FillRowMenu(row);
    }

    /// <summary>Fills the menu of a group (also used by the smoke test to click its items).</summary>
    public ContextMenu FillRowMenu(NavigatorRow row)
    {
        RowMenu.Items.Clear();
        if (model == null || row == null || !row.IsGroup) return RowMenu;
        var target = model;
        var count = row.Node.LeafCount.ToString(CultureInfo.InvariantCulture);
        var select = new MenuItem
        {
            Header = "Выделить все устройства группы (" + count + ")",
            Tag = SelectGroupCommand,
            ToolTip = "Выделить в чертеже все объекты, сгруппированные в «" + row.Node.Name + "», на всех уровнях"
        };
        select.Click += (_, _) => target.SelectGroupCommand.Execute(row);
        RowMenu.Items.Add(select);
        return RowMenu;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (model == null || args.OriginalSource is not DependencyObject source || Up(source).OfType<ButtonBase>().Any()) return;
        if (RowAt(source) is not NavigatorRow row) return;
        if (row.IsLeaf) model.ActivateCommand.Execute(row);
        else model.ToggleCommand.Execute(row);
        args.Handled = true;
    }

    /// <summary>Enter shows the object (or toggles a group), → expands, ← collapses or goes to the parent group.</summary>
    private void OnKey(object sender, KeyEventArgs args)
    {
        if (model == null || Rows.SelectedItem is not NavigatorRow row) return;
        switch (args.Key)
        {
            case Key.Enter:
                if (row.IsLeaf) model.ActivateCommand.Execute(row);
                else model.ToggleCommand.Execute(row);
                args.Handled = true;
                break;
            case Key.Right when row.HasChildren && !row.IsExpanded:
                row.IsExpanded = true;
                args.Handled = true;
                break;
            case Key.Left when row.HasChildren && row.IsExpanded:
                row.IsExpanded = false;
                args.Handled = true;
                FocusRow(row);
                break;
            case Key.Left when row.Depth > 0:
                var index = model.Rows.IndexOf(row);
                for (var i = index - 1; i >= 0; i--)
                {
                    if (model.Rows[i].Depth >= row.Depth) continue;
                    Rows.SelectedItem = model.Rows[i];
                    FocusRow(model.Rows[i]);
                    break;
                }
                args.Handled = true;
                break;
        }
    }

    private void FocusRow(NavigatorRow row) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        Rows.ScrollIntoView(row);
        if (Rows.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem item) item.Focus();
    }));

    private static IEnumerable<DependencyObject> Up(DependencyObject? element)
    {
        for (; element != null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            yield return element;
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            var nested = Find<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private sealed class LeftMargin : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new Thickness(value is double left ? left : 0, 0, 0, 0);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
