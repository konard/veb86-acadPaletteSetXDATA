using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AcadPaletteSetXData.Core;
using Microsoft.Win32;

namespace AcadPaletteSetXData.UI;

/// <summary>
/// The modal schema editor (TZXDATA 9): tabs «Параметры», «ENUM», «Шаблоны» and «Импорт/экспорт JSON» over
/// <see cref="SchemaEditorViewModel"/>. Every edit changes the draft only; the window closes with
/// <see cref="Window.DialogResult"/> <c>true</c> on «Применить» and the caller writes the draft to NOD in one
/// transaction. «Отмена» and closing the window leave the drawing unchanged.
/// </summary>
public partial class SchemaEditorWindow : Window
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private const string FileFilter = "Схема параметров (*.json)|*.json";
    private const string Reference = "@@[";
    private readonly SchemaEditorViewModel model;
    private bool closingConfirmed;

    public SchemaEditorWindow(SchemaEditorViewModel model, PaletteTheme theme = PaletteTheme.Dark)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        var name = theme == PaletteTheme.Light ? "Light" : "Dark";
        var assembly = typeof(SchemaEditorWindow).Assembly.GetName().Name;
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/{assembly};component/Themes/{name}.xaml", UriKind.Relative)
        };
        model.Confirm ??= Confirm;
        DataContext = model;
        Loaded += (_, _) =>
        {
            if (model.SelectedParameter is null && model.FilteredParameters.Count != 0) model.SelectedParameter = model.FilteredParameters[0];
            if (model.SelectedEnum is null && model.Enums.Count != 0) model.SelectedEnum = model.Enums[0];
            if (model.SelectedTemplate is null && model.Templates.Count != 0) model.SelectedTemplate = model.Templates[0];
            ParameterSearchField.Focus();
        };
    }

    public SchemaEditorViewModel Model => model;

    /// <summary>Selects a tab: 0 — «Параметры», 1 — «ENUM», 2 — «Шаблоны», 3 — «Импорт/экспорт JSON».</summary>
    public void SelectTab(int index) => Tabs.SelectedIndex = index;

    /// <summary>Imports <paramref name="json"/> into the draft and shows the report on the import tab.</summary>
    public void Import(string json, SchemaImportMode mode)
    {
        ReportList.ItemsSource = model.ImportJson(json, mode);
        Tabs.SelectedItem = ImportTab;
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // A text box commits on leaving it: move the focus so that the last typed value reaches the draft first.
        CommitFocusedText();
        if (!model.CanApply) return;
        closingConfirmed = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        closingConfirmed = true;
        DialogResult = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!closingConfirmed && !ConfirmDiscard()) e.Cancel = true;
    }

    private bool ConfirmDiscard()
    {
        CommitFocusedText();
        return !model.IsDirty || Confirm("Черновик схемы изменён. Закрыть редактор без записи в чертёж?");
    }

    private static void CommitFocusedText()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Keyboard.FocusedElement is DependencyObject element && ItemsControlFromEditable(element) is ComboBox combo)
            combo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
    }

    private static ComboBox? ItemsControlFromEditable(DependencyObject element)
    {
        for (var current = element; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is ComboBox combo) return combo;
        return null;
    }

    // ---- Formula hints: typing «@@[» opens the list of parameters, a choice inserts @@[Id] at the caret -------------

    private void OnFormatChanged(object sender, TextChangedEventArgs e)
    {
        var caret = FormatField.CaretIndex;
        if (caret >= Reference.Length && FormatField.Text.Substring(0, caret).EndsWith(Reference, StringComparison.Ordinal) && FormatField.IsKeyboardFocused)
            ReferenceList.IsDropDownOpen = true;
    }

    private void OnInsertReference(object sender, SelectionChangedEventArgs e)
    {
        if (!(ReferenceList.SelectedItem is string id) || model.SelectedParameter is null) return;
        var item = model.SelectedParameter;
        var position = Math.Min(FormatField.CaretIndex, item.Format.Length);
        // «@@[» typed before opening the list is replaced by the whole reference.
        if (position >= Reference.Length && item.Format.Substring(0, position).EndsWith(Reference, StringComparison.Ordinal))
        {
            item.Format = item.Format.Remove(position - Reference.Length, Reference.Length);
            position -= Reference.Length;
        }
        model.InsertReference(id, position);
        ReferenceList.SelectedIndex = -1;
        FormatField.Focus();
        FormatField.CaretIndex = Math.Min(position + Reference.Length + id.Length + 1, FormatField.Text.Length);
    }

    private void OnItemFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBoxItem item && !item.IsSelected) item.IsSelected = true;
    }

    private void OnAvailableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (model.AddToTemplateCommand.CanExecute(null)) model.AddToTemplateCommand.Execute(null);
    }

    // ---- Import / export of the draft ------------------------------------------------------------------------------

    private void OnImportMerge(object sender, RoutedEventArgs e) => ImportFile(SchemaImportMode.Merge);

    private void OnImportReplace(object sender, RoutedEventArgs e) => ImportFile(SchemaImportMode.Replace);

    private void ImportFile(SchemaImportMode mode)
    {
        var dialog = new OpenFileDialog { Filter = FileFilter, Title = "Импорт схемы в черновик" };
        if (dialog.ShowDialog(this) != true) return;
        try { Import(File.ReadAllText(dialog.FileName, Utf8), mode); }
        catch (Exception error) { ReportList.ItemsSource = new[] { "Файл не прочитан: " + error.Message }; }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        CommitFocusedText();
        var dialog = new SaveFileDialog { Filter = FileFilter, FileName = "schema.json", Title = "Экспорт черновика схемы" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, model.ExportJson(), Utf8);
            ReportList.ItemsSource = new[] { "Черновик записан в " + dialog.FileName + "." };
        }
        catch (Exception error) { ReportList.ItemsSource = new[] { "Файл не записан: " + error.Message }; }
    }
}
