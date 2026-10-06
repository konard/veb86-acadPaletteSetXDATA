using System.Windows;

namespace AcadPaletteSetXData.UI;

/// <summary>Field identity and mixed state for the reusable editor templates.</summary>
public static class EditBehavior
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.RegisterAttached(
        "Field", typeof(string), typeof(EditBehavior), new PropertyMetadata(""));
    public static readonly DependencyProperty MixedProperty = DependencyProperty.RegisterAttached(
        "Mixed", typeof(bool), typeof(EditBehavior), new PropertyMetadata(false));
    public static void SetField(DependencyObject target, string value) => target.SetValue(FieldProperty, value);
    public static string GetField(DependencyObject target) => (string)target.GetValue(FieldProperty);
    public static void SetMixed(DependencyObject target, bool value) => target.SetValue(MixedProperty, value);
    public static bool GetMixed(DependencyObject target) => (bool)target.GetValue(MixedProperty);
}
