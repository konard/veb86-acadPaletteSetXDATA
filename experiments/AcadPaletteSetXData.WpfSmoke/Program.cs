using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.UI;

namespace AcadPaletteSetXData.WpfSmoke;

/// <summary>Exercises the real WPF controls without AutoCAD and renders review images.</summary>
internal static class Program
{
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
            var model = new PaletteViewModel(enableDraftPreview: true);
            using var view = new PaletteView(model);
            var window = new Window
            {
                Title = "Предпросмотр панели XDATA", Content = view, Width = 520, Height = 760
            };
            if (args.Contains("--interactive"))
            {
                LoadSample(model.Properties);
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
            var editor = Descendants<PropertyEditorView>(view).Single();
            Check(model.Properties.Materials.Count == 0, "The plugin must start with an empty draft.");
            Save(view, Path.Combine(output, "stage2-empty.png"));
            VerifyDraftControls(editor, model.Properties, view);
            LoadSample(model.Properties);

            foreach (var theme in new[] { PaletteTheme.Dark, PaletteTheme.Light })
            {
                model.Theme = theme;
                Pump(view);
                var expectedBackground = theme == PaletteTheme.Dark ? "#FF3D4451" : "#FFF5F6F8";
                Check(((SolidColorBrush)view.Background).Color.ToString() == expectedBackground,
                    "The WPF background must follow the theme.");

                for (var index = 0; index < model.Tabs.Count; index++)
                {
                    // Set the control selection to exercise the two-way binding used by a click.
                    tabs.SelectedIndex = index;
                    Pump(view);
                    Check(model.SelectedTabIndex == index, "Selection must update the ViewModel.");
                    if (index == 0)
                    {
                        editor = Descendants<PropertyEditorView>(view).Single();
                        VerifyGroups(editor, model.Properties);
                        var name = (TextBox)editor.FindName("NameField");
                        Check(name.Text == "АО21", "The draft must survive theme changes and tab switches.");
                        Check(((SolidColorBrush)name.Foreground).Color.ToString() ==
                              (theme == PaletteTheme.Dark ? "#FFF3F4F6" : "#FF202832"),
                            "Editor text must follow the theme.");
                        foreach (var label in new[] { "МАРКА", "КОЛ.", "Спец.", "ПРИМЕЧАНИЕ" })
                        {
                            var heading = Descendants<TextBlock>(editor).Single(text => text.Text == label);
                            Check(((SolidColorBrush)heading.Foreground).Color == ((SolidColorBrush)name.Foreground).Color,
                                "Material column headings must remain readable in both themes: " + label);
                        }
                        var type = (ComboBox)editor.FindName("TypeField");
                        type.IsDropDownOpen = true;
                        Pump(view);
                        Check(type.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem,
                            "Type choices must be rendered in the real dropdown.");
                        type.IsDropDownOpen = false;
                        Pump(view);
                    }
                    else
                        Check(Descendants<TextBlock>(view).Any(text => text.Text == model.Tabs[index].Title),
                            "The selected tab content must be displayed.");
                    var previousY = -1.0;
                    for (var tabIndex = 0; tabIndex < tabs.Items.Count; tabIndex++)
                    {
                        var item = (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(tabIndex);
                        var location = item.TranslatePoint(new Point(), view);
                        Check(location.X > view.ActualWidth / 2, "The tab strip must be at the right edge.");
                        Check(location.Y > previousY, "Tab headers must be stacked vertically.");
                        Check(item.ActualWidth < 50 && item.ActualHeight > 65,
                            "Tab labels must have vertical geometry.");
                        previousY = location.Y;
                    }
                    Save(view, Path.Combine(output, $"stage2-{theme.ToString().ToLowerInvariant()}-{index}.png"));
                }
            }

            model.SelectedTabIndex = 0;
            model.Theme = PaletteTheme.Dark;
            Pump(view);
            Check(tabs.SelectedIndex == 0, "Changing the theme must preserve selection.");
            editor = Descendants<PropertyEditorView>(view).Single();
            var project = (Expander)editor.FindName("ProjectSection");
            Invoke(Descendants<System.Windows.Controls.Primitives.ToggleButton>(project).Single(), view);
            Check(project.IsExpanded, "The project section must expand via its header.");
            var projectField = (TextBox)editor.FindName("ProjectField");
            projectField.Text = "Проект 021 / лист 1";
            Check(model.Properties.ProjectReference == projectField.Text, "Project text must update the draft.");
            Save(view, Path.Combine(output, "stage2-project.png"));
            Invoke(Descendants<System.Windows.Controls.Primitives.ToggleButton>(project).Single(), view);
            Check(!project.IsExpanded, "The project section must collapse via its header.");
            var materials = (Expander)editor.FindName("MaterialsSection");
            Invoke(Descendants<System.Windows.Controls.Primitives.ToggleButton>(materials).First(), view);
            Check(!materials.IsExpanded, "The materials section must collapse.");
            Invoke(Descendants<System.Windows.Controls.Primitives.ToggleButton>(materials).First(), view);
            Check(materials.IsExpanded, "The materials section must reopen.");
            // Exercise the minimum supported palette size as well as the normal render.
            window.Width = 280;
            window.Height = 320;
            Pump(view);
            Check(view.ActualWidth > 0 && tabs.ActualWidth <= view.ActualWidth,
                "The shell must fit the minimum palette width.");
            var scroll = (ScrollViewer)editor.FindName("MaterialsScroll");
            Check(scroll.ExtentWidth > scroll.ViewportWidth, "Narrow palettes must allow horizontal material scrolling.");
            scroll.ScrollToRightEnd();
            Pump(view);
            Check(scroll.HorizontalOffset > 0, "The last material column must be reachable.");
            ((ScrollViewer)editor.FindName("EditorScroll")).ScrollToBottom();
            Pump(view);
            var add = (Button)editor.FindName("AddMaterialButton");
            var addLocation = add.TranslatePoint(new Point(), view);
            Check(addLocation.Y >= 0 && addLocation.Y + add.ActualHeight < view.ActualHeight,
                "The add button must be reachable by vertical scrolling at minimum size.");
            Save(view, Path.Combine(output, "stage2-minimum.png"));
            VerifySelectionControls(window, output);
            VerifyGroupControls(window, output);
            VerifyWriterControls(window, output);
            Check(bindingErrors.Messages.Count == 0, "WPF binding failures: " + string.Join("\n", bindingErrors.Messages));
            window.Close();
            view.Dispose();
            model.Theme = PaletteTheme.Light;
            Check(view.DataContext == null, "Disposal must detach the ViewModel.");
            application.Shutdown();
            Console.WriteLine("WPF checks passed: draft controls, read-only inspection, merged values, Enter/focus confirmation, transactional writer, preserved unique values, group material operations, typed trees, themes, resize and disposal.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void LoadSample(PropertyEditorViewModel editor)
    {
        editor.Type = "Опора 0,4 кВ";
        editor.Number = "";
        editor.Name = "АО21";
        editor.Title = "";
        editor.ProjectReference = "";
        editor.Materials.Clear();
        var marks = new[] { "СВ110-5", "ЗП6", "CD35", "CS 10.3", "E 778", "ES 1500E", "F 207", "NB 20", "P 72", "PA 1500" };
        var counts = new[] { "1", "1,2", "2", "1", "3", "1", "4", "4", "1", "1" };
        for (var index = 0; index < marks.Length; index++)
            editor.Materials.Add(new MaterialItemViewModel
            {
                Category = editor.Categories[Math.Min(index, 2)], Name = marks[index], Count = counts[index]
            });
    }

    private static void VerifySelectionControls(Window window, string output)
    {
        var model = new PaletteViewModel();
        using var view = new PaletteView(model);
        window.Content = view;
        window.Width = 520; window.Height = 760;
        Pump(view);
        var editor = Descendants<PropertyEditorView>(view).Single();
        Check(!editor.IsEnabled && model.Properties.IsReadOnly, "Empty selection must disable the editor.");
        Save(view, Path.Combine(output, "stage3-empty.png"));
        var sample = SampleSelection();
        model.Selection.Apply(sample.Take(1).ToArray());
        Pump(view);
        Check(editor.IsEnabled, "Selecting an object must enable property inspection.");
        Check(((TextBox)editor.FindName("NameField")).Text == "АО21", "Selected XDATA must populate the real property field.");
        Check(((TextBox)editor.FindName("NameField")).IsReadOnly, "Selection properties must be read-only in stage three.");
        Check(!((ComboBox)editor.FindName("TypeField")).IsEnabled, "Read-only type must reject dropdown changes.");
        Check(!((Button)editor.FindName("AddMaterialButton")).IsEnabled, "Adding must be disabled for selected DWG objects.");
        Check(Descendants<CheckBox>(editor).All(flag => !flag.IsEnabled), "Specification flags must be read-only.");
        VerifyGroups(editor, model.Properties);
        Save(view, Path.Combine(output, "stage3-single.png"));

        model.Selection.Apply(sample);
        var selector = (ComboBox)view.FindName("SelectedEntityField");
        foreach (var theme in new[] { PaletteTheme.Dark, PaletteTheme.Light })
        {
            model.Theme = theme;
            selector.SelectedIndex = 0;
            model.SelectedTabIndex = 0;
            Pump(view);
            Check(selector.Items.Count == 2 && model.Selection.SelectedEntity!.Handle == "A1", "Every selected entity must be inspectable.");
            Check(Descendants<TextBlock>(selector).Any(text => text.Text == "BlockReference [A1]"),
                "The selected-object caption must display the entity type and handle, not a CLR type name.");
            Save(view, Path.Combine(output, $"stage3-{theme.ToString().ToLowerInvariant()}-properties.png"));
            model.SelectedTabIndex = 1;
            Pump(view);
            var tree = Descendants<TreeView>(view).Single();
            Check(tree.Items.Count == 1 && tree.IsEnabled, "The selected entity must expose a navigable XDATA tree.");
            Check(Descendants<TextBlock>(tree).Any(text => text.Text.Contains("ESMT_LEP_v1.0")), "RegApp branches must be visible.");
            Save(view, Path.Combine(output, $"stage3-{theme.ToString().ToLowerInvariant()}-tree.png"));
            selector.SelectedIndex = 1;
            Pump(view);
            Check(model.Selection.SelectedEntity!.Handle == "B2", "Object selection must update the ViewModel.");
            Check(Descendants<TextBlock>(selector).Any(text => text.Text == "Polyline [B2]"),
                "Switching objects must update the selected-object caption.");
            Check(Descendants<TextBlock>(tree).Any(text => text.Text == "Name: Кабель"), "Parsed XML must render as structured tree nodes.");
            model.SelectedTabIndex = 0;
            Pump(view);
            editor = Descendants<PropertyEditorView>(view).Single();
            Check(model.Properties.IsNameMixed && ((TextBox)editor.FindName("NameField")).Text == "", "Raw-tree object switching must preserve the merged property view.");
        }
        model.SelectedTabIndex = 1;
        Pump(view);
        Save(view, Path.Combine(output, "stage3-xrecord.png"));
        model.Selection.Apply(new[] { new EntityDataSnapshot("C3", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>()) });
        model.SelectedTabIndex = 0;
        Pump(view);
        editor = Descendants<PropertyEditorView>(view).Single();
        Check(editor.IsEnabled && ((TextBox)editor.FindName("NameField")).Text == "", "An entity without XDATA must clear the previous values.");
        model.Selection.Apply(Array.Empty<EntityDataSnapshot>());
        Pump(view);
        Check(!editor.IsEnabled && model.Selection.Tree.Count == 0, "Deselection must disable controls and clear stale data.");
    }

    private static void VerifyGroupControls(Window window, string output)
    {
        var model = new PaletteViewModel();
        var source = new MemorySelection { Snapshots = SampleSelection() };
        using var controller = new SelectionController(source, model.Selection);
        using var view = new PaletteView(model);
        window.Content = view; window.Width = 520; window.Height = 760;
        Pump(view);
        var editor = Descendants<PropertyEditorView>(view).Single();
        var name = (TextBox)editor.FindName("NameField");
        Check(!name.IsReadOnly && model.Properties.IsNameMixed, "Selected properties must be editable and independently mixed.");
        foreach (var theme in new[] { PaletteTheme.Dark, PaletteTheme.Light })
        {
            model.Theme = theme; Pump(view);
            Check(name.Text == "" && EditBehavior.GetMixed(name), "Разное must be a placeholder, not the bound value.");
            var placeholder = (TextBlock)name.Template.FindName("MixedPlaceholder", name);
            Check(placeholder.IsVisible && placeholder.FontStyle == FontStyles.Italic, "Mixed headers must show an italic placeholder.");
            Check(((SolidColorBrush)placeholder.Foreground).Color == ((SolidColorBrush)view.FindResource("PaletteMuted")).Color,
                "Mixed text must follow the theme's muted color.");
            Check(Descendants<CheckBox>(editor).Any(flag => flag.IsChecked == null && flag.IsEnabled), "Different specification flags must be indeterminate and editable.");
            Check(Descendants<TextBox>(editor).Any(box => AutomationProperties.GetName(box) == "Марка материала" && EditBehavior.GetMixed(box)),
                "A material missing on one entity must show a mixed brand cell.");
            var mixedCount = Descendants<TextBox>(editor).First(box => AutomationProperties.GetName(box) == "Количество материала" && EditBehavior.GetMixed(box));
            var placeholderProbe = new TextBlock { Text = "Разное", FontFamily = mixedCount.FontFamily, FontSize = mixedCount.FontSize, FontStyle = FontStyles.Italic };
            placeholderProbe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(mixedCount.ActualWidth - mixedCount.Padding.Left - mixedCount.Padding.Right >= placeholderProbe.DesiredSize.Width,
                "The entire mixed placeholder must fit the count cell without clipping.");
            Save(view, Path.Combine(output, "stage4-" + theme.ToString().ToLowerInvariant() + "-mixed.png"));
        }
        name.Focus(); Pump(view);
        var number = (TextBox)editor.FindName("NumberField");
        number.Focus(); Pump(view);
        Check(source.Writes == 0, "Visiting and leaving a mixed field must never save the placeholder.");
        name.Focus(); name.Text = "Единое название"; Pump(view);
        Check(source.Writes == 0, "Typing must not write before confirmation.");
        Press(name, Key.Enter, view);
        Check(source.Writes == 1 && !model.Properties.IsNameMixed, "Enter must commit once to all selected entities.");
        Press(name, Key.Enter, view);
        number.Focus(); Pump(view);
        Check(source.Writes == 1, "Repeated Enter and subsequent focus loss must not duplicate a write.");
        number.Text = "099"; name.Focus(); Pump(view);
        Check(source.Writes == 2 && source.Snapshots.All(s => new XDataParser().Parse(s).Properties["Number"] == "099"),
            "Leaving a changed field must write every object.");
        var type = (ComboBox)editor.FindName("TypeField");
        type.Focus(); type.Text = "Общий тип"; Pump(view); Press(type, Key.Enter, view);
        Check(source.Writes == 3 && model.Properties.Type == "Общий тип", "Editable type must confirm through Enter.");
        var row = model.Properties.Materials.Single(material => material.MaterialKey == "CD35");
        var edit = Descendants<Button>(editor).Single(button => ReferenceEquals(button.DataContext, row) &&
            AutomationProperties.GetName(button) == "Редактировать материал");
        Invoke(edit, view);
        var count = Descendants<TextBox>(editor).Single(box => ReferenceEquals(box.DataContext, row) && AutomationProperties.GetName(box) == "Количество материала");
        count.Focus(); count.Text = "7"; Press(count, Key.Enter, view);
        Check(source.Snapshots.All(s => new XDataParser().Parse(s).Materials.Single(m => m.Name == "CD35").Count == "7"),
            "Material cells must patch all entities without replacing other material fields.");
        row = model.Properties.Materials.Single(material => material.MaterialKey == "CD35");
        var category = Descendants<ComboBox>(editor).Single(combo => ReferenceEquals(combo.DataContext, row));
        category.Focus(); Pump(view);
        var beforeCategoryWrite = source.Writes;
        category.Text = "Новая категория"; Pump(view);
        Check(source.Writes == beforeCategoryWrite && category.IsKeyboardFocusWithin,
            "Typing a category must not move the focused row or save before confirmation.");
        Press(category, Key.Enter, view);
        Check(source.Writes == beforeCategoryWrite + 1 && source.Snapshots.All(s => new XDataParser().Parse(s).Materials.Single(m => m.Name == "CD35").Category == "Новая категория"),
            "Category confirmation must regroup and write the full text to every object.");
        row = model.Properties.Materials.Single(material => material.MaterialKey == "CD35");
        VerifyGroups(editor, model.Properties);
        Check(row.IsEditing, "Confirmed material editing must keep the row open.");
        var flag = Descendants<CheckBox>(editor).Single(box => ReferenceEquals(box.DataContext, row));
        flag.IsChecked = true; flag.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, flag)); Pump(view);
        Check(source.Snapshots.All(s => new XDataParser().Parse(s).Materials.Single(m => m.Name == "CD35").IsInSpec == true),
            "Specification click must commit the chosen state to the group.");
        Invoke((Button)editor.FindName("AddMaterialButton"), view);
        Check(source.Snapshots.All(s => new XDataParser().Parse(s).Materials.Any(m => m.Name == "Новый материал 1")),
            "Add must write a stable new brand to every entity.");
        row = model.Properties.Materials.Single(material => material.MaterialKey == "Новый материал 1");
        var brand = Descendants<TextBox>(editor).Single(box => ReferenceEquals(box.DataContext, row) && AutomationProperties.GetName(box) == "Марка материала");
        brand.Focus(); brand.Text = "Новая марка"; Press(brand, Key.Enter, view);
        Check(source.Snapshots.All(s => new XDataParser().Parse(s).Materials.Any(m => m.Name == "Новая марка")), "Renaming must retain the original brand target across the group.");
        Save(view, Path.Combine(output, "stage4-edited.png"));
        row = model.Properties.Materials.Single(material => material.MaterialKey == "Новая марка");
        Invoke(Descendants<Button>(editor).Single(button => ReferenceEquals(button.DataContext, row) && AutomationProperties.GetName(button) == "Удалить материал"), view);
        Check(source.Snapshots.All(s => !new XDataParser().Parse(s).Materials.Any(m => m.Name == "Новая марка")), "Delete must remove the requested brand from every entity.");
        Check(source.Snapshots.Select(s => new XDataParser().Parse(s).Materials.Count).SequenceEqual(new[] { 2, 1 }),
            "Unrelated materials must survive group edits.");
        model.Selection.Apply(Array.Empty<EntityDataSnapshot>()); Pump(view);
        Check(!editor.IsEnabled, "Deselection must still disable editing.");
    }

    private static void Press(FrameworkElement control, Key key, FrameworkElement view)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        control.RaiseEvent(args); Pump(view);
    }

