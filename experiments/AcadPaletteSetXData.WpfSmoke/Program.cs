using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.UI;

namespace AcadPaletteSetXData.WpfSmoke;

/// <summary>Exercises the real WPF controls without AutoCAD and renders review images.</summary>
internal static class Program
{
    private static readonly string SchemaPath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (typeof(PaletteView).Assembly.GetName().Name == "acadPaletteSetXDATA")
                AutoCadMetadataStub.Install();
            var application = new Application();
            var bindingErrors = new BindingErrorListener();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            var model = new PaletteViewModel();
            var source = new MemorySelection();
            using var controller = new SelectionController(source, model.Selection);
            using var view = new PaletteView(model);
            var window = new Window
            {
                Title = "Предпросмотр панели XDATA", Content = view, Width = 520, Height = 760
            };
            if (args.Contains("--interactive"))
            {
                source.Select(SampleSelection());
                application.Run(window);
                return 0;
            }

            var output = args.Length > 0 ? args[0] : "artifacts/screenshots";
            Directory.CreateDirectory(output);
            window.Show();
            var tabs = (TabControl)view.FindName("Tabs");
            Check(tabs.TabStripPlacement == Dock.Right, "Tabs must be on the right.");
            Check(tabs.Items.Count == 3, "Expected three tabs.");
            Pump(view);
            VerifyEmpty(view, model, output);
            source.Select(SampleSelection());
            Pump(view);
            VerifyThemes(view, model, tabs, output);
            VerifyTree(view, model);
            VerifyToolbar(view, model, source, output);
            VerifyColumns(view, model);
            VerifyVirtualization(view, model, source, output);
            VerifyTemplateOffer(view, model, source, output);
            VerifyLegacy(view, model, tabs, source, output);
            source.Select(SampleSelection());
            VerifyMinimum(window, view, model, tabs, output);
            Check(bindingErrors.Messages.Count == 0, "WPF binding failures: " + string.Join("\n", bindingErrors.Messages));
            window.Close();
            view.Dispose();
            model.Theme = PaletteTheme.Light;
            Check(view.DataContext == null, "Disposal must detach the ViewModel.");
            application.Shutdown();
            Console.WriteLine("WPF checks passed: VST columns and states, «Разное», table rows, control panel, filter, column menu, " +
                              "virtualization, kept expansion and scroll, template offer, legacy data, XDATA tree, themes, resize and disposal.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    /// <summary>TZXDATA 8.5: no selection — the VST is disabled and empty.</summary>
    private static void VerifyEmpty(PaletteView view, PaletteViewModel model, string output)
    {
        var grid = Grid(view);
        var list = List(grid);
        Check(ReferenceEquals(grid.DataContext, model.Grid), "The «Свойства» tab must show the VST model.");
        Check(!list.IsEnabled && list.Items.Count == 0, "Without a selection the VST must be disabled and empty.");
        Check(((TextBlock)grid.FindName("MessageText")).Text == "Выберите объекты в чертеже" && ((TextBlock)grid.FindName("MessageText")).IsVisible,
            "The empty selection must explain what to do.");
        Save(view, Path.Combine(output, "tzxdata-stage5-empty.png"));
    }

    private static void VerifyThemes(PaletteView view, PaletteViewModel model, TabControl tabs, string output)
    {
        foreach (var theme in new[] { PaletteTheme.Dark, PaletteTheme.Light })
        {
            model.Theme = theme;
            Pump(view);
            var expectedBackground = theme == PaletteTheme.Dark ? "#FF3D4451" : "#FFF5F6F8";
            Check(((SolidColorBrush)view.Background).Color.ToString() == expectedBackground, "The WPF background must follow the theme.");
            for (var index = 0; index < model.Tabs.Count; index++)
            {
                // Set the control selection to exercise the two-way binding used by a click.
                tabs.SelectedIndex = index;
                Pump(view);
                Check(model.SelectedTabIndex == index, "Selection must update the ViewModel.");
                if (index != 0)
                    Check(Descendants<TextBlock>(view).Any(text => text.Text == model.Tabs[index].Title),
                        "The selected tab content must be displayed.");
                var previousY = -1.0;
                for (var tabIndex = 0; tabIndex < tabs.Items.Count; tabIndex++)
                {
                    var item = (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(tabIndex);
                    var location = item.TranslatePoint(new Point(), view);
                    Check(location.X > view.ActualWidth / 2, "The tab strip must be at the right edge.");
                    Check(location.Y > previousY, "Tab headers must be stacked vertically.");
                    Check(item.ActualWidth < 50 && item.ActualHeight > 65, "Tab labels must have vertical geometry.");
                    previousY = location.Y;
                }
            }
            tabs.SelectedIndex = 0;
            Pump(view);
            var grid = Grid(view);
            var list = List(grid);
            Check(list.IsEnabled && list.Items.Count == model.Grid.Rows.Count && list.Items.Count > 10, "The selection must fill the VST.");
            var foreground = Resource(view, "PaletteForeground");
            var muted = Resource(view, "PaletteMuted");
            var power = Cell(list, "Мощность, кВт");
            Check(Fore(power) == foreground, "Captions must follow the theme: " + theme);
            Check(Descendants<TextBlock>(list).Any(text => text.Text == "Разное" && text.FontStyle == FontStyles.Italic && Fore(text) == muted),
                "Different values must show the grey italic «Разное» placeholder.");
            Check(Descendants<TextBlock>(list).Any(text => text.Text == "Электрика" && text.FontStyle == FontStyles.Italic),
                "Group names must be italic.");
            foreach (var symbol in new[] { "●", "○", "ƒ" })
                Check(Descendants<TextBlock>(list).Any(text => text.Text == symbol && text.IsVisible), "The state column must show " + symbol);
            Check(Fore(Descendants<TextBlock>(list).First(text => text.Text == "○")) == muted, "Defaults must be muted.");
            Check(Descendants<CheckBox>(list).Any(flag => flag.IsVisible && flag.IsChecked == null) &&
                  Descendants<CheckBox>(list).Any(flag => flag.IsVisible && flag.IsChecked == true),
                "The Boolean column of table rows must show three-state flags.");
            Check(Descendants<Button>(list).Count(button => AutomationProperties.GetName(button) == "Удалить строку" && button.IsVisible && !button.IsEnabled) >= 2,
                "Table rows must show the reserved × ✎ actions.");
            Check(Descendants<Button>(list).Any(button => AutomationProperties.GetName(button) == "Добавить строку" && button.IsVisible),
                "A table of the schema must offer «+ Добавить».");
            Check(Descendants<TextBlock>(list).Any(text => text.Text == "по умолчанию"), "Template defaults must be noted.");
            var header = Descendants<GridViewColumnHeader>(list).First(item => (item.Content as string) == "ПАРАМЕТР");
            Check(Fore(header) == muted, "Column headers must follow the theme.");
            Save(view, Path.Combine(output, $"tzxdata-stage5-{theme.ToString().ToLowerInvariant()}.png"));
        }
        model.Theme = PaletteTheme.Dark;
        Pump(view);
    }

    /// <summary>▸/▾, keyboard and expansion kept for the template.</summary>
    private static void VerifyTree(PaletteView view, PaletteViewModel model)
    {
        var list = List(Grid(view));
        var group = model.Grid.Rows.First(row => row.Caption == "Электрика");
        var before = list.Items.Count;
        Invoke(Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, group) && AutomationProperties.GetName(button) == "Развернуть или свернуть"), view);
        Check(!group.IsExpanded && list.Items.Count == before - group.Children.Count, "▸/▾ must collapse a group in place.");
        Check(Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, group) && AutomationProperties.GetName(button) == "Развернуть или свернуть").Content as string == "▸",
            "A collapsed group must show ▸.");
        list.SelectedItem = group;
        Pump(view);
        Press((ListViewItem)list.ItemContainerGenerator.ContainerFromItem(group), Key.Right, view);
        Check(group.IsExpanded && list.Items.Count == before, "→ must expand the selected group.");
        Press((ListViewItem)list.ItemContainerGenerator.ContainerFromItem(group), Key.Left, view);
        Check(!group.IsExpanded, "← must collapse the selected group.");
        var leaf = Descendants<TextBlock>(list).Any(text => text.Text == "Мощность, кВт");
        Check(!leaf, "Children of a collapsed group must not be shown.");
        // Another template and back: the collapsed group stays collapsed (TZXDATA 8.5).
        model.Selection.Apply(new[] { Object("K1", "CABLE", "Polyline", Pair("Cable_Mark", "ВВГнг")) }, cache: Cache());
        Pump(view);
        Check(model.Grid.TemplateText == "Кабель", "The template indicator must follow the selection.");
        model.Selection.RequestRefresh();
        Pump(view);
        group = model.Grid.Rows.First(row => row.Caption == "Электрика");
        Check(!group.IsExpanded, "Expansion must be kept per template.");
        model.Grid.Toggle(group);
        Pump(view);
    }

    private static void VerifyToolbar(PaletteView view, PaletteViewModel model, MemorySelection source, string output)
    {
        var grid = Grid(view);
        var list = List(grid);
        var toolbar = (FrameworkElement)grid.FindName("Toolbar");
        Check(Math.Abs(toolbar.ActualHeight - 32) < 0.5, "The control panel must reserve 32 px.");
        Check(Descendants<TextBlock>((DependencyObject)grid.FindName("TemplateIndicator")).Any(text => text.Text.Contains("Устройство")),
            "The template indicator must show the template of the selection.");
        Check(!((Button)grid.FindName("AddParameterButton")).IsEnabled && !((Button)grid.FindName("SchemaButton")).IsEnabled,
            "«+ Параметр» and «⚙ Схема» are reserved for later stages.");
        var reads = source.Reads;
        Invoke((Button)grid.FindName("RefreshButton"), view);
        Check(source.Reads == reads + 1, "⟳ must re-read the selection.");
        Invoke((Button)grid.FindName("CollapseAllButton"), view);
        Check(list.Items.Count == model.Grid.Roots.Count, "⊟ must collapse every group.");
        Invoke((Button)grid.FindName("ExpandAllButton"), view);
        Check(list.Items.Count == model.Grid.RowCount, "⊞ must expand every group.");
        var filter = (TextBox)grid.FindName("FilterField");
        var placeholder = (TextBlock)grid.FindName("FilterPlaceholder");
        Check(placeholder.IsVisible, "An empty filter must show its placeholder.");
        filter.Text = "мощн";
        Pump(view);
        Check(model.Grid.Filter == "мощн" && !placeholder.IsVisible, "Typing must filter the VST.");
        Check(list.Items.Cast<PropertyRow>().Select(row => row.Caption).SequenceEqual(new[] { "Электрика", "Мощность, кВт" }),
            "The filter must hide rows that do not match.");
        Save(view, Path.Combine(output, "tzxdata-stage5-filter.png"));
        filter.Text = "";
        Pump(view);
        Check(list.Items.Count == model.Grid.RowCount, "Clearing the filter must restore the rows.");
    }

    /// <summary>Columns 0, 3 and 4 are hidden through the header context menu (TZXDATA 8.2).</summary>
    private static void VerifyColumns(PaletteView view, PaletteViewModel model)
    {
        var grid = Grid(view);
        var list = List(grid);
        var columns = ((GridView)list.View).Columns;
        Check(columns.Count == 5, "The VST must have the five columns of TZXDATA 8.2.");
        var menu = (ContextMenu)grid.FindName("ColumnMenu");
        Check(ReferenceEquals(((GridView)list.View).ColumnHeaderContextMenu, menu) && ReferenceEquals(menu.DataContext, model.Grid),
            "The header context menu must control the columns.");
        foreach (var (name, check) in new (string, Func<bool>)[]
                 {
                     ("ActionsColumnItem", () => model.Grid.ShowActionsColumn), ("StateColumnItem", () => model.Grid.ShowStateColumn),
                     ("NoteColumnItem", () => model.Grid.ShowNoteColumn)
                 })
        {
            var item = (MenuItem)grid.FindName(name);
            Check(item.IsCheckable && item.IsChecked, "Column menu items must start checked: " + name);
            item.IsChecked = false;
            Pump(view);
            Check(!check() && columns.Count == 4, "Unchecking must hide the column: " + name);
            item.IsChecked = true;
            Pump(view);
            Check(check() && columns.Count == 5, "Checking must show the column again: " + name);
        }
        columns[1].Width = 170;
        Pump(view);
        Check(model.Grid.ColumnWidths[1] == 170, "Dragged widths must be kept for the session.");
        columns[1].Width = 150;
    }

    /// <summary>TZXDATA 10.5: only visible rows get containers; the scroll position is kept per template.</summary>
    private static void VerifyVirtualization(PaletteView view, PaletteViewModel model, MemorySelection source, string output)
    {
        var rows = Enumerable.Range(1, 400).Select(index => (index % 3 == 0 ? "Concrete" : "LineFittings", "M" + index.ToString("000"), (double)index, index % 2 == 0, "")).ToArray();
        var large = new[] { Object("L1", "DEVICE", "BlockReference", Pair("Power", 5.5), Materials(rows)) };
        source.Select(large);
        Pump(view);
        var grid = Grid(view);
        var list = List(grid);
        var containers = Descendants<ListViewItem>(list).Count();
        Check(list.Items.Count > 400 && containers > 0 && containers < list.Items.Count,
            $"The VST must be virtualized: {containers} containers for {list.Items.Count} rows.");
        Check(VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling && VirtualizingPanel.GetIsVirtualizing(list),
            "The VST must recycle containers.");
        var scroll = grid.RowsScroll!;
        Check(scroll.ExtentHeight > scroll.ViewportHeight, "A long selection must scroll.");
        scroll.ScrollToVerticalOffset(150);
        Pump(view);
        Check(Math.Abs(model.Grid.ScrollOffset - scroll.VerticalOffset) < 0.5 && scroll.VerticalOffset > 100,
            "Scrolling must be saved for the template.");
        Check(Descendants<ListViewItem>(list).Count() < list.Items.Count, "Scrolling must keep the list virtualized.");
        Save(view, Path.Combine(output, "tzxdata-stage5-virtualized.png"));
        var saved = scroll.VerticalOffset;
        source.Select(new[] { Object("K1", "CABLE", "Polyline", Pair("Cable_Mark", "ВВГнг")) });
        Pump(view);
        Check(scroll.VerticalOffset < 1, "Another template must start at its own position.");
        source.Select(large);
        Pump(view);
        Pump(view);
        Check(Math.Abs(scroll.VerticalOffset - saved) < 0.5, $"The scroll position must come back for the same template: {scroll.VerticalOffset} ≠ {saved}.");
        Console.WriteLine(model.Grid.BuildSummary);
        source.Select(SampleSelection());
        Pump(view);
    }

    /// <summary>TZXDATA 8.5: objects without BobrovXDATA get the «Назначить шаблон» offer (stage 7).</summary>
    private static void VerifyTemplateOffer(PaletteView view, PaletteViewModel model, MemorySelection source, string output)
    {
        source.Select(new[] { new EntityDataSnapshot("C3", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>()) });
        Pump(view);
        var grid = Grid(view);
        var assign = (Button)grid.FindName("AssignTemplateButton");
        Check(model.Grid.NeedsTemplate && assign.IsVisible && !assign.IsEnabled, "«Назначить шаблон» must be offered and reserved.");
        Check(((TextBlock)grid.FindName("MessageText")).Text.Contains("назначьте шаблон"), "The message must explain the offer.");
        Save(view, Path.Combine(output, "tzxdata-stage5-no-template.png"));
        source.Select(SampleSelection());
        Pump(view);
        Check(!assign.IsVisible, "Objects with data must not offer a template.");
    }

    /// <summary>Data of the former format stays readable; the XDATA tree tab is unchanged (stage 3).</summary>
    private static void VerifyLegacy(PaletteView view, PaletteViewModel model, TabControl tabs, MemorySelection source, string output)
    {
        source.Select(LegacySelection());
        Pump(view);
        Invoke((Button)Grid(view).FindName("ExpandAllButton"), view);
        var list = List(Grid(view));
        Check(Descendants<TextBlock>(list).Any(text => text.Text == PropertyGridViewModel.GroupLegacy), "The legacy group must be shown.");
        // CD35 is in the ESMT data of A1 and in the XML XRecord of B2; СВ110-5 of A1 alone is «Разное».
        Check(Descendants<TextBlock>(list).Any(text => text.Text == "CD35"), "Legacy materials must be table rows.");
        Save(view, Path.Combine(output, "tzxdata-stage5-legacy.png"));
        var selector = (ComboBox)view.FindName("SelectedEntityField");
        selector.SelectedIndex = 0;
        tabs.SelectedIndex = 1;
        Pump(view);
        Check(selector.Items.Count == 2 && Descendants<TextBlock>(selector).Any(text => text.Text == "BlockReference [A1]"),
            "Every selected entity must be inspectable.");
        var tree = Descendants<TreeView>(view).Single();
        Check(tree.Items.Count == 1 && tree.IsEnabled && Descendants<TextBlock>(tree).Any(text => text.Text.Contains("ESMT_LEP_v1.0")),
            "The selected entity must expose a navigable XDATA tree.");
        selector.SelectedIndex = 1;
        Pump(view);
        Check(Descendants<TextBlock>(tree).Any(text => text.Text == "Name: Кабель"), "Parsed XML must render as structured tree nodes.");
        tabs.SelectedIndex = 0;
        Pump(view);
        Check(model.Grid.Rows.Count > 0 && List(Grid(view)).Items.Count == model.Grid.Rows.Count, "Switching tabs must keep the VST.");
    }

    private static void VerifyMinimum(Window window, PaletteView view, PaletteViewModel model, TabControl tabs, string output)
    {
        // Exercise the minimum supported palette size as well as the normal render.
        window.Width = 280;
        window.Height = 320;
        Pump(view);
        Check(view.ActualWidth > 0 && tabs.ActualWidth <= view.ActualWidth, "The shell must fit the minimum palette width.");
        var grid = Grid(view);
        var filter = (TextBox)grid.FindName("FilterField");
        Check(filter.ActualWidth >= 40, "The filter must stay usable at minimum width.");
        var scroll = grid.RowsScroll!;
        Check(scroll.ExtentWidth > scroll.ViewportWidth, "Narrow palettes must scroll the VST horizontally.");
        scroll.ScrollToRightEnd();
        Pump(view);
        Check(scroll.HorizontalOffset > 0, "The last VST column must be reachable.");
        Save(view, Path.Combine(output, "tzxdata-stage5-minimum.png"));
        scroll.ScrollToLeftEnd();
        window.Width = 520;
        window.Height = 760;
        Pump(view);
        Check(model.Grid.IsEnabled, "Resizing must keep the selection.");
    }

    private static PropertyGridView Grid(PaletteView view) => Descendants<PropertyGridView>(view).Single();

    private static ListView List(PropertyGridView grid) => (ListView)grid.FindName("Rows");

    private static TextBlock Cell(ListView list, string text) => Descendants<TextBlock>(list).First(block => block.Text == text);

    private static Color Resource(FrameworkElement view, string key) => ((SolidColorBrush)view.FindResource(key)).Color;

    private static Color Fore(TextBlock text) => ((SolidColorBrush)text.Foreground).Color;

    private static Color Fore(Control control) => ((SolidColorBrush)control.Foreground).Color;

    private static ObjectValuesCache Cache()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(SchemaPath));
        Check(result.Schema != null && !result.HasErrors, "The example schema must load.");
        return new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
    }

    /// <summary>Two devices: shared, different, default and calculated values, a table with categories and an unknown Id.</summary>
    private static EntityDataSnapshot[] SampleSelection() => new[]
    {
        Object("A1", "DEVICE", "BlockReference", Pair("Device_Type", "Щит"), Pair("NMO_Name", "Щит учёта"), Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "3"),
            Pair("Power", 2.5), Pair("Phase", "A"),
            Materials(("LineFittings", "CD35", 2.0, true, "Пример"), ("LineFittings", "CS 10.3", 1.0, true, ""), ("Concrete", "СВ110-5", 1.0, false, ""))),
        Object("B2", "DEVICE", "BlockReference", Pair("Device_Type", "Щит"), Pair("NMO_Name", "Щит освещения"), Pair("GC_HeadDevice", "ЩУ1"), Pair("GC_HDGroup", "4"),
            Pair("Power", 2.5), Pair("Phase", "B"), Pair("Old_Param", "из старой схемы"),
            Materials(("LineFittings", "CD35", 2.0, true, "Пример"), ("Concrete", "СВ110-5", 1.0, true, "")))
    };

    private static EntityDataSnapshot[] LegacySelection()
    {
        return new[]
        {
            new EntityDataSnapshot("A1", "BlockReference", new[]
            {
                V(1001, "ESMT_LEP_v1.0"), V(1000, "Type=Опора 0,4 кВ"), V(1000, "Number=021"), V(1000, "Name=АО21"),
                V(1000, "Title=Опора"), V(1000, "ProjectReference=Проект 021 / лист 1"),
                V(1000, "Material"), V(1002, "{"), V(1000, "Category=Железобетонные элементы"),
                V(1000, "Name=СВ110-5"), V(1000, "Count=1"), V(1000, "IsInSpec=1"), V(1000, "Comment=Основной"), V(1002, "}"),
                V(1000, "Material"), V(1002, "{"), V(1000, "Category=Линейная арматура"),
                V(1000, "Name=CD35"), V(1000, "Count=2"), V(1000, "IsInSpec=0"), V(1002, "}")
            }, Array.Empty<DataRecord>()),
            new EntityDataSnapshot("B2", "Polyline", new[] { V(1001, "BobrovXDATA"), V(1000, "Number=022") }, new[]
            {
                new DataRecord("BobrovXDATA/Properties", new[] { V(1, "<VisualTreeString><Properties><Type>Кабель</Type><Name>Кабель</Name></Properties><Materials><Material Category='Линейная арматура' Name='CD35' Count='2' IsInSpec='true'/></Materials></VisualTreeString>") })
            })
        };
    }

    private static EntityDataSnapshot Object(string handle, string template, string entityType, params DataValue[][] pairs)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)2) };
        xdata.AddRange(Pair(XDataV2.TemplateKey, template));
        foreach (var pair in pairs) xdata.AddRange(pair);
        return new EntityDataSnapshot(handle, entityType, xdata.ToArray(), Array.Empty<DataRecord>());
    }

    private static DataValue[] Pair(string id, object value) => new[]
    {
        V(1000, id),
        value switch
        {
            string text => V(1000, text),
            double number => V(1040, number),
            int integer => V(1071, integer),
            short flag => V(1070, flag),
            _ => throw new ArgumentException(value.GetType().Name)
        }
    };

    private static DataValue[] Materials(params (string Category, string Mark, double Count, bool InSpec, string Comment)[] rows)
    {
        var list = new List<DataValue> { V(1000, "Materials"), V(1002, "{") };
        foreach (var row in rows)
        {
            list.Add(V(1002, "{"));
            list.AddRange(Pair("Mat_Category", row.Category));
            list.AddRange(Pair("Mat_Mark", row.Mark));
            list.AddRange(Pair("Mat_Count", row.Count));
            list.AddRange(Pair("Mat_InSpec", (short)(row.InSpec ? 1 : 0)));
            if (row.Comment.Length != 0) list.AddRange(Pair("Mat_Comment", row.Comment));
            list.Add(V(1002, "}"));
        }
        list.Add(V(1002, "}"));
        return list.ToArray();
    }

    private static DataValue V(int code, object value) => new DataValue(code, value);

    /// <summary>The selection of a drawing with the example schema in its NOD.</summary>
    private sealed class MemorySelection : ISelectionSource, IObjectValuesSource
    {
        private EntityDataSnapshot[] snapshots = Array.Empty<EntityDataSnapshot>();
        public int Reads { get; private set; }
        public ObjectValuesCache? Values { get; } = Cache();
        public event EventHandler? SelectionChanged;
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() { Reads++; return snapshots; }

        public void Select(EntityDataSnapshot[] next)
        {
            snapshots = next;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Press(FrameworkElement control, Key key, FrameworkElement view)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        control.RaiseEvent(args); Pump(view);
    }

    private static void Invoke(Button control, FrameworkElement view)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(control).GetPattern(PatternInterface.Invoke)).Invoke();
        Pump(view);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = new List<string>();
        public override void Write(string? message) { if (message != null) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }

    private static void Pump(FrameworkElement view)
    {
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Save(FrameworkElement view, string path)
    {
        Pump(view);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
