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
            var application = new Application();
            var bindingErrors = new BindingErrorListener();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            var model = new PaletteViewModel();
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
            Save(view, Path.Combine(output, "stage2-minimum.png"));
            Check(bindingErrors.Messages.Count == 0, "WPF binding failures: " + string.Join("\n", bindingErrors.Messages));
            window.Close();
            view.Dispose();
            model.Theme = PaletteTheme.Light;
            Check(view.DataContext == null, "Disposal must detach the ViewModel.");
            application.Shutdown();
            Console.WriteLine("WPF checks passed: fields, dropdown, project, live category groups, add/edit/delete, specification, right tabs, both themes, resize, disposal; nine PNG renders.");
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
        Check(groups.Count == model.Materials.Select(item => item.Category).Distinct().Count(),
            "Materials must regroup when categories change.");
        foreach (var group in groups)
        {
            Check(group.Items.Cast<MaterialItemViewModel>().All(item => item.Category == (string)group.Name),
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