    private static void VerifyWriterControls(Window window, string output)
    {
        var model = new PaletteViewModel();
        var source = new MemorySelection { Snapshots = SampleSelection().Select(snapshot =>
            new XDataPatch().Apply(snapshot, SelectionEdit.Header("Type", "Кабель"))).ToArray() };
        using var controller = new SelectionController(source, model.Selection);
        using var view = new PaletteView(model);
        window.Content = view; model.SelectedTabIndex = 0; Pump(view);
        var editor = Descendants<PropertyEditorView>(view).Single();
        Check(!model.Properties.IsTypeMixed && model.Properties.IsNameMixed, "A shared type and unique names must be shown independently.");
        Save(view, Path.Combine(output, "stage5-before.png"));
        var original = source.Snapshots.Select(snapshot => new XDataParser().Parse(snapshot)).ToArray();
        var type = (ComboBox)editor.FindName("TypeField");
        type.Focus(); type.Text = "Устройство"; Pump(view); Press(type, Key.Enter, view);
        Check(source.Writes == 1 && model.Properties.Type == "Устройство", "The common type edit must commit one transaction.");
        for (var index = 0; index < source.Snapshots.Length; index++)
        {
            var after = new XDataParser().Parse(source.Snapshots[index]);
            Check(after.Properties["Name"] == original[index].Properties["Name"] &&
                  after.Properties["Number"] == original[index].Properties["Number"], "Editing a shared field must preserve unique names and numbers.");
            Check(after.Materials.Select(row => row.Count + "/" + row.IsInSpec).SequenceEqual(
                  original[index].Materials.Select(row => row.Count + "/" + row.IsInSpec)), "Unique material values must survive the writer.");
        }
        Check(model.Properties.IsNameMixed && model.Properties.IsNumberMixed, "Unedited mixed fields must remain mixed after refresh.");
        Save(view, Path.Combine(output, "stage5-after.png"));
    }

