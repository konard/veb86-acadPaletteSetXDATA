using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.UI;

/// <summary>
/// The settings of a profile of the «Навигатор» tab (issue #66): its name, the expression of the tree and the
/// columns. The window edits a <see cref="NavigatorProfileEditor"/>; nothing changes until «ОК».
/// </summary>
public partial class NavigatorProfileWindow : Window
{
    public NavigatorProfileWindow(NavigatorProfileEditor editor, PaletteTheme theme = PaletteTheme.Dark)
    {
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        InitializeComponent();
        var name = theme == PaletteTheme.Light ? "Light" : "Dark";
        var assembly = typeof(NavigatorProfileWindow).Assembly.GetName().Name;
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/{assembly};component/Themes/{name}.xaml", UriKind.Relative)
        };
        DataContext = editor;
        // Typing in a cell selects its row, so that − ▲ ▼ act on the column being edited.
        ColumnList.AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnCellFocus), true);
        Loaded += (_, _) => (editor.IsNew ? IdBox : NameBox).Focus();
    }

    public NavigatorProfileEditor Editor { get; }

    /// <summary>Opens the window over <paramref name="editor"/>; true when «ОК» accepted it.</summary>
    public static bool Edit(NavigatorProfileEditor editor, PaletteTheme theme, DependencyObject? owner = null)
    {
        var window = new NavigatorProfileWindow(editor, theme);
        var parent = owner == null ? null : GetWindow(owner);
        if (parent != null && !ReferenceEquals(parent, window)) window.Owner = parent;
        return SettingsWindow.ShowModal(window) == true;
    }

    private void OnCellFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        for (var current = args.NewFocus as DependencyObject; current != null && !ReferenceEquals(current, ColumnList);
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ListViewItem item)
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    private void OnOk(object sender, RoutedEventArgs args)
    {
        // A width typed in a cell is pushed on lost focus; OK may be pressed with Enter while the cell is focused.
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!Editor.IsValid) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs args) => DialogResult = false;
}
