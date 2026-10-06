using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.UI;

public partial class PropertyEditorView : UserControl
{
    public PropertyEditorView()
    {
        InitializeComponent();
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus));
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
        PreviewKeyDown += OnKey;
    }

    private FrameworkElement? FieldElement(object source)
    {
        var item = source as DependencyObject;
        while (item != null && !ReferenceEquals(item, this))
        {
            if (item is FrameworkElement element && EditBehavior.GetField(element).Length != 0) return element;
            item = VisualTreeHelper.GetParent(item);
        }
        return null;
    }

    private void Commit(FrameworkElement element)
    {
        if (!(DataContext is PropertyEditorViewModel editor)) return;
        var field = EditBehavior.GetField(element);
        if (element is CheckBox flag) flag.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        if (element.DataContext is MaterialItemViewModel row) editor.CommitMaterialField(row, field);
        else editor.CommitField(field);
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        var element = FieldElement(args.OriginalSource);
        if (element == null || element.IsKeyboardFocusWithin || element is CheckBox) return;
        Commit(element);
    }

    private void OnKey(object sender, KeyEventArgs args)
    {
        var element = FieldElement(args.OriginalSource);
        if (element == null) return;
        if ((args.Key == Key.Delete || args.Key == Key.Back) && EditBehavior.GetMixed(element))
        {
            var field = EditBehavior.GetField(element);
            // Clearing an already-empty placeholder is still a deliberate edit.
            var model = element.DataContext;
            var property = model.GetType().GetProperty(field)!;
            property.SetValue(model, property.GetValue(model));
        }
        if (args.Key != Key.Enter) return;
        Commit(element); args.Handled = true;
    }

    private void OnClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is CheckBox flag) Commit(flag);
    }
}