    private sealed class MemorySelection : ISelectionSource, ISelectionWriter
    {
        public EntityDataSnapshot[] Snapshots { get; set; } = Array.Empty<EntityDataSnapshot>();
        public int Writes { get; private set; }
        public event EventHandler? SelectionChanged { add { } remove { } }
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() => Snapshots;
        public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit)
        {
            Check(handles.SequenceEqual(Snapshots.Select(s => s.Handle)), "Write targets must be the whole selection.");
            new XDataWriter(_ => new MemoryTransaction(this)).WriteSelection(handles, edit);
        }

        private sealed class MemoryTransaction : IXDataWriteTransaction
        {
            private readonly MemorySelection source;
            private readonly EntityDataSnapshot[] staged;
            public MemoryTransaction(MemorySelection source) { this.source = source; Snapshots = source.Snapshots; staged = source.Snapshots.ToArray(); }
            public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }
            public void Write(EntityDataSnapshot snapshot) => staged[Array.FindIndex(staged, s => s.Handle == snapshot.Handle)] = snapshot;
            public void Commit() { source.Snapshots = staged; source.Writes++; }
            public void Dispose() { }
        }
    }

    private static EntityDataSnapshot[] SampleSelection()
    {
        DataValue V(int code, object value) => new DataValue(code, value);
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

    private static void VerifyDraftControls(PropertyEditorView editor, PropertyEditorViewModel model, PaletteView view)
    {
        var type = (ComboBox)editor.FindName("TypeField");
        type.Text = "Пользовательский тип";
        Check(model.Type == type.Text, "An editable type choice must update the draft.");
        foreach (var field in new[] { "NumberField", "NameField", "TitleField" })
            ((TextBox)editor.FindName(field)).Text = field;
        Check(model.Number == "NumberField" && model.Name == "NameField" && model.Title == "TitleField",
            "All header fields must update their corresponding properties.");
        Invoke((Button)editor.FindName("AddMaterialButton"), view);
        Check(model.Materials.Count == 1, "The add button must insert a row.");
        var material = model.Materials[0];
        Check(material.IsEditing, "New rows must open for editing.");
        var fields = Descendants<TextBox>(editor).Where(box => ReferenceEquals(box.DataContext, material)).ToList();
        var mark = fields.Single(box => AutomationProperties.GetName(box) == "Марка материала");
        mark.Text = "Тестовая марка";
        fields.Single(box => AutomationProperties.GetName(box) == "Количество материала").Text = "1,2";
        fields.Single(box => AutomationProperties.GetName(box) == "Примечание материала").Text = "Тест";
        Check(material.Name == "Тестовая марка" && material.Count == "1,2" && material.Comment == "Тест",
            "Material cells must update the correct row.");
        var flag = Descendants<CheckBox>(editor).Single(box => ReferenceEquals(box.DataContext, material));
        var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(flag).GetPattern(PatternInterface.Toggle);
        toggle.Toggle();
        Pump(view);
        Check(material.IsInSpec == null, "The checkbox must support the nullable specification state.");
        toggle.Toggle();
        Pump(view);
        Check(material.IsInSpec == false, "The specification checkbox must update the draft.");
        var category = Descendants<ComboBox>(editor).Single(box => ReferenceEquals(box.DataContext, material));
        category.Text = "Пользовательская категория";
        category.GetBindingExpression(ComboBox.TextProperty).UpdateSource();
        Pump(view);
        Check(material.Category == "Пользовательская категория", "Category editing must update the row.");
        VerifyGroups(editor, model);
        var edit = Descendants<Button>(editor).Single(button => ReferenceEquals(button.DataContext, material) &&
            AutomationProperties.GetName(button) == "Завершить редактирование");
        Invoke(edit, view);
        Check(!material.IsEditing, "The edit button must finish editing the selected row.");
        mark = Descendants<TextBox>(editor).Single(box => ReferenceEquals(box.DataContext, material) &&
            AutomationProperties.GetName(box) == "Марка материала");
        Check(mark.IsReadOnly, "Finished rows must return to display mode.");
        edit = Descendants<Button>(editor).Single(button => ReferenceEquals(button.DataContext, material) &&
            AutomationProperties.GetName(button) == "Редактировать материал");
        Invoke(edit, view);
        Check(material.IsEditing && !mark.IsReadOnly, "The edit button must reopen the same row.");
        var delete = Descendants<Button>(editor).Single(button => ReferenceEquals(button.DataContext, material) &&
            AutomationProperties.GetName(button) == "Удалить материал");
        Invoke(delete, view);
        Check(model.Materials.Count == 0, "The delete button must remove the selected row.");
        VerifyGroups(editor, model);
    }

    private static void VerifyGroups(PropertyEditorView editor, PropertyEditorViewModel model)
    {
        var list = (ItemsControl)editor.FindName("MaterialsList");
        var groups = ((ICollectionView)list.ItemsSource).Groups!.Cast<CollectionViewGroup>().ToList();
        Check(groups.Count == model.Materials.Select(item => item.GroupCategory).Distinct().Count(),
            "Materials must regroup when categories change.");
        foreach (var group in groups)
        {
            Check(group.Items.Cast<MaterialItemViewModel>().All(item => item.GroupCategory == (string)group.Name),
                "A group must contain only rows of its own category.");
            Check(Descendants<TextBlock>(list).Any(text => text.Text == (string)group.Name && text.FontStyle == FontStyles.Italic),
                "Category headers must be visible and italic.");
        }
    }

    private static void Invoke(Button control, FrameworkElement view)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(control).GetPattern(PatternInterface.Invoke)).Invoke();
        Pump(view);
    }

    private static void Invoke(System.Windows.Controls.Primitives.ToggleButton control, FrameworkElement view)
    {
        ((IToggleProvider)new ToggleButtonAutomationPeer(control).GetPattern(PatternInterface.Toggle)).Toggle();
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
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
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
