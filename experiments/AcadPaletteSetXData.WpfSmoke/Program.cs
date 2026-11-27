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
using System.Windows.Controls.Primitives;
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
    private static readonly string SystemSchemaPath = Path.Combine(AppContext.BaseDirectory, "examples", "issue48-system-group-schema.json");
    private static readonly string NavigatorSchemaPath = Path.Combine(AppContext.BaseDirectory, "examples", "navigator-schema.json");

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
            // Issue #66: the profiles of the navigator live in a temporary folder seeded with examples/navigator.
            var navigatorFolder = NavigatorFolder();
            var model = new PaletteViewModel(navigatorStore: new NavigatorProfileStore(navigatorFolder));
            var source = new MemorySelection();
            // Issue #59: the controller limits the selection by the settings of the palette, as in AutoCAD.
            using var controller = new SelectionController(source, model.Selection, model.Settings);
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
            Check(tabs.Items.Count == 4, "Expected four tabs.");
            Pump(view);
            VerifyEmpty(view, model, tabs, output);
            source.Select(SampleSelection());
            Pump(view);
            VerifyThemes(view, model, tabs, output);
            VerifyTree(view, model);
            VerifyToolbar(view, model, source, output);
            VerifyColumns(view, model);
            VerifyVirtualization(view, model, source, output);
            VerifyTemplates(view, model, source, output);
            VerifySchemaEditor(view, model, output);
            VerifySettings(view, model, source, output);
            VerifySelectionLimit(view, model, controller, source, output);
            VerifyLegacy(view, model, tabs, source, output);
            VerifyEditing(view, model, source, output);
            VerifySelectionGroups(view, model, tabs, source, output);
            VerifyEntityProperties(view, model, tabs, output);
            VerifyNavigator(view, model, tabs, navigatorFolder, output);
            source.Select(SampleSelection());
            VerifyMinimum(window, view, model, tabs, output);
            Check(bindingErrors.Messages.Count == 0, "WPF binding failures: " + string.Join("\n", bindingErrors.Messages));
            window.Close();
            view.Dispose();
            model.Theme = PaletteTheme.Light;
            Check(view.DataContext == null, "Disposal must detach the ViewModel.");
            application.Shutdown();
            Console.WriteLine("WPF checks passed: VST columns and states, «Разное», table rows, control panel, filter, column menu, " +
                              "virtualization, kept expansion and scroll, template menu, assigning and changing templates, committed defaults, legacy data, editors, Enter/Esc, check boxes, × ✎ «+ Добавить», " +
                              "«+ Параметр», write errors, selection groups with * and − (issue #41), ⚙ schema menu, " +
                              "hidden SYSTEM group and the settings window, theme background of the empty lists (issue #48), " +
                              "«Навигатор» tab (issue #66: profiles, multi-column tree, sort, column order, width and visibility, profile window, select by handle), schema editor (draft, formula preview, NCalc formulas (issue #43), guards, errors, import, apply, cancel, light theme), XDATA tree, themes, resize and disposal.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    /// <summary>TZXDATA 8.5: no selection — the VST is disabled and empty, in the theme colors (issue #48).</summary>
    private static void VerifyEmpty(PaletteView view, PaletteViewModel model, TabControl tabs, string output)
    {
        var grid = Grid(view);
        var list = List(grid);
        Check(ReferenceEquals(grid.DataContext, model.Grid), "The «Свойства» tab must show the VST model.");
        Check(!list.IsEnabled && list.Items.Count == 0, "Without a selection the VST must be disabled and empty.");
        Check(((TextBlock)grid.FindName("MessageText")).Text == "Выберите объекты в чертеже" && ((TextBlock)grid.FindName("MessageText")).IsVisible,
            "The empty selection must explain what to do.");
        Save(view, Path.Combine(output, "tzxdata-stage6-empty.png"));

        // Issue #48: the disabled empty lists keep the theme background; the system templates painted them white.
        foreach (var theme in new[] { PaletteTheme.Dark, PaletteTheme.Light })
        {
            model.Theme = theme;
            tabs.SelectedIndex = 0;
            Pump(view);
            var background = Resource(view, "PaletteBackground");
            var name = theme == PaletteTheme.Dark ? "dark" : "light";
            list = List(Grid(view));
            Check(!list.IsEnabled, "Without a selection the VST must stay disabled.");
            CheckBackground(view, list, background, "The empty VST (" + name + ")");
            Save(view, Path.Combine(output, "issue48-empty-properties-" + name + ".png"));

            tabs.SelectedIndex = 1;
            Pump(view);
            var tree = Descendants<TreeView>(view).Single();
            Check(!tree.IsEnabled && tree.Items.Count == 0, "Without a selection the XDATA tree must be disabled and empty.");
            CheckBackground(view, tree, background, "The empty XDATA tree (" + name + ")");
            Save(view, Path.Combine(output, "issue48-empty-tree-" + name + ".png"));
        }
        model.Theme = PaletteTheme.Dark;
        tabs.SelectedIndex = 0;
        Pump(view);
    }

    /// <summary>The template border and the rendered pixels of a disabled list are the theme background.</summary>
    private static void CheckBackground(FrameworkElement view, Control list, Color background, string name)
    {
        var border = (Border)list.Template.FindName("Bd", list);
        Check(border != null && border.Background is SolidColorBrush brush && brush.Color == background,
            name + " must use the theme template: " + (border?.Background?.ToString() ?? "no border"));
        foreach (var point in new[] { new Point(8, list.ActualHeight - 8), new Point(list.ActualWidth / 2, list.ActualHeight - 8), new Point(list.ActualWidth - 8, list.ActualHeight / 2) })
        {
            var pixel = Pixel(view, list, point);
            Check(pixel == background, name + " must be painted with " + background + ", not " + pixel + " at " + point + ".");
        }
    }

    /// <summary>The rendered color of <paramref name="view"/> at <paramref name="point"/> of <paramref name="element"/>.</summary>
    private static Color Pixel(FrameworkElement view, FrameworkElement element, Point point)
    {
        Pump(view);
        var at = element.TranslatePoint(point, view);
        var bitmap = Render(view);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)at.X, (int)at.Y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
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
            Check(Descendants<Button>(list).Count(button => AutomationProperties.GetName(button) == "Удалить строку" && button.IsVisible && button.IsEnabled) >= 2 &&
                  Descendants<Button>(list).Count(button => AutomationProperties.GetName(button) == "Редактировать строку" && button.IsVisible && button.IsEnabled) >= 2,
                "Table rows of a writable selection must offer × and ✎.");
            Check(Descendants<Button>(list).Any(button => AutomationProperties.GetName(button) == "Добавить строку" && button.IsVisible),
                "A table of the schema must offer «+ Добавить».");
            Check(Descendants<TextBlock>(list).Any(text => text.Text == "по умолчанию"), "Template defaults must be noted.");
            var header = Descendants<GridViewColumnHeader>(list).First(item => (item.Content as string) == "ПАРАМЕТР");
            Check(Fore(header) == muted, "Column headers must follow the theme.");
            Save(view, Path.Combine(output, $"tzxdata-stage6-{theme.ToString().ToLowerInvariant()}.png"));
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
        Check(((Button)grid.FindName("AddParameterButton")).IsEnabled && ((Button)grid.FindName("SchemaButton")).IsEnabled,
            "«+ Параметр» edits a writable selection; ⚙ opens the settings even without AutoCAD (issue #48).");
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
        Save(view, Path.Combine(output, "tzxdata-stage6-filter.png"));
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
        Save(view, Path.Combine(output, "tzxdata-stage6-virtualized.png"));
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

    /// <summary>
    /// TZXDATA 8.5, 6.4, 6.6, 6.2 (stage 7): objects without BobrovXDATA get «Назначить шаблон»; the template is assigned to the
    /// whole group in one transaction with new DEVICE_IDs, changed with the values kept, its defaults are committed.
    /// </summary>
    private static void VerifyTemplates(PaletteView view, PaletteViewModel model, MemorySelection source, string output)
    {
        source.Select(new[] { Empty("D1"), Empty("D2"), Empty("D3") });
        Pump(view);
        var grid = Grid(view);
        var assign = (Button)grid.FindName("AssignTemplateButton");
        Check(model.Grid.NeedsTemplate && assign.IsVisible && assign.IsEnabled, "«Назначить шаблон» must be offered.");
        Check(((TextBlock)grid.FindName("MessageText")).Text.Contains("назначьте шаблон"), "The message must explain the offer.");
        Save(view, Path.Combine(output, "tzxdata-stage7-no-template.png"));

        // The menu lists the templates of the block references only: CABLE and TRAY are for lines and polylines.
        Invoke(assign, view);
        var menu = (ContextMenu)grid.FindName("TemplateMenu");
        var templates = menu.Items.OfType<MenuItem>().Where(item => item.Tag is string id && id.Length != 0 && id != "defaults").ToArray();
        Check(menu.IsOpen && templates.Select(item => (string)item.Tag).SequenceEqual(new[] { "DEVICE", "MOTOR" }),
            "The template menu must list the templates of the entity types: " + string.Join(", ", templates.Select(item => item.Tag)));
        Check(!Item(menu, "").IsEnabled && !Item(menu, "defaults").IsEnabled, "Without a template nothing can be removed or committed.");
        Pump(view);
        var right = menu.PointToScreen(new Point(menu.ActualWidth, 0)).X - view.PointToScreen(new Point(view.ActualWidth, 0)).X;
        Check(right <= 1, "The menu of «Назначить шаблон» must open leftwards inside the palette: " + right.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        SaveWithPopup(view, menu, Path.Combine(output, "tzxdata-stage7-template-menu.png"));
        var commits = source.Commits;
        Click(templates[1], menu, view);
        var objects = source.Objects();
        var ids = objects.Select(item => item.DeviceId).ToArray();
        Check(source.Commits == commits + 1 && objects.All(item => item.Template == "MOTOR" && item.Format == XDataFormat.V2) &&
              ids.All(id => Guid.TryParse(id, out _)) && ids.Distinct().Count() == 3,
            "The template must be assigned to the group in one transaction with a new DEVICE_ID per object.");
        Check(!assign.IsVisible && model.Grid.TemplateText == "Электродвигатель", "The indicator must show the assigned template.");
        var poles = Parameter(model, "Poles");
        Check(poles.StateText == "○" && poles.Note == PropertyGridViewModel.DefaultNote && poles.Value == "3" && source.Stored("Poles").All(value => value is null),
            "Template defaults must be shown with ○ and not written.");
        Check(model.Grid.HasDefaults, "Shown defaults must offer «Зафиксировать значения по умолчанию».");
        Save(view, Path.Combine(output, "tzxdata-stage7-assigned.png"));

        // «Зафиксировать значения по умолчанию» writes the shown defaults in one transaction.
        var indicator = (Button)grid.FindName("TemplateIndicator");
        Invoke(indicator, view);
        Check(Item(menu, "MOTOR").IsChecked && Item(menu, "").IsEnabled && Item(menu, "defaults").IsEnabled,
            "The menu must check the template and offer «Без шаблона» and the defaults.");
        SaveWithPopup(view, menu, Path.Combine(output, "tzxdata-stage7-commit-defaults.png"));
        Click(Item(menu, "defaults"), menu, view);
        Check(source.Commits == commits + 2 && source.Stored("Poles").All(value => Equals(value, 3)) &&
              source.Stored("Phase").All(value => Equals(value, "ABC")) && source.Stored("Device_Type").All(value => Equals(value, "Электродвигатель")),
            "The defaults of the template must be written.");
        Check(!model.Grid.HasDefaults && Parameter(model, "Poles").StateText == "●", "Committed defaults are stored values.");

        // Another template keeps DEVICE_ID and the values: Poles is outside DEVICE and goes to «Дополнительные».
        Invoke(indicator, view);
        Click(Item(menu, "DEVICE"), menu, view);
        objects = source.Objects();
        var extra = model.Grid.Find("g:" + PropertyGridViewModel.GroupExtra);
        Check(source.Commits == commits + 3 && objects.All(item => item.Template == "DEVICE") && objects.Select(item => item.DeviceId).SequenceEqual(ids) &&
              source.Stored("Poles").All(value => Equals(value, 3)) && extra != null && extra.Children.Any(row => row.Id == "Poles"),
            "Changing the template must keep DEVICE_ID and the values outside the new template.");
        Save(view, Path.Combine(output, "tzxdata-stage7-changed-template.png"));

        // «Без шаблона» removes the template; the values and DEVICE_ID stay.
        Invoke(indicator, view);
        Click(Item(menu, ""), menu, view);
        objects = source.Objects();
        Check(source.Commits == commits + 4 && objects.All(item => item.Template is null) && objects.Select(item => item.DeviceId).SequenceEqual(ids) &&
              source.Stored("Poles").All(value => Equals(value, 3)), "«Без шаблона» must remove only the template.");

        source.Select(SampleSelection());
        Pump(view);
        Check(!assign.IsVisible, "Objects with data must not offer a template.");
    }

    /// <summary>TZXDATA 9: the ⚙ menu starts the schema commands; the editor changes a draft and «Применить» writes it once.</summary>
    private static void VerifySchemaEditor(PaletteView view, PaletteViewModel model, string output)
    {
        var grid = Grid(view);
        var button = (Button)grid.FindName("SchemaButton");
        var schemaCommands = new[] { PropertyGridViewModel.SchemaEdit, PropertyGridViewModel.SchemaImport, PropertyGridViewModel.SchemaExport };
        // Issue #48: ⚙ also opens the settings, so it is enabled without AutoCAD; only the schema commands are disabled.
        Check(button.IsEnabled, "Without AutoCAD the ⚙ button must still open the settings.");
        Invoke(button, view);
        var menu = (ContextMenu)grid.FindName("SchemaMenu");
        Check(menu.IsOpen && schemaCommands.All(tag => !Item(menu, tag).IsEnabled) && Item(menu, PropertyGridView.SettingsCommand).IsEnabled,
            "Without AutoCAD the ⚙ menu must offer only «Настройки…».");
        menu.IsOpen = false;
        Pump(view);
        var started = new List<string>();
        model.Grid.SchemaCommandRunner = started.Add;
        Pump(view);
        Invoke(button, view);
        var commands = menu.Items.OfType<MenuItem>().Select(item => (string)item.Tag).ToArray();
        Check(menu.IsOpen && commands.SequenceEqual(schemaCommands.Concat(new[] { PropertyGridView.SettingsCommand })) &&
              menu.Items.OfType<MenuItem>().All(item => item.IsEnabled),
            "The ⚙ menu must offer the editor, import, export and the settings: " + string.Join(", ", commands));
        Pump(view);
        SaveWithPopup(view, menu, Path.Combine(output, "tzxdata-stage8-schema-menu.png"));
        Click(Item(menu, PropertyGridViewModel.SchemaEdit), menu, view);
        Check(started.SequenceEqual(new[] { PropertyGridViewModel.SchemaEdit }), "«Редактор схемы…» must start VELEC_SCHEMA_EDIT.");
        model.Grid.SchemaCommandRunner = null;

        var loaded = ParameterSchemaJson.Load(File.ReadAllText(SchemaPath));
        var selection = SampleSelection();
        var usage = new SchemaUsage();
        foreach (var item in selection) usage.Add(item.XData);
        var asked = new List<string>();
        var editor = new SchemaEditorViewModel(loaded.Schema, usage, selection) { Confirm = message => { asked.Add(message); return true; } };
        var window = new SchemaEditorWindow(editor);
        var result = ShowEditor(window, root =>
        {
            Check(editor.SelectedParameter == editor.Parameters[0] && !editor.IsDirty && !((Button)window.FindName("ApplyButton")).IsEnabled,
                "The editor must open on the first parameter with nothing to apply.");
            editor.SelectedParameter = editor.FindParameter("GC_Name");
            Pump(root);
            Check(((FrameworkElement)window.FindName("FormatField")).IsVisible && editor.SelectedParameter!.FormulaPreview == "«ЩУ1.3» (BlockReference [A1])",
                "The formula preview must calculate the draft on the first selected object: " + editor.SelectedParameter!.FormulaPreview);
            Save(root, Path.Combine(output, "tzxdata-stage8-parameters.png"));

            // A parameter stored in the objects cannot be deleted: the draft is kept and the status explains why.
            editor.SelectedParameter = editor.FindParameter("Power");
            editor.DeleteParameterCommand.Execute(null);
            Check(editor.FindParameter("Power") != null && editor.Status.Contains("удаление запрещено") && !editor.IsDirty,
                "A used parameter must not be deleted: " + editor.Status);

            window.SelectTab(1);
            editor.SelectedEnum = editor.FindEnum("Phase");
            Pump(root);
            var keys = (ListBox)window.FindName("EnumKeyList");
            Check(keys.IsVisible && keys.Items.Count == editor.SelectedEnum!.Items.Count && keys.Items.Count != 0, "The ENUM tab must list the keys.");
            Save(root, Path.Combine(output, "tzxdata-stage8-enums.png"));

            window.SelectTab(2);
            editor.SelectedTemplate = editor.FindTemplate("DEVICE");
            Pump(root);
            var members = (ListBox)window.FindName("TemplateParameterList");
            Check(members.IsVisible && members.Items.Count == editor.SelectedTemplate!.Parameters.Count && ((ListBox)window.FindName("AvailableList")).Items.Count != 0,
                "The template tab must show the parameters of the template and the others.");
            Save(root, Path.Combine(output, "tzxdata-stage8-templates.png"));
            VerifyTemplateDefaultButton(window, editor, root, output);

            // Issue #43: a format with = is an NCalc formula; NCalc comes from the embedded resources of the release DLL.
            window.SelectTab(0);
            editor.SelectedParameter = editor.FindParameter("GC_Name");
            var format = editor.SelectedParameter!.Format;
            editor.SelectedParameter.Format = "=\"Щит \" & @@[GC_HeadDevice] & \".\" & @@[GC_HDGroup] & IF(@@[Power] > 2 AND @@[Power] < 10, \" P>2\", \"\")";
            Pump(root);
            Check(editor.SelectedParameter.FormulaPreview == "«Щит ЩУ1.3 P>2» (BlockReference [A1])",
                "The formula preview must evaluate an NCalc formula: " + editor.SelectedParameter.FormulaPreview);
            Save(root, Path.Combine(output, "issue43-formula-preview.png"));
            VerifyFormulaEngine();
            editor.SelectedParameter.Format = format;
            Pump(root);

            // A new parameter with an invalid Id: the error is listed and «Применить» stays disabled.
            editor.AddParameterCommand.Execute(null);
            var added = editor.SelectedParameter!;
            added.Id = "1st";
            Pump(root);
            var apply = (Button)window.FindName("ApplyButton");
            Check(editor.IsDirty && editor.HasErrors && !apply.IsEnabled && ((ListBox)window.FindName("IssueList")).Items.Count != 0,
                "An invalid draft must list the error and block «Применить».");
            Save(root, Path.Combine(output, "tzxdata-stage8-errors.png"));
            added.Id = "Room";
            added.Caption = "Помещение";
            Pump(root);
            Check(editor.CanApply && apply.IsEnabled, "A corrected draft must be applicable.");

            window.Import("{ \"format\": \"BobrovXDATA.schema\", \"version\": 1, \"parameters\": [" +
                          "{ \"id\": \"Floor\", \"type\": \"Integer\", \"caption\": \"Этаж\" }, { \"id\": \"Power\", \"type\": \"Integer\", \"caption\": \"Мощность\" }] }",
                SchemaImportMode.Merge);
            Pump(root);
            Check(((ListBox)window.FindName("ReportList")).Items.Count != 0 && editor.FindParameter("Floor") != null && editor.FindParameter("Power")!.Type == "Double",
                "Merge must add Floor to the draft and keep the type of Power.");
            Save(root, Path.Combine(output, "tzxdata-stage8-import.png"));
            Invoke(apply, root);
        });
        Check(result == true && asked.Count == 0, "«Применить» must close the editor with DialogResult true without questions.");

        // The caller writes the draft in one transaction; the written NOD reads back as the draft.
        var nod = new MemoryNod();
        var written = editor.Apply(nod);
        var stored = SchemaNod.FromRecords(nod.Records);
        Check(nod.Commits == 1 && written.Changed && stored.Schema != null && stored.Schema.FindParameter("Room") != null && stored.Schema.FindParameter("Floor") != null &&
              ParameterSchemaJson.Write(stored.Schema) == editor.ExportJson(), "«Применить» must write the draft to NOD in one commit.");

        // «Отмена» of a changed draft asks once and leaves the drawing unchanged; the light theme follows AutoCAD.
        editor = new SchemaEditorViewModel(loaded.Schema, usage, selection) { Confirm = message => { asked.Add(message); return true; } };
        window = new SchemaEditorWindow(editor, PaletteTheme.Light);
        result = ShowEditor(window, root =>
        {
            editor.SelectedParameter = editor.FindParameter("GC_Name");
            Pump(root);
            Check(((SolidColorBrush)((Border)root).Background).Color == Resource(root, "PaletteBackground") &&
                  Resource(root, "PaletteBackground") != Colors.Black && Resource(root, "PaletteBackground").R > 128, "The light theme must be applied.");
            Save(root, Path.Combine(output, "tzxdata-stage8-light.png"));
            editor.SelectedParameter!.Caption = "Обозначение по группе";
            Pump(root);
            Check(editor.IsDirty && editor.CanApply, "A changed caption must make the draft applicable.");
            Invoke((Button)window.FindName("CancelButton"), root);
        });
        Check(result == false && asked.Count == 1 && asked[0].Contains("без записи в чертёж"), "«Отмена» of a changed draft must ask and return false.");
    }

    /// <summary>
    /// Issue #50: «Ок» right of «По умолчанию в шаблоне» is as high as the field and twice as wide; it assigns the typed
    /// default to the selected parameter of the template while the text is still in the field (no focus change).
    /// </summary>
    private static void VerifyTemplateDefaultButton(SchemaEditorWindow window, SchemaEditorViewModel editor, FrameworkElement root, string output)
    {
        var power = editor.SelectedTemplate!.Parameters.Single(item => item.Id == "Power");
        editor.SelectedTemplateParameter = power;
        Pump(root);
        var field = (TextBox)window.FindName("TemplateDefaultField");
        var ok = (Button)window.FindName("TemplateDefaultApplyButton");
        Check(field.IsVisible && ok.IsVisible && ok.IsEnabled && (string)ok.Content == "Ок", "«Ок» must be shown next to «По умолчанию в шаблоне».");
        var fieldBox = field.TransformToAncestor(root).TransformBounds(new Rect(field.RenderSize));
        var okBox = ok.TransformToAncestor(root).TransformBounds(new Rect(ok.RenderSize));
        Check(field.ActualHeight > 0 && Math.Abs(ok.ActualHeight - field.ActualHeight) < 0.5 && Math.Abs(ok.ActualWidth - 2 * field.ActualHeight) < 0.5,
            $"«Ок» must be as high as the field and twice as wide: field {field.ActualWidth}×{field.ActualHeight}, «Ок» {ok.ActualWidth}×{ok.ActualHeight}.");
        Check(okBox.Left >= fieldBox.Right && okBox.Left - fieldBox.Right < 10 && Math.Abs(okBox.Top - fieldBox.Top) < 0.5,
            $"«Ок» must stand right of the field on its line: field {fieldBox}, «Ок» {okBox}.");

        var before = power.DefaultText;
        var dirty = editor.IsDirty;
        field.Text = "7.5";
        Pump(root);
        Check(power.DefaultText == before && editor.IsDirty == dirty, "Typed text must not reach the draft before «Ок» or leaving the field.");
        Invoke(ok, root);
        Check(power.DefaultText == "7.5" && power.DefaultSummary == "переопределено: 7.5" && editor.IsDirty,
            "«Ок» must assign the default of the template: " + power.DefaultSummary);
        Check(Descendants<TextBlock>((DependencyObject)window.FindName("TemplateParameterList")).Any(text => text.Text == "переопределено: 7.5"),
            "The list of the template parameters must show the assigned default.");
        Save(root, Path.Combine(output, "issue50-template-default-ok.png"));

        // The previous text and «Ок» return the draft to its state before the check.
        field.Text = before;
        Pump(root);
        Invoke(ok, root);
        Check(power.DefaultText == before && editor.IsDirty == dirty, "«Ок» with the previous text must restore the draft.");
    }

    /// <summary>
    /// Issue #48: parameters of the schema group SYSTEM are hidden in the «Свойства» tab; «Настройки…» of the ⚙ menu
    /// opens the settings window, whose checkbox shows them. «Отмена» keeps the settings.
    /// </summary>
    private static void VerifySettings(PaletteView view, PaletteViewModel model, MemorySelection source, string output)
    {
        var grid = Grid(view);
        var list = List(grid);
        var button = (Button)grid.FindName("SchemaButton");
        var menu = (ContextMenu)grid.FindName("SchemaMenu");
        var system = new[] { "SYS_Guid", "SYS_Revision", "SYS_Source" };
        var changes = new List<bool>();
        model.Settings.PropertyChanged += (_, _) => changes.Add(model.Settings.ShowSystemGroup);
        Check(!model.Settings.ShowSystemGroup, "SYSTEM must be hidden by default.");
        model.Selection.Apply(new[]
        {
            Object("S1", "DEVICE", "BlockReference", Pair("Name", "Щит ЩР1"), Pair("Mark", "ЩРн-24"), Pair("Power", 4.5),
                Pair("SYS_Guid", "7F3C-21A9"), Pair("SYS_Revision", 3), Pair("SYS_Source", "БД проекта"))
        }, cache: Cache(SystemSchemaPath));
        Invoke((Button)grid.FindName("ExpandAllButton"), view);
        Check(model.Grid.Rows.Any(row => row.Id == "Power") && !model.Grid.Rows.Any(row => row.Id != null && system.Contains(row.Id)) &&
              !Descendants<TextBlock>(list).Any(text => text.Text == "SYSTEM" || text.Text.StartsWith("Ревизия")),
            "The parameters of the SYSTEM group must be hidden: " + string.Join(", ", model.Grid.Rows.Select(row => row.Id)));
        Check(!model.Grid.AvailableParameters.Any(option => system.Contains(option.Key)), "«+ Параметр» must not offer the hidden parameters.");
        Save(view, Path.Combine(output, "issue48-system-hidden.png"));

        var shown = 0;
        var previous = SettingsWindow.ShowModal;
        try
        {
            // «ОК» with the checkbox ticked: the grid shows SYSTEM at once.
            SettingsWindow.ShowModal = window => ShowSettings((SettingsWindow)window, root =>
            {
                shown++;
                var box = (CheckBox)window.FindName("ShowSystemGroupBox");
                Check(box.IsVisible && box.IsChecked == false, "The checkbox must show the current setting.");
                Check(((SolidColorBrush)window.Background).Color == Resource(view, "PaletteBackground"), "The settings window must use the dark theme of the palette.");
                Check(window.Owner == Window.GetWindow(view), "The settings window must belong to the palette window.");
                ((IToggleProvider)new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle)).Toggle();
                Pump(root);
                Check(box.IsChecked == true && !model.Settings.ShowSystemGroup, "The checkbox must change only the draft until «ОК».");
                Save(Client(window), Path.Combine(output, "issue48-settings-dark.png"));
                Invoke((Button)window.FindName("OkButton"), root);
            });
            Invoke(button, view);
            Click(Item(menu, PropertyGridView.SettingsCommand), menu, view);
            Invoke((Button)grid.FindName("ExpandAllButton"), view);
            Check(shown == 1 && model.Settings.ShowSystemGroup && changes.SequenceEqual(new[] { true }), "«ОК» must apply the checkbox.");
            Check(system.All(id => model.Grid.Rows.Any(row => row.Id == id)) && model.Grid.Roots.Any(row => row.Caption == "SYSTEM") &&
                  Descendants<TextBlock>(list).Any(text => text.Text == "SYSTEM"),
                "The ticked checkbox must show the SYSTEM group: " + string.Join(", ", model.Grid.Rows.Select(row => row.Id)));
            Save(view, Path.Combine(output, "issue48-system-shown.png"));

            // «Отмена» drops the draft.
            SettingsWindow.ShowModal = window => ShowSettings((SettingsWindow)window, root =>
            {
                shown++;
                var box = (CheckBox)window.FindName("ShowSystemGroupBox");
                Check(box.IsChecked == true, "The checkbox must show the applied setting.");
                ((IToggleProvider)new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle)).Toggle();
                Pump(root);
                Invoke((Button)window.FindName("CancelButton"), root);
            });
            Invoke(button, view);
            Click(Item(menu, PropertyGridView.SettingsCommand), menu, view);
            Check(shown == 2 && model.Settings.ShowSystemGroup && changes.Count == 1, "«Отмена» must keep the settings.");

            // The light theme of the palette gives a light window.
            model.Theme = PaletteTheme.Light;
            Pump(view);
            SettingsWindow.ShowModal = window => ShowSettings((SettingsWindow)window, root =>
            {
                shown++;
                Check(((SolidColorBrush)window.Background).Color == Resource(view, "PaletteBackground"), "The settings window must use the light theme of the palette.");
                Save(Client(window), Path.Combine(output, "issue48-settings-light.png"));
                Invoke((Button)window.FindName("CancelButton"), root);
            });
            Check(!grid.OpenSettings() && shown == 3 && model.Settings.ShowSystemGroup, "«Отмена» must return false.");
        }
        finally
        {
            SettingsWindow.ShowModal = previous;
            model.Theme = PaletteTheme.Dark;
        }

        model.Settings.ShowSystemGroup = false;
        Pump(view);
        Check(!model.Grid.Rows.Any(row => row.Id != null && system.Contains(row.Id)), "Clearing the setting must hide SYSTEM again.");
        source.Select(SampleSelection());
        Pump(view);
    }

    /// <summary>
    /// Issue #59: «Настройки…» edits the selection limit (15000 by default); a selection over it is not read and the
    /// palette says why. A hidden palette (inactive controller) reads nothing and is empty.
    /// </summary>
    private static void VerifySelectionLimit(PaletteView view, PaletteViewModel model, SelectionController controller, MemorySelection source, string output)
    {
        var grid = Grid(view);
        var button = (Button)grid.FindName("SchemaButton");
        var menu = (ContextMenu)grid.FindName("SchemaMenu");
        source.Select(SampleSelection());
        Pump(view);
        Check(model.Settings.MaxSelectionCount == PaletteSettings.DefaultMaxSelectionCount && model.Selection.Entities.Count == 2,
            "The default limit must read a selection of two objects.");
        var shown = 0;
        var previous = SettingsWindow.ShowModal;
        try
        {
            SettingsWindow.ShowModal = window => ShowSettings((SettingsWindow)window, root =>
            {
                shown++;
                var box = (TextBox)window.FindName("MaxSelectionCountBox");
                var error = (TextBlock)window.FindName("MaxSelectionCountError");
                Check(box.IsVisible && box.Text == "15000" && !error.IsVisible, "The limit field must show 15000: " + box.Text);
                box.Text = "много";
                Pump(root);
                Check(error.IsVisible, "A wrong limit must show the error.");
                Invoke((Button)window.FindName("OkButton"), root);
                Check(window.IsVisible && model.Settings.MaxSelectionCount == 15000, "«ОК» with a wrong limit must keep the window open.");
                box.Text = "1";
                Pump(root);
                Check(!error.IsVisible && model.Settings.MaxSelectionCount == 15000, "The field must change only the draft until «ОК».");
                Save(Client(window), Path.Combine(output, "issue59-settings-limit.png"));
                Invoke((Button)window.FindName("OkButton"), root);
            });
            Invoke(button, view);
            Click(Item(menu, PropertyGridView.SettingsCommand), menu, view);
        }
        finally { SettingsWindow.ShowModal = previous; }
        Pump(view);
        Check(shown == 1 && model.Settings.MaxSelectionCount == 1, "«ОК» must apply the limit.");
        Check(model.Selection.Entities.Count == 0 && model.Selection.SkippedCount == 2 &&
              model.Selection.Status == SelectionViewModel.OverLimitMessage(2, 1) && model.Grid.Message == model.Selection.Status,
            "A selection over the limit must not be read: " + model.Selection.Status);
        Save(view, Path.Combine(output, "issue59-over-limit.png"));

        model.Settings.MaxSelectionCount = PaletteSettings.DefaultMaxSelectionCount;
        Pump(view);
        Check(model.Selection.Entities.Count == 2 && model.Selection.SkippedCount == 0, "A larger limit must read the selection again.");

        var reads = source.Reads;
        controller.Active = false;
        source.Select(SampleSelection());
        Pump(view);
        Check(source.Reads == reads && model.Selection.Entities.Count == 0, "A hidden palette must not read the selection.");
        controller.Active = true;
        Pump(view);
        Check(source.Reads == reads + 1 && model.Selection.Entities.Count == 2, "A shown palette must read the selection at once.");
    }

    /// <summary>The client area of a window with its background, without the frame.</summary>
    private static FrameworkElement Client(Window window) => (FrameworkElement)VisualTreeHelper.GetChild(window, 0);

    /// <summary>Shows the modal settings window and runs <paramref name="checks"/> inside its dialog loop.</summary>
    private static bool? ShowSettings(SettingsWindow window, Action<FrameworkElement> checks)
    {
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { checks((FrameworkElement)window.FindName("Root")); }
            catch (Exception error) { failure = error; window.Close(); }
        }));
        var result = window.ShowDialog();
        if (failure != null) throw new InvalidOperationException("Settings window: " + failure.Message, failure);
        return result;
    }

    /// <summary>Shows the modal editor and runs <paramref name="checks"/> inside its dialog loop on the content.</summary>
    private static bool? ShowEditor(SchemaEditorWindow window, Action<FrameworkElement> checks)
    {
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { checks((FrameworkElement)window.FindName("Root")); }
            catch (Exception error) { failure = error; window.Model.Confirm = _ => true; window.Close(); }
        }));
        var result = window.ShowDialog();
        if (failure != null) throw new InvalidOperationException("Schema editor: " + failure.Message, failure);
        return result;
    }

    /// <summary>The NOD of a drawing in memory: what <see cref="SchemaEditorViewModel.Apply"/> writes in one commit.</summary>
    private sealed class MemoryNod : ISchemaNodTransaction
    {
        private readonly List<DataRecord> store = new List<DataRecord>();
        private readonly List<Action> pending = new List<Action>();
        public int Commits { get; private set; }
        public IReadOnlyList<DataRecord> Records => store.ToList();
        public IReadOnlyList<string> Warnings => Array.Empty<string>();
        public void Write(DataRecord record) => pending.Add(() =>
        {
            store.RemoveAll(existing => string.Equals(existing.Name, record.Name, StringComparison.OrdinalIgnoreCase));
            store.Add(record);
        });
        public void Erase(string path) => pending.Add(() => store.RemoveAll(existing => string.Equals(existing.Name, path, StringComparison.OrdinalIgnoreCase)));
        public void Commit() { pending.ForEach(action => action()); pending.Clear(); Commits++; }
        public void Dispose() => pending.Clear();
    }

    private static MenuItem Item(ContextMenu menu, string tag) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, tag));

    private static void Click(MenuItem item, ContextMenu menu, FrameworkElement view)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        menu.IsOpen = false;
        Pump(view);
    }

    /// <summary>
    /// Issue #41: the list above the panel groups the selection by SYSTEMTYPE or by the AutoCAD name of the object type,
    /// «Все (N)» only for more than one group; * keeps only the chosen group selected, − removes it, «Все» + − clears.
    /// </summary>
    private static void VerifySelectionGroups(PaletteView view, PaletteViewModel model, TabControl tabs, MemorySelection source, string output)
    {
        tabs.SelectedIndex = 0;
        source.Select(GroupSelection());
        Pump(view);
        var field = (ComboBox)view.FindName("SelectedGroupField");
        var keep = (Button)view.FindName("KeepGroupButton");
        var exclude = (Button)view.FindName("ExcludeGroupButton");
        string[] Texts() => field.Items.Cast<SelectionGroup>().Select(group => group.Text).ToArray();
        var expected = new[] { "Все (7)", "Опора (3)", "Полилиния (2)", "Отрезок (1)", "Вхождение блока (1)" };
        Check(Texts().SequenceEqual(expected), "Unexpected groups: " + string.Join(", ", Texts()));
        Check(field.IsVisible && keep.IsVisible && exclude.IsVisible && (string)keep.Content == "*" && (string)exclude.Content == "\u2212",
            "The group list and the * and − buttons must be above the panel.");
        Check(field.TranslatePoint(new Point(), view).X < keep.TranslatePoint(new Point(), view).X &&
              keep.TranslatePoint(new Point(), view).X < exclude.TranslatePoint(new Point(), view).X, "* and − must be right of the list.");
        Check(Descendants<TextBlock>(field).Any(text => text.Text == "Все (7)"), "«Все (N)» must be chosen first.");
        Check(keep.IsEnabled && exclude.IsEnabled, "The buttons must be available for a drawing selection.");

        // «Все» + * changes nothing.
        var changes = source.Changes;
        Invoke(keep, view);
        Check(source.Changes == changes && model.Selection.Entities.Count == 7, "«Все» + * must keep the selection.");

        field.IsDropDownOpen = true;
        Pump(view);
        var popup = (Popup)field.Template.FindName("PART_Popup", field);
        SaveWithPopup(view, (FrameworkElement)popup.Child, Path.Combine(output, "issue41-selection-groups.png"));
        field.IsDropDownOpen = false;
        Pump(view);

        // − removes the chosen group and the list is rebuilt from the new selection.
        field.SelectedItem = field.Items.Cast<SelectionGroup>().Single(group => group.Name == "Полилиния");
        Pump(view);
        Invoke(exclude, view);
        Check(source.Changes == changes + 1 && source.Handles().SequenceEqual(new[] { "10", "11", "12", "15", "16" }), "− must deselect the group.");
        Check(Texts().SequenceEqual(new[] { "Все (5)", "Опора (3)", "Отрезок (1)", "Вхождение блока (1)" }), "The list must follow −: " + string.Join(", ", Texts()));
        Save(view, Path.Combine(output, "issue41-after-exclude.png"));

        // * keeps only the chosen group; with one group «Все» is not listed.
        field.SelectedItem = field.Items.Cast<SelectionGroup>().Single(group => group.Name == "Опора");
        Pump(view);
        Invoke(keep, view);
        Check(source.Handles().SequenceEqual(new[] { "10", "11", "12" }), "* must keep only the group.");
        Check(Texts().SequenceEqual(new[] { "Опора (3)" }) && Descendants<TextBlock>(field).Any(text => text.Text == "Опора (3)"),
            "A single group must be listed without «Все»: " + string.Join(", ", Texts()));
        Save(view, Path.Combine(output, "issue41-after-keep.png"));
        Check(source.Snapshots().Count == 3 && source.Snapshots().All(snapshot => SelectionGroups.SystemType(snapshot.XData) == "Опора"),
            "The buttons must not change XDATA.");

        // «Все» + − clears the selection; the list and the buttons are hidden.
        source.Select(GroupSelection());
        Pump(view);
        Check(((SelectionGroup)field.SelectedItem).Text == "Опора (3)", "The chosen group must stay chosen after the selection changes.");
        field.SelectedIndex = 0;
        Pump(view);
        Check(((SelectionGroup)field.SelectedItem).IsAll, "«Все» must be the first item.");
        Invoke(exclude, view);
        Check(source.Handles().Length == 0 && model.Selection.Groups.Count == 0 && !field.IsVisible && !keep.IsVisible,
            "«Все» + − must clear the selection.");
        Check(source.Changes == changes + 3, "Every change must go through the drawing selection.");
    }

    /// <summary>
    /// Seven objects: three with SYSTEMTYPE «Опора» (v2, v1 and a polyline), an empty SYSTEMTYPE and objects without it.
    /// The type names stand for what AutoCAD returns in a Russian session; the plugin takes them from AutoCAD.
    /// </summary>
    private static EntityDataSnapshot[] GroupSelection() => new[]
    {
        Typed(Object("10", "DEVICE", "BlockReference", Pair("SYSTEMTYPE", "Опора")), "Вхождение блока"),
        Typed(new EntityDataSnapshot("11", "BlockReference", new[] { V(1001, DrawingNames.RegApp), V(1000, "SYSTEMTYPE=Опора") }, Array.Empty<DataRecord>()), "Вхождение блока"),
        Typed(Object("12", "DEVICE", "Polyline", Pair("SYSTEMTYPE", "Опора")), "Полилиния"),
        Typed(Object("13", "DEVICE", "Polyline", Pair("SYSTEMTYPE", " ")), "Полилиния"),
        Typed(new EntityDataSnapshot("14", "Polyline", Array.Empty<DataValue>(), Array.Empty<DataRecord>()), "Полилиния"),
        Typed(new EntityDataSnapshot("15", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>()), "Отрезок"),
        Typed(new EntityDataSnapshot("16", "BlockReference", Array.Empty<DataValue>(), Array.Empty<DataRecord>()), "Вхождение блока")
    };

    private static void VerifyEntityProperties(PaletteView view, PaletteViewModel model, TabControl tabs, string output)
    {
        var cache = Cache(Path.Combine(AppContext.BaseDirectory, "examples", "issue64-entity-properties-schema.json"));
        var list = List(Grid(view));
        tabs.SelectedIndex = 0;
        var block = Object("A1", "PART", "BlockReference", Pair("Project", "42"));
        var attributes = new EntityProperties(new Dictionary<string, string> { ["PART_CODE"] = "A-17", ["PART_SERIES"] = "S-1" });
        model.Selection.Apply(new[] { new EntityDataSnapshot(block.Handle, block.EntityType, block.XData, block.Records,
            typeDisplayName: "Вхождение блока", entityProperties: attributes) }, cache: cache);
        Invoke((Button)Grid(view).FindName("ExpandAllButton"), view);
        Check(Cell(list, "Атрибуты").IsVisible && Cell(list, "PART_CODE").IsVisible && Cell(list, "A-17").IsVisible &&
            Cell(list, "S-1").IsVisible && Cell(list, "П42: A-17").IsVisible, "The palette must show attributes and the formula using them.");
        Save(view, Path.Combine(output, "issue64-attributes.png"));

        var outline = Object("B2", "OUTLINE", "Polyline");
        model.Selection.Apply(new[] { new EntityDataSnapshot(outline.Handle, outline.EntityType, outline.XData, outline.Records,
            typeDisplayName: "Полилиния", entityProperties: new EntityProperties(length: 14, area: 12)) }, cache: cache);
        Invoke((Button)Grid(view).FindName("ExpandAllButton"), view);
        Check(Cell(list, "Геометрия").IsVisible && Cell(list, "Длина").IsVisible && Cell(list, "Площадь").IsVisible &&
            Cell(list, "14").IsVisible && Cell(list, "12").IsVisible && Cell(list, "24").IsVisible &&
            Cell(list, "14 ед. / 12 ед.²").IsVisible, "The palette must show length and area and the formulas using them.");
        Save(view, Path.Combine(output, "issue64-geometry.png"));
    }

    private static EntityDataSnapshot Typed(EntityDataSnapshot snapshot, string typeDisplayName) =>
        new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, snapshot.XData, snapshot.Records, typeDisplayName: typeDisplayName);

    private static EntityDataSnapshot Empty(string handle) =>
        new EntityDataSnapshot(handle, "BlockReference", Array.Empty<DataValue>(), Array.Empty<DataRecord>());

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
        Save(view, Path.Combine(output, "tzxdata-stage6-legacy.png"));
        tabs.SelectedIndex = 1;
        Pump(view);
        // The object of the tree is chosen in the «Дерево XDATA» tab; the list above the panel groups the selection (issue #41).
        var selector = Descendants<ComboBox>(view).Single(combo => combo.Name == "SelectedEntityField");
        selector.SelectedIndex = 0;
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

    /// <summary>
    /// TZXDATA stage 6: only the selected row has an editor; Enter writes the whole selection through XDataWriter, Esc
    /// restores the value, wrong input and failed writes are reported; check boxes, × ✎ «+ Добавить» and «+ Параметр».
    /// </summary>
    private static void VerifyEditing(PaletteView view, PaletteViewModel model, MemorySelection source, string output)
    {
        source.Select(SampleSelection());
        Pump(view);
        var grid = Grid(view);
        var list = List(grid);
        var message = (TextBlock)grid.FindName("MessageText");
        Check(model.Grid.CanEdit, "A writable v2 selection must be editable.");
        Check(!Descendants<TextBox>(list).Any(box => box.IsVisible), "Rows that are not selected must not show editors.");

        // «Разное»: a visit without typing writes nothing; typed text and Enter write every object in one transaction.
        var name = Select(list, Parameter(model, "NMO_Name"), view);
        var editor = Editor<TextBox>(list, name);
        Check(name.IsMixed && editor.Text == "" && Descendants<TextBox>(list).Count(box => box.IsVisible) == 1,
            "The selected «Разное» row must show one empty editor.");
        var commits = source.Commits;
        Press(editor, Key.Enter, view);
        Check(source.Commits == commits, "Enter without typing must not write «Разное».");
        editor = Editor<TextBox>(list, Selected(list));
        Check(EditBehavior.GetMixed(editor), "The empty editor of «Разное» must show the placeholder.");
        // Typed and erased text is an empty value, not «Разное»: the placeholder hides; Esc brings it back.
        editor.Text = "x";
        editor.Text = "";
        Pump(view);
        Check(!EditBehavior.GetMixed(editor), "A typed and erased text must hide the «Разное» placeholder.");
        Press(editor, Key.Escape, view);
        editor = Editor<TextBox>(list, Selected(list));
        Check(source.Commits == commits && EditBehavior.GetMixed(editor), "Esc must show «Разное» again without a write.");
        editor.Text = "Щит ВРУ";
        Pump(view);
        Press(editor, Key.Enter, view);
        Check(source.Commits == commits + 1 && source.Stored("NMO_Name").All(value => Equals(value, "Щит ВРУ")),
            "Enter must write the value to every object at once.");
        Check(Selected(list).Id == "NMO_Name" && !Selected(list).IsMixed && Selected(list).Value == "Щит ВРУ",
            "The written row must stay selected with the common value.");

        // Esc restores the shown value; nothing is written.
        var power = Select(list, Parameter(model, "Power"), view);
        editor = Editor<TextBox>(list, power);
        var shown = editor.Text;
        editor.Text = "4";
        Pump(view);
        Check(Descendants<TextBlock>(Container(list, power)).Any(text => text.IsVisible && text.Text == "кВт"), "The number editor must show its unit.");
        Save(view, Path.Combine(output, "tzxdata-stage6-editor.png"));
        Press(editor, Key.Escape, view);
        Check(source.Commits == commits + 1 && editor.Text == shown && !power.IsDirty, "Esc must restore the value without writing.");

        // Input outside the schema limits is reported in red above the VST and is not written.
        editor.Text = "-1";
        Pump(view);
        Press(editor, Key.Enter, view);
        Check(source.Commits == commits + 1 && model.Grid.HasError && message.Text.StartsWith("Ошибка ввода: Power", StringComparison.Ordinal) &&
              Fore(message) == Resource(view, "PaletteError"), "Wrong input must be reported and not written: " + message.Text);
        Save(view, Path.Combine(output, "tzxdata-stage6-input-error.png"));

        // An Enum value is picked in the list and written when the list closes.
        var phase = Select(list, Parameter(model, "Phase"), view);
        var choice = Editor<ComboBox>(list, phase);
        var option = phase.Options.Last();
        Check(choice.Items.Count == phase.Options.Count && phase.Options.Count > 1, "The list must offer the values of the enum.");
        // Issue #44: both states are rendered before the check, so a failure still leaves the two review images.
        var mixedText = ChoiceTextProblem(view, Container(list, phase), choice, "«Разное»", output, "issue44-enum-mixed");
        choice.Text = option.Caption; // «ABC (3 фазы)»: the brackets and «ф» reach the top and the bottom of the line
        Pump(view);
        var valueText = ChoiceTextProblem(view, Container(list, phase), choice, "the value", output, "issue44-enum-value");
        Check(mixedText == null && valueText == null, string.Join(" ", new[] { mixedText, valueText }.Where(problem => problem != null)));
        choice.IsDropDownOpen = true;
        Pump(view);
        choice.SelectedItem = option;
        choice.IsDropDownOpen = false;
        Pump(view);
        Check(source.Commits == commits + 2 && source.Stored("Phase").All(value => Equals(value, option.Key)) && !model.Grid.HasError,
            "A picked value must be written to every object: " + string.Join(", ", source.Stored("Phase")));

        // A click on the three-state box of «Разное» writes the clicked state; the other values are kept.
        Invoke((Button)grid.FindName("ExpandAllButton"), view);
        var concrete = TableRow(model, "СВ110-5");
        list.ScrollIntoView(concrete);
        Pump(view);
        var flag = Descendants<CheckBox>(Container(list, concrete)).Single(box => box.IsVisible && AutomationProperties.GetName(box) == "Флаг строки");
        Check(flag.IsChecked == null, "Different flags must show the mixed box.");
        flag.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, flag));
        Pump(view);
        Check(source.Commits == commits + 3 && source.Cells("Materials", "СВ110-5", "Mat_InSpec").All(value => Equals(value, true)) &&
              source.Cells("Materials", "CD35", "Mat_Count").All(value => Equals(value, 2.0)), "A check box must set the flag in every object.");

        // ✎ shows the cells of a row; a cell is written like a parameter.
        var row = TableRow(model, "CD35");
        Invoke(Action(list, row, "Редактировать строку"), view);
        row = TableRow(model, "CD35");
        var count = Select(list, model.Grid.Rows.Single(item => item.Kind == PropertyRowKind.Cell && item.RowKey == "CD35" && item.Column == "Mat_Count"), view);
        Check(row.IsEditing && list.Items.Cast<PropertyRow>().Count(item => item.Kind == PropertyRowKind.Cell && item.RowKey == "CD35") >= 4,
            "✎ must show every field of the row.");
        editor = Editor<TextBox>(list, count);
        editor.Text = "5";
        Pump(view);
        Save(view, Path.Combine(output, "tzxdata-stage6-row-edit.png"));
        Press(editor, Key.Enter, view);
        Check(source.Commits == commits + 4 && source.Cells("Materials", "CD35", "Mat_Count").All(value => Equals(value, 5.0)) &&
              Selected(list).Column == "Mat_Count" && TableRow(model, "CD35").IsEditing, "A cell must be written and stay open.");

        // «+ Добавить» adds a row to every object and selects its key; × deletes it again.
        var rows = source.Rows("Materials").Count;
        Invoke(Descendants<Button>(list).First(button => button.IsVisible && AutomationProperties.GetName(button) == "Добавить строку"), view);
        var added = Selected(list);
        Check(source.Commits == commits + 5 && source.Rows("Materials").Count == rows + 1 && added.Kind == PropertyRowKind.Cell &&
              added.RowKey == PropertyGridViewModel.NewRowKey && added.Column == "Mat_Mark", "«+ Добавить» must add a row and select its key.");
        Invoke(Action(list, TableRow(model, PropertyGridViewModel.NewRowKey), "Удалить строку"), view);
        Check(source.Commits == commits + 6 && source.Rows("Materials").Count == rows, "× must delete the row in every object.");

        // «+ Параметр» offers the parameters of the schema that are not shown and writes the chosen one.
        var add = (Button)grid.FindName("AddParameterButton");
        Invoke(add, view);
        var menu = (ContextMenu)grid.FindName("ParameterMenu");
        var poles = menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Tag, "Poles"));
        Check(menu.IsOpen && poles != null && poles.IsEnabled && menu.Items.Count == model.Grid.AvailableParameters.Count,
            "«+ Параметр» must list the parameters of the schema that are not shown.");
        SaveWithPopup(view, menu, Path.Combine(output, "tzxdata-stage6-add-parameter.png"));
        poles!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, poles));
        menu.IsOpen = false;
        Pump(view);
        Check(source.Commits == commits + 7 && source.Stored("Poles").All(value => Equals(value, 1)) && Selected(list).Id == "Poles" &&
              !model.Grid.AvailableParameters.Any(item => item.Key == "Poles"), "The added parameter must be written and selected.");

        // A failed transaction writes nothing: the error is shown, the values and the selection are kept.
        source.FailWrite = true;
        name = Select(list, Parameter(model, "NMO_Name"), view);
        editor = Editor<TextBox>(list, name);
        editor.Text = "Не записано";
        Pump(view);
        Press(editor, Key.Enter, view);
        Check(source.Commits == commits + 7 && source.Stored("NMO_Name").All(value => Equals(value, "Щит ВРУ")) && model.Grid.HasError &&
              message.Text.StartsWith("Ошибка записи", StringComparison.Ordinal) && Selected(list).Id == "NMO_Name",
            "A failed write must keep the values and the selection: " + message.Text);
        Save(view, Path.Combine(output, "tzxdata-stage6-write-error.png"));
        source.FailWrite = false;
        source.Select(SampleSelection());
        Pump(view);
        Check(!model.Grid.HasError, "Reading the selection again must clear the error.");
    }

    private static PropertyRow Parameter(PaletteViewModel model, string id) =>
        model.Grid.Rows.Single(row => row.Kind == PropertyRowKind.Parameter && row.Id == id);

    private static PropertyRow TableRow(PaletteViewModel model, string key) =>
        model.Grid.Rows.First(row => row.Kind == PropertyRowKind.TableRow && row.RowKey == key);

    private static PropertyRow Select(ListView list, PropertyRow row, FrameworkElement view)
    {
        // Like a click: the row gets the focus before it is selected. Otherwise the focused editor of the previous row
        // is hidden by the selection, WPF moves the focus to that row and scrolls the list back to it.
        for (var attempt = 0; attempt < 3 && list.ItemContainerGenerator.ContainerFromItem(row) == null; attempt++)
        {
            list.ScrollIntoView(row);
            Pump(view);
        }
        Container(list, row).Focus();
        list.SelectedItem = row;
        Pump(view);
        Check(ReferenceEquals(list.SelectedItem, row) && list.ItemContainerGenerator.ContainerFromItem(row) != null,
            "The row must be selected and shown: " + row.Caption);
        return row;
    }

    private static PropertyRow Selected(ListView list) => (PropertyRow)list.SelectedItem;

    private static ListViewItem Container(ListView list, PropertyRow row) =>
        list.ItemContainerGenerator.ContainerFromItem(row) as ListViewItem ??
        throw new InvalidOperationException("The row is not shown: " + row.Caption + ", offset " + Descendants<ScrollViewer>(list).FirstOrDefault()?.VerticalOffset);

    private static T Editor<T>(ListView list, PropertyRow row) where T : Control
    {
        var name = typeof(T) == typeof(ComboBox) ? "Список значений" : "Редактор значения";
        return Descendants<T>(Container(list, row)).Single(control => control.IsVisible && AutomationProperties.GetName(control) == name);
    }

    /// <summary>
    /// Issue #44: the text of the 21 px enum editor must fit vertically. The editable text box and the «Разное»
    /// placeholder must get at least one line of the font, otherwise the tops and bottoms of the letters are cut off.
    /// Saves the view and the zoomed row; returns the problem or null.
    /// </summary>
    private static string? ChoiceTextProblem(FrameworkElement view, ListViewItem row, ComboBox choice, string shown, string output, string name)
    {
        choice.ApplyTemplate();
        var box = (TextBox)choice.Template.FindName("PART_EditableTextBox", choice);
        box.ApplyTemplate();
        var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        var placeholder = (TextBlock)choice.Template.FindName("MixedPlaceholder", choice);
        var line = new FormattedText("Ёйрщ", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(box).PixelsPerDip).Height;
        var text = placeholder.IsVisible ? placeholder : (FrameworkElement)host;
        var room = LayoutInformation.GetLayoutSlot(text).Height - text.Margin.Top - text.Margin.Bottom;
        var bounds = text.TransformToAncestor(choice).TransformBounds(new Rect(text.RenderSize));
        Save(view, Path.Combine(output, name + ".png"));
        SaveZoomed(row, Path.Combine(output, name + "-zoom.png"));
        return choice.ActualHeight >= line && room >= line - 0.5 && text.ActualHeight >= line - 0.5 &&
               bounds.Top >= -0.5 && bounds.Bottom <= choice.ActualHeight + 0.5
            ? null
            : $"The enum editor must show {shown} without cutting the letters: editor {choice.ActualHeight:0.#} px, " +
              $"room {room:0.#} px, text {text.ActualHeight:0.#} px at {bounds.Top:0.#}–{bounds.Bottom:0.#}, line {line:0.#} px.";
    }

    private static Button Action(ListView list, PropertyRow row, string name)
    {
        list.ScrollIntoView(row);
        list.UpdateLayout();
        return Descendants<Button>(Container(list, row)).Single(button => button.IsVisible && AutomationProperties.GetName(button) == name);
    }

    /// <summary>
    /// Issue #66: the «Навигатор» tab over a drawing in memory. The profile bar, the multi-column tree with groups,
    /// expansion, sort, header drag, resize and the header menu; switching, editing, creating and deleting profiles in
    /// the profile window; a leaf selects its object by handle; a hidden tab does not read the drawing.
    /// </summary>
    private static void VerifyNavigator(PaletteView view, PaletteViewModel model, TabControl tabs, string folder, string output)
    {
        var navigatorModel = model.Navigator;
        var drawing = new MemoryDrawing(NavigatorCache(), NavigatorObjects());
        navigatorModel.Source = drawing;
        Check(drawing.Reads == 0, "A hidden navigator must not read the drawing.");
        tabs.SelectedIndex = PaletteViewModel.NavigatorTabIndex;
        Pump(view);
        Check(navigatorModel.IsShown && drawing.Reads == 1, "Showing the tab must read the drawing once: " + drawing.Reads);
        var navigator = Descendants<NavigatorView>(view).Single();
        var list = (ListView)navigator.FindName("Rows");
        var grid = (GridView)list.View;
        var combo = (ComboBox)navigator.FindName("ProfileField");
        var status = (TextBlock)navigator.FindName("StatusText");
        Check(combo.Items.Count == 2 && combo.SelectedItem is NavigatorProfile { Id: "electrical" } && combo.Text == "Электрооборудование",
            "The profile list must show the profiles of the folder and the active profile of navigator.json: " + combo.Text);
        Check(grid.Columns.Count == 6 && Equals(grid.Columns[0].Header, "Объект ▲") &&
              grid.Columns.Skip(1).Select(column => column.Header).SequenceEqual(new object[] { "Наименование", "Базовое имя", "Суффикс", "Мощность", "Фаза" }),
            "The tree column and the visible columns of the profile must be shown: " + string.Join(", ", grid.Columns.Select(column => column.Header)));
        Check(navigatorModel.Rows.All(row => row.Depth == 0) && list.Items.Count == navigatorModel.Rows.Count &&
              navigatorModel.Rows.Select(row => row.Caption).SequenceEqual(new[] { "ВРУ (1)", "Лампа (1)", "ЩР (3)", "Не задано (1)" }),
            "The tree must start with the collapsed groups of NMO_BaseName, «Не задано» last: " + string.Join(", ", navigatorModel.Rows.Select(row => row.Caption)));
        Check(status.Text == "Объектов: 6 · групп: 9", "The status must count the objects and groups: " + status.Text);

        Invoke((Button)navigator.FindName("ExpandAllButton"), view);
        var leaf = navigatorModel.Rows.Single(row => row.Node.Handle == "N1");
        Check(leaf.IsLeaf && leaf.Caption == "Блок «Щит»" && leaf.Depth == 2, "Leaves must be named by the object type (-@@[NMO_Name]): " + leaf.Caption);
        Check(navigatorModel.Rows.Count(row => row.Node.Handle != null) == 6 && list.Items.Count == navigatorModel.Rows.Count,
            "«Развернуть всё» must show every object.");
        var power = navigatorModel.Columns.Single(column => column.Id == "power");
        list.ScrollIntoView(leaf);
        Pump(view);
        var powerText = leaf.Cells[power.Slot].Text;
        Check(powerText.Length != 0 && Descendants<TextBlock>(list).Any(text => text.Text == powerText), "The cells must show the values of the object: " + powerText);
        Check(navigatorModel.Rows.Where(row => row.IsGroup).All(row => row.Cells[power.Slot].IsEmpty), "Group rows must have no values.");
        foreach (var theme in new[] { PaletteTheme.Dark, PaletteTheme.Light })
        {
            model.Theme = theme;
            Pump(view);
            var name = theme == PaletteTheme.Dark ? "dark" : "light";
            var group = Descendants<TextBlock>(list).First(text => text.Text == "ЩР (3)");
            Check(Fore(group) == Resource(view, "PaletteForeground") && group.FontWeight == FontWeights.SemiBold, "Groups must be semibold in the theme colors: " + name);
            Check(Fore(Descendants<TextBlock>(list).First(text => text.Text == "Не задано (1)")) == Resource(view, "PaletteMuted"),
                "The «Не задано» group must be grey: " + name);
            Check(((SolidColorBrush)list.Background).Color == Resource(view, "PaletteBackground"), "The tree must use the theme background: " + name);
            Save(view, Path.Combine(output, "issue66-navigator-" + name + ".png"));
        }
        model.Theme = PaletteTheme.Dark;
        Pump(view);

        // A leaf selects its object in AutoCAD; Enter also zooms to it; a group selects nothing.
        list.SelectedItem = leaf;
        Pump(view);
        Check(drawing.Selected.SequenceEqual(new[] { ("N1", false) }), "Selecting a leaf must select its object by handle.");
        Press(list, Key.Enter, view);
        Check(drawing.Selected.Last() == ("N1", true), "Enter on a leaf must show the object.");
        var shield = navigatorModel.Rows.First(row => row.Caption == "ЩР (3)");
        list.SelectedItem = shield;
        Pump(view);
        Check(drawing.Selected.Count == 2, "A group must not select objects.");
        Press(list, Key.Left, view);
        Check(!shield.IsExpanded && navigatorModel.Rows.All(row => row.Node.Handle != "N2"), "← must collapse the group.");
        Press(list, Key.Right, view);
        Check(shield.IsExpanded, "→ must expand the group.");
        drawing.Erased.Add("N2");
        list.SelectedItem = navigatorModel.Rows.First(row => row.Node.Handle == "N2");
        Pump(view);
        var diagnostics = (FrameworkElement)navigator.FindName("DiagnosticsPanel");
        Check(diagnostics.IsVisible && navigatorModel.Diagnostics.Any(text => text.Contains("N2") && text.Contains("Обновить")),
            "An erased object must be reported, not selected: " + string.Join("; ", navigatorModel.Diagnostics));
        Save(view, Path.Combine(output, "issue66-navigator-erased.png"));
        drawing.Erased.Clear();
        drawing.Objects = NavigatorObjects().Where(item => item.Snapshot.Handle != "N2").ToArray();
        var reads = drawing.Reads;
        Invoke((Button)navigator.FindName("RefreshButton"), view);
        Check(drawing.Reads == reads + 1 && navigatorModel.Rows.All(row => row.Node.Handle != "N2") &&
              navigatorModel.Rows.Any(row => row.Caption == "ЩР (2)" && row.IsExpanded),
            "«Обновить» must read the drawing and keep the expanded groups: " + string.Join(", ", navigatorModel.Rows.Select(row => row.Caption)));
        Check(navigatorModel.Message == null && !navigatorModel.Diagnostics.Any(text => text.Contains("N2")),
            "«Обновить» must remove the message about the erased object: " + string.Join("; ", navigatorModel.Diagnostics));
        drawing.Objects = NavigatorObjects();
        drawing.RaiseChanged();
        Pump(view);
        Check(navigatorModel.Rows.Any(row => row.Node.Handle == "N2"), "A change of the drawing must refresh the shown tree.");

        VerifyNavigatorGroupSelection(view, navigator, list, navigatorModel, drawing, output);

        // Sort by a column keeps the groups; a click on the tree header sorts by name.
        var headers = Descendants<GridViewColumnHeader>(list).Where(header => header.Column != null).ToList();
        var powerHeader = headers.Single(header => ReferenceEquals(header.Column, grid.Columns[4]));
        powerHeader.RaiseEvent(new RoutedEventArgs(GridViewColumnHeader.ClickEvent, powerHeader));
        Pump(view);
        Check(power.SortGlyph == " ▲" && Equals(grid.Columns[4].Header, "Мощность ▲") && Equals(grid.Columns[0].Header, "Объект"),
            "A header click must sort by the column: " + grid.Columns[4].Header);
        Check(navigatorModel.Rows.Where(row => row.Depth == 0).Select(row => row.Caption).SequenceEqual(new[] { "ВРУ (1)", "Лампа (1)", "ЩР (3)", "Не задано (1)" }),
            "Sorting by a column must keep the groups.");
        var treeHeader = headers.Single(header => ReferenceEquals(header.Column, grid.Columns[0]));
        treeHeader.RaiseEvent(new RoutedEventArgs(GridViewColumnHeader.ClickEvent, treeHeader));
        Pump(view);
        Check(power.SortGlyph == "" && Equals(grid.Columns[0].Header, "Объект ▲"), "A click on the tree header must sort by name: " + grid.Columns[0].Header);

        // A header drag changes the order of the profile, the tree column stays first; resize changes the width.
        Check(!navigatorModel.IsDirty || navigatorModel.Save(), "The sort must not leave unsaved columns.");
        var tree = navigatorModel.Tree;
        grid.Columns.Move(4, 1);
        Pump(view);
        Check(navigatorModel.VisibleColumns.Select(column => column.Id).SequenceEqual(new[] { "power", "name", "base", "suffix", "phase" }) && navigatorModel.IsDirty,
            "A header drag must change the order of the profile: " + string.Join(", ", navigatorModel.VisibleColumns.Select(column => column.Id)));
        grid.Columns.Move(3, 0);
        Pump(view);
        Check(Equals(grid.Columns[0].Header, "Объект ▲") && navigatorModel.VisibleColumns.Select(column => column.Id).SequenceEqual(new[] { "base", "power", "name", "suffix", "phase" }),
            "The tree column must stay first: " + string.Join(", ", grid.Columns.Select(column => column.Header)));
        grid.Columns[2].Width = 150;
        Pump(view);
        Check(power.Width == 150 && ReferenceEquals(tree, navigatorModel.Tree), "Width and order must not rebuild the tree.");
        Check(((Button)navigator.FindName("SaveButton")).IsEnabled, "Changed columns must enable «Сохранить».");

        // The header menu hides and shows columns.
        var columnMenu = grid.ColumnHeaderContextMenu;
        columnMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, columnMenu));
        Check(columnMenu.Items.OfType<MenuItem>().Count(item => item.IsCheckable && item.IsChecked) == 6, "The header menu must check the shown columns.");
        var phase = Item(columnMenu, "phase");
        phase.IsChecked = false;
        Click(phase, columnMenu, view);
        columnMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, columnMenu));
        var link = Item(columnMenu, "link");
        link.IsChecked = true;
        Click(link, columnMenu, view);
        Check(grid.Columns.Count == 6 && grid.Columns.Select(column => column.Header).Contains("Связь") && !grid.Columns.Select(column => column.Header).Contains("Фаза"),
            "The header menu must hide and show columns: " + string.Join(", ", grid.Columns.Select(column => column.Header)));
        list.ScrollIntoView(navigatorModel.Rows.First(row => row.Node.Handle == "N1"));
        Pump(view);
        Check(Descendants<TextBlock>(list).Any(text => text.Text == "db-01"), "A shown column must display its values at once.");
        Save(view, Path.Combine(output, "issue66-navigator-columns.png"));
        Invoke((Button)navigator.FindName("SaveButton"), view);
        var store = new NavigatorProfileStore(folder);
        var saved = store.LoadProfile("electrical")!.Profile!;
        Check(!navigatorModel.IsDirty && saved.Columns.Take(4).Select(column => column.Id).SequenceEqual(new[] { "base", "power", "name", "suffix" }) &&
              saved.Columns.Single(column => column.Id == "power").Width == 150 && !saved.Columns.Single(column => column.Id == "phase").Visible &&
              saved.Columns.Single(column => column.Id == "link").Visible,
            "«Сохранить» must write the order, width and visibility to the profile file.");

        // Another profile without a restart: the same objects are not read again.
        reads = drawing.Reads;
        combo.SelectedItem = navigatorModel.Profiles.Single(profile => profile.Id == "by-phase");
        Pump(view);
        Check(navigatorModel.Profile!.Id == "by-phase" && drawing.Reads == reads && store.LoadSettings(out _).ActiveProfile == "by-phase",
            "Choosing a profile must switch the tree without reading the drawing and remember the choice.");
        Check(grid.Columns.Count == 4 && Equals(grid.Columns[1].Header, "Мощность"), "The columns of the profile must be shown.");
        navigatorModel.ExpandAllCommand.Execute(null);
        Pump(view);
        Check(navigatorModel.Rows[0].Caption == "Фаза A (2)", "The tree must follow the expression of the profile: " + navigatorModel.Rows[0].Caption);
        var unit = navigatorModel.Columns.Single(column => column.Id == "unit");
        list.ScrollIntoView(navigatorModel.Rows.First(row => row.Node.Handle == "N5"));
        Pump(view);
        var error = Descendants<TextBlock>(list).FirstOrDefault(text => text.Text == FormulaExpression.ErrorText);
        Check(error != null && Fore(error) == Resource(view, "PaletteError") && error.ToolTip is string tip && tip.Contains("PowerPerUnit"),
            "A failing formula must show a red #ERR with the reason: " + string.Join(", ", navigatorModel.Rows.Select(row => row.Cells[unit.Slot].Text)));
        Save(view, Path.Combine(output, "issue66-navigator-by-phase.png"));

        var shown = 0;
        var previous = SettingsWindow.ShowModal;
        var confirm = NavigatorView.ConfirmDialog;
        try
        {
            // Edit the active profile: a broken expression keeps «ОК» disabled, a fixed one applies.
            SettingsWindow.ShowModal = window => ShowDialog(window, "Profile window", root =>
            {
                shown++;
                var profileWindow = (NavigatorProfileWindow)window;
                var editor = profileWindow.Editor;
                var expression = (TextBox)window.FindName("ExpressionBox");
                var ok = (Button)window.FindName("OkButton");
                Check(window.Owner == Window.GetWindow(view) && ((SolidColorBrush)window.Background).Color == Resource(view, "PaletteBackground"),
                    "The profile window must belong to the palette and use its theme.");
                Check(((TextBox)window.FindName("NameBox")).Text == "По фазам" && !((TextBox)window.FindName("IdBox")).IsEnabled &&
                      ((ListView)window.FindName("ColumnList")).Items.Count == 3 && ok.IsEnabled,
                    "The window must show the profile.");
                expression.Text = "+Phase|";
                Pump(root);
                var problems = (TextBlock)window.FindName("ProblemsText");
                Check(!ok.IsEnabled && problems.Text.Contains("уровень 2 пустой"), "A broken expression must disable «ОК»: " + problems.Text);
                Save(Client(window), Path.Combine(output, "issue66-profile-error.png"));
                expression.Text = "+Phase|+@@[NMO_Name]";
                Invoke((Button)window.FindName("AddColumnButton"), root);
                var added = editor.SelectedColumn!;
                added.Caption = "Количество";
                added.Value = "not an id";
                Pump(root);
                Check(!ok.IsEnabled && added.SourceError != null, "A wrong column value must disable «ОК».");
                added.Value = "Count";
                Invoke((Button)window.FindName("MoveUpButton"), root);
                ((TextBox)window.FindName("NameBox")).Text = "По фазам и количеству";
                Pump(root);
                Check(ok.IsEnabled && editor.Columns[2] == added && ((TextBlock)window.FindName("ExpressionInfoText")).Text.Length != 0,
                    "The fixed profile must be valid: " + problems.Text);
                Save(Client(window), Path.Combine(output, "issue66-profile-window-dark.png"));
                Invoke(ok, root);
            });
            var menu = navigator.FillProfileMenu();
            Check(!Item(menu, NavigatorView.SaveCommand).IsEnabled, "«Сохранить» must be disabled without changes.");
            Click(Item(menu, NavigatorView.EditCommand), menu, view);
            Check(shown == 1 && navigatorModel.Profile!.Name == "По фазам и количеству" && combo.Text == "По фазам и количеству" &&
                  grid.Columns.Count == 5 && Equals(grid.Columns[3].Header, "Количество"),
                "«ОК» must apply the profile: " + string.Join(", ", grid.Columns.Select(column => column.Header)));
            Check(store.LoadProfile("by-phase")!.Profile!.TreeExpression == "+Phase|+@@[NMO_Name]", "«ОК» must save the profile file.");

            // A new profile from the ⚙ menu.
            SettingsWindow.ShowModal = window => ShowDialog(window, "New profile window", root =>
            {
                shown++;
                var id = (TextBox)window.FindName("IdBox");
                Check(id.IsEnabled && ((NavigatorProfileWindow)window).Editor.IsNew, "A new profile must have an editable id.");
                id.Text = "by-base";
                ((TextBox)window.FindName("NameBox")).Text = "По базовому имени";
                ((TextBox)window.FindName("ExpressionBox")).Text = "+NMO_BaseName|+@@[NMO_Name]";
                Pump(root);
                Invoke((Button)window.FindName("OkButton"), root);
            });
            menu = navigator.FillProfileMenu();
            Click(Item(menu, NavigatorView.NewCommand), menu, view);
            Check(shown == 2 && navigatorModel.Profile!.Id == "by-base" && combo.Items.Count == 3 && File.Exists(store.ProfilePath("by-base")),
                "«Новый профиль…» must create, save and activate the profile.");

            // The light theme gives a light window; «Отмена» changes nothing.
            model.Theme = PaletteTheme.Light;
            Pump(view);
            SettingsWindow.ShowModal = window => ShowDialog(window, "Light profile window", root =>
            {
                shown++;
                Check(((SolidColorBrush)window.Background).Color == Resource(view, "PaletteBackground"), "The profile window must use the light theme.");
                ((TextBox)window.FindName("NameBox")).Text = "Не применять";
                Save(Client(window), Path.Combine(output, "issue66-profile-window-light.png"));
                Invoke((Button)window.FindName("CancelButton"), root);
            });
            menu = navigator.FillProfileMenu();
            Click(Item(menu, NavigatorView.EditCommand), menu, view);
            Check(shown == 3 && navigatorModel.Profile!.Name == "По базовому имени", "«Отмена» must keep the profile.");
            model.Theme = PaletteTheme.Dark;
            Pump(view);

            // Deleting asks first.
            var questions = new List<string>();
            NavigatorView.ConfirmDialog = text => { questions.Add(text); return questions.Count > 1; };
            menu = navigator.FillProfileMenu();
            Click(Item(menu, NavigatorView.DeleteCommand), menu, view);
            Check(questions.Count == 1 && File.Exists(store.ProfilePath("by-base")) && navigatorModel.Profile!.Id == "by-base", "«Нет» must keep the profile.");
            menu = navigator.FillProfileMenu();
            Click(Item(menu, NavigatorView.DeleteCommand), menu, view);
            Check(questions.Count == 2 && questions[1].Contains("По базовому имени") && !File.Exists(store.ProfilePath("by-base")) &&
                  combo.Items.Count == 2 && navigatorModel.Profile!.Id != "by-base",
                "«Да» must delete the profile file and switch to another profile.");
        }
        finally
        {
            SettingsWindow.ShowModal = previous;
            NavigatorView.ConfirmDialog = confirm;
            model.Theme = PaletteTheme.Dark;
        }

        // The drawing is closed: the tree is emptied; a hidden tab does not read, it reads when shown again.
        drawing.Document = null;
        drawing.RaiseChanged();
        Pump(view);
        Check(list.Items.Count == 0 && status.Text == "Нет активного чертежа", "A closed drawing must empty the tree: " + status.Text);
        drawing.Document = "drawing.dwg";
        tabs.SelectedIndex = 0;
        Pump(view);
        reads = drawing.Reads;
        drawing.RaiseChanged();
        Check(drawing.Reads == reads && !navigatorModel.IsShown, "A hidden navigator must not read the drawing.");
        tabs.SelectedIndex = PaletteViewModel.NavigatorTabIndex;
        Pump(view);
        Check(drawing.Reads == reads + 1, "Showing the tab must read the changed drawing once: reads " + (drawing.Reads - reads) + ".");
        // The TabControl creates the view of the tab again: the tree is shown by the new one.
        var shownAgain = (ListView)Descendants<NavigatorView>(view).Single().FindName("Rows");
        Check(navigatorModel.Rows.Count > 0 && shownAgain.Items.Count == navigatorModel.Rows.Count,
            "The navigator shown again must show the tree: " + shownAgain.Items.Count + " of " + navigatorModel.Rows.Count + ".");
        tabs.SelectedIndex = 0;
        Pump(view);
    }

    /// <summary>The profiles of examples/navigator in a temporary folder, as in %APPDATA%\AcadPaletteSetXData\navigator.</summary>
    private static string NavigatorFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xdata-navigator-smoke-" + Guid.NewGuid().ToString("N"));
        var examples = Path.Combine(AppContext.BaseDirectory, "examples", "navigator");
        Directory.CreateDirectory(Path.Combine(folder, NavigatorProfileStore.ProfilesFolder));
        File.Copy(Path.Combine(examples, NavigatorProfileStore.SettingsFileName), Path.Combine(folder, NavigatorProfileStore.SettingsFileName));
        foreach (var file in Directory.GetFiles(Path.Combine(examples, NavigatorProfileStore.ProfilesFolder), "*.json"))
            File.Copy(file, Path.Combine(folder, NavigatorProfileStore.ProfilesFolder, Path.GetFileName(file)));
        return folder;
    }

    private static ObjectValuesCache NavigatorCache() => Cache(NavigatorSchemaPath);

    /// <summary>Six objects of examples/navigator-schema.json: duplicates, an empty base name and a division by zero.</summary>
    private static (EntityDataSnapshot Snapshot, string Type)[] NavigatorObjects() => new[]
    {
        (Object("N1", "DEVICE", "BlockReference", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "1"), Pair("Power", 4.5), Pair("Phase", "A"), Pair("DB_link", "db-01")), "Блок «Щит»"),
        (Object("N2", "DEVICE", "BlockReference", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "2"), Pair("Power", 2.0), Pair("Phase", "B")), "Блок «Щит»"),
        (Object("N3", "DEVICE", "BlockReference", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "2"), Pair("Power", 1.0), Pair("Phase", "C")), "Блок «Щит»"),
        (Object("N4", "DEVICE", "BlockReference", Pair("NMO_BaseName", "ВРУ"), Pair("NMO_Suffix", "1"), Pair("Power", 12.5), Pair("Phase", "A")), "Блок «ВРУ»"),
        (Object("N5", "DEVICE", "Line", Pair("Power", 0.5), Pair("Count", 0)), "Отрезок"),
        (Object("N6", "DEVICE", "Circle", Pair("NMO_BaseName", "Лампа"), Pair("Power", 0.1), Pair("Count", 2), Pair("Phase", "B")), "Круг")
    };

    /// <summary>The objects of the active document for the navigator, as the AutoCAD source reads them.</summary>
    /// <summary>
    /// Issue #68: a click on the chosen object selects it again; the right button on a group opens the menu
    /// «Выделить все устройства группы (N)», which selects every object of the group; objects and the empty space have
    /// no menu, the header keeps the menu of columns; double clicks still show an object and expand a group.
    /// </summary>
    private static void VerifyNavigatorGroupSelection(PaletteView view, NavigatorView navigator, ListView list, NavigatorViewModel model, MemoryDrawing drawing, string output)
    {
        var leaf = model.Rows.First(row => row.Node.Handle == "N1");
        list.SelectedItem = leaf;
        Pump(view);
        var selected = drawing.Selected.Count;
        Check(drawing.Selected.Last() == ("N1", false), "Choosing an object must select it.");
        MouseDown(list, Caption(list, leaf), view);
        Check(drawing.Selected.Count == selected + 1 && drawing.Selected.Last() == ("N1", false),
            "A click on the chosen object must select it again: " + string.Join(", ", drawing.Selected));
        // Another row is chosen by the list itself (MouseLeftButtonDown, not raised here): the previous object is not selected again.
        MouseDown(list, Caption(list, model.Rows.First(row => row.Node.Handle == "N3")), view);
        Check(drawing.Selected.Count == selected + 1, "A click on another row must not select the previous object again.");
        MouseDown(list, Caption(list, model.Rows.First(row => row.Caption == "ЩР (3)")), view);
        Check(drawing.Selected.Count == selected + 1 && drawing.SelectedGroups.Count == 0, "A click on a group must select nothing.");
        DoubleClick(list, Caption(list, leaf), view);
        Check(drawing.Selected.Last() == ("N1", true), "A double click on an object must show it.");

        var shield = model.Rows.First(row => row.Caption == "ЩР (3)");
        Check(shield.IsExpanded && shield.ToolTip.Contains("Правая кнопка"), "The tooltip of a group must name its menu: " + shield.ToolTip);
        var menu = (ContextMenu)navigator.FindName("RowMenu");
        Check(ReferenceEquals(list.ContextMenu, menu), "The rows must have the menu of groups.");
        Check(MenuOpening(Caption(list, leaf), view), "An object must have no menu.");
        var header = Descendants<GridViewColumnHeader>(list).First(item => item.Column != null);
        Check(!MenuOpening(header, view), "The header must keep the menu of columns.");
        Check(!MenuOpening(Caption(list, shield), view), "A group must open its menu.");
        var item = Item(menu, NavigatorView.SelectGroupCommand);
        Check(menu.Items.Count == 1 && Equals(item.Header, "Выделить все устройства группы (3)"), "The menu of a group must offer to select its objects: " + item.Header);
        var picked = drawing.Selected.Count;
        list.SelectedItem = shield; // the right button chooses the row, as ListBoxItem does
        Pump(view);
        Check(drawing.Selected.Count == picked, "Choosing a group must select nothing.");
        var container = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(shield);
        menu.PlacementTarget = container;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 40;
        menu.IsOpen = true;
        Pump(view);
        SaveWithPopup(view, menu, Path.Combine(output, "issue68-navigator-group-menu.png"));
        var groups = drawing.SelectedGroups.Count;
        Click(item, menu, view);
        var expected = shield.Node.Descendants().Select(node => node.Handle).OfType<string>().ToArray();
        Check(drawing.SelectedGroups.Count == groups + 1 && expected.Length == 3 && drawing.SelectedGroups.Last().SequenceEqual(expected),
            "«Выделить все устройства группы» must select every object of the group: " + string.Join(", ", drawing.SelectedGroups.LastOrDefault() ?? new string[0]));
        Check(shield.IsExpanded && model.Message == null, "Selecting a group must not change the tree.");

        drawing.Erased.Add(expected[0]);
        Check(!MenuOpening(Caption(list, shield), view), "A group must open its menu again.");
        Click(Item(menu, NavigatorView.SelectGroupCommand), menu, view);
        Check(drawing.SelectedGroups.Last().Length == 2 && model.Message != null && model.Message.Contains("2 из 3"),
            "An erased object of the group must be reported: " + model.Message);
        drawing.Erased.Clear();
        Invoke((Button)navigator.FindName("RefreshButton"), view);
        Check(model.Message == null, "«Обновить» must remove the message about the group.");

        shield = model.Rows.First(row => row.Caption == "ЩР (3)");
        DoubleClick(list, Caption(list, shield), view);
        Check(!shield.IsExpanded, "A double click on a group must collapse it.");
        DoubleClick(list, Caption(list, shield), view);
        Check(shield.IsExpanded, "A double click on a group must expand it.");
    }

    /// <summary>The caption of the row (scrolled into view), a place where a click lands.</summary>
    private static TextBlock Caption(ListView list, NavigatorRow row)
    {
        list.ScrollIntoView(row);
        list.UpdateLayout();
        var container = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(row);
        return Descendants<TextBlock>(container).First(text => text.Text == row.Caption);
    }

    /// <summary>
    /// PreviewMouseLeftButtonDown is a direct event that WPF raises on every element of the route of the mouse button:
    /// raise it on the list with the clicked element as the original source.
    /// </summary>
    private static void MouseDown(ListView list, UIElement target, FrameworkElement view)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        args.Source = target;
        list.RaiseEvent(args);
        Pump(view);
    }

    /// <summary>
    /// MouseDoubleClick is a direct event: like <see cref="Control"/>, raise it on the list with the clicked element
    /// as the original source.
    /// </summary>
    private static void DoubleClick(ListView list, UIElement target, FrameworkElement view)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent };
        args.Source = target;
        list.RaiseEvent(args);
        Pump(view);
    }

    /// <summary>Raises ContextMenuOpening as the right button does; true when the menu is suppressed.</summary>
    private static bool MenuOpening(UIElement target, FrameworkElement view)
    {
        // ContextMenuEventArgs has no public constructor; this is the one the ContextMenuService uses.
        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new object[] { target, true }, null)!;
        target.RaiseEvent(args);
        Pump(view);
        return args.Handled;
    }

    private sealed class MemoryDrawing : INavigatorSource
    {
        private readonly ObjectValuesCache cache;

        public MemoryDrawing(ObjectValuesCache cache, (EntityDataSnapshot Snapshot, string Type)[] objects) { this.cache = cache; Objects = objects; }

        public event EventHandler? Changed;
        public (EntityDataSnapshot Snapshot, string Type)[] Objects { get; set; }
        public string? Document { get; set; } = "drawing.dwg";
        public int Reads { get; private set; }
        public List<(string, bool)> Selected { get; } = new List<(string, bool)>();
        public HashSet<string> Erased { get; } = new HashSet<string>();

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public NavigatorSnapshot Read(bool full)
        {
            Reads++;
            if (Document is null) return NavigatorSnapshot.Empty;
            return new NavigatorSnapshot(Document, Objects.Select(item => new NavigatorItem(cache.Get(item.Snapshot), item.Type)).ToArray(), cache.Schema);
        }

        public bool Select(string handle, bool zoom)
        {
            if (Erased.Contains(handle)) return false;
            Selected.Add((handle, zoom));
            return true;
        }

        public List<string[]> SelectedGroups { get; } = new List<string[]>();

        public int SelectAll(IReadOnlyList<string> handles)
        {
            var found = handles.Where(handle => !Erased.Contains(handle)).ToArray();
            if (found.Length > 0) SelectedGroups.Add(found);
            return found.Length;
        }
    }

    /// <summary>Shows a modal window and runs <paramref name="checks"/> inside its dialog loop on its root.</summary>
    private static bool? ShowDialog(Window window, string name, Action<FrameworkElement> checks)
    {
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { checks((FrameworkElement)window.FindName("Root")); }
            catch (Exception error) { failure = error; window.Close(); }
        }));
        var result = window.ShowDialog();
        if (failure != null) throw new InvalidOperationException(name + ": " + failure.Message, failure);
        return result;
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
        Save(view, Path.Combine(output, "tzxdata-stage6-minimum.png"));
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

    private static ObjectValuesCache Cache(string? path = null)
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(path ?? SchemaPath));
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

    /// <summary>
    /// The selection of a drawing with the example schema in its NOD, written through <see cref="XDataWriter"/> in one
    /// in-memory transaction per edit, as the AutoCAD writer does.
    /// </summary>
    private sealed class MemorySelection : ISelectionSource, ISelectionWriter, IObjectValuesSource, ISelectionChanger
    {
        private readonly XDataWriter writer;
        private EntityDataSnapshot[] snapshots = Array.Empty<EntityDataSnapshot>();

        public MemorySelection() => writer = new XDataWriter(handles => new Transaction(this, handles), Values);

        public int Reads { get; private set; }
        public int Commits { get; private set; }
        /// <summary>The transaction fails on commit: nothing is written.</summary>
        public bool FailWrite { get; set; }
        public ObjectValuesCache Values { get; } = Cache();
        ObjectValuesCache? IObjectValuesSource.Values => Values;
        public event EventHandler? SelectionChanged;
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() { Reads++; return snapshots; }
        public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit) => writer.WriteSelection(handles, edit);
        /// <summary>Changes made by * and −.</summary>
        public int Changes { get; private set; }

        /// <summary>Like <c>SetImpliedSelection</c>: selects these objects of the selection; the controller reads it again.</summary>
        public void SetSelection(IReadOnlyList<string> handles)
        {
            snapshots = snapshots.Where(snapshot => handles.Contains(snapshot.Handle)).ToArray();
            Changes++;
        }

        public string[] Handles() => snapshots.Select(snapshot => snapshot.Handle).ToArray();
        public IReadOnlyList<EntityDataSnapshot> Snapshots() => snapshots;

        public void Select(EntityDataSnapshot[] next)
        {
            snapshots = next;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The typed value of a parameter in every object.</summary>
        public object?[] Stored(string id) => snapshots.Select(snapshot => Read(snapshot).Find(id)?.Value).ToArray();

        /// <summary>A cell of the row with <paramref name="key"/> in every object.</summary>
        public object?[] Cells(string table, string key, string column) =>
            snapshots.Select(snapshot => Read(snapshot).Find(table)?.Rows?.FirstOrDefault(row => row.Key == key)?.Find(column)?.Value).ToArray();

        /// <summary>Row keys of the table in the first object.</summary>
        public IReadOnlyList<string?> Rows(string table) => Read(snapshots[0]).Find(table)!.Rows!.Select(row => row.Key).ToArray();

        /// <summary>The values of every object.</summary>
        public ObjectValues[] Objects() => snapshots.Select(Read).ToArray();

        private ObjectValues Read(EntityDataSnapshot snapshot) => XDataV2.Read(snapshot, Values.Schema);

        private sealed class Transaction : IXDataWriteTransaction
        {
            private readonly MemorySelection drawing;
            private readonly EntityDataSnapshot[] staged;

            public Transaction(MemorySelection drawing, IReadOnlyList<string> handles)
            {
                this.drawing = drawing;
                staged = drawing.snapshots.ToArray();
                Snapshots = handles.Select(handle => drawing.snapshots.Single(snapshot => snapshot.Handle == handle)).ToArray();
            }

            public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }

            public void Write(EntityDataSnapshot snapshot) => staged[Array.FindIndex(staged, item => item.Handle == snapshot.Handle)] = snapshot;

            public void Commit()
            {
                if (drawing.FailWrite) throw new InvalidOperationException("транзакция отменена");
                drawing.snapshots = staged;
                drawing.Commits++;
            }

            public void Dispose() { }
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

    /// <summary>Renders the view with an open popup (a context menu is a separate window) at its place.</summary>
    private static void SaveWithPopup(FrameworkElement view, FrameworkElement popup, string path)
    {
        Pump(view);
        popup.UpdateLayout();
        var offset = popup.PointToScreen(new Point()) - view.PointToScreen(new Point());
        var scene = new DrawingVisual();
        using (var context = scene.RenderOpen())
        {
            context.DrawImage(Render(view), new Rect(0, 0, view.ActualWidth, view.ActualHeight));
            context.DrawImage(Render(popup), new Rect(offset.X, offset.Y, popup.ActualWidth, popup.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(scene);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Renders an element four times larger, so that a few pixels of clipped text are visible in a review.</summary>
    private static void SaveZoomed(FrameworkElement element, string path)
    {
        // A VisualBrush draws the element at the origin; RenderTargetBitmap.Render would keep its offset in the parent.
        const double scale = 4;
        var size = new Rect(0, 0, element.ActualWidth * scale, element.ActualHeight * scale);
        var scene = new DrawingVisual();
        using (var context = scene.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Resource(element, "PaletteBackground")), null, size);
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, size);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(scene);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Issue #43: the formula engine is loaded. The release <c>acadPaletteSetXDATA.dll</c> is the only file of the
    /// plugin, so NCalc must be loaded from its embedded resources, not from a file.
    /// </summary>
    private static void VerifyFormulaEngine()
    {
        // NCalc 7 keeps the evaluator (NCalc.Expression) in NCalc.Core.dll.
        var engine = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "NCalc.Core");
        Check(engine != null, "The formula preview must load NCalc.Core: " +
            string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).OrderBy(name => name)));
        if (typeof(PaletteView).Assembly.GetName().Name != "acadPaletteSetXDATA") return;
        var files = Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Select(Path.GetFileName).ToArray();
        Check(engine!.Location.Length == 0 && EmbeddedAssemblies.Names.Contains("NCalc.Core"),
            "The release DLL must load NCalc.Core from its embedded resources: " + engine.Location);
        Check(files.SequenceEqual(new[] { "acadPaletteSetXDATA.dll" }, StringComparer.OrdinalIgnoreCase),
            "The release must need no DLL next to acadPaletteSetXDATA.dll: " + string.Join(", ", files));
    }

    private static BitmapSource Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
