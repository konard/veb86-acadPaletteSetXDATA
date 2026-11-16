using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.UI;

/// <summary>
/// The general settings of the palette (issue #48), opened by «Настройки…» of the ⚙ menu. The window edits a
/// <see cref="PaletteSettings.Clone"/> draft; «ОК» copies it to the settings of the palette, «Отмена» drops it.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly PaletteSettings settings;

    public SettingsWindow(PaletteSettings settings, PaletteTheme theme = PaletteTheme.Dark)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();
        var name = theme == PaletteTheme.Light ? "Light" : "Dark";
        var assembly = typeof(SettingsWindow).Assembly.GetName().Name;
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/{assembly};component/Themes/{name}.xaml", UriKind.Relative)
        };
        Draft = settings.Clone();
        DataContext = Draft;
        MaxSelectionCountBox.Text = Draft.MaxSelectionCount.ToString(CultureInfo.InvariantCulture);
    }

    private void OnMaxSelectionCountChanged(object sender, TextChangedEventArgs args)
    {
        var value = PaletteSettings.ParseMaxSelectionCount(MaxSelectionCountBox.Text);
        MaxSelectionCountError.Visibility = value.HasValue ? Visibility.Collapsed : Visibility.Visible;
        if (value.HasValue) Draft.MaxSelectionCount = value.Value;
    }

    /// <summary>The edited copy of the settings.</summary>
    public PaletteSettings Draft { get; }

    /// <summary>
    /// Shows a settings window modally. The AutoCAD host sets <c>Application.ShowModalWindow</c>, which gives the
    /// window the AutoCAD main window as owner; elsewhere it is <see cref="Window.ShowDialog"/>.
    /// </summary>
    public static Func<Window, bool?> ShowModal { get; set; } = window => window.ShowDialog();

    /// <summary>Opens the window over <paramref name="settings"/>; true when «ОК» applied the draft.</summary>
    public static bool Edit(PaletteSettings settings, PaletteTheme theme, DependencyObject? owner = null)
    {
        var window = new SettingsWindow(settings, theme);
        var parent = owner == null ? null : GetWindow(owner);
        if (parent != null && !ReferenceEquals(parent, window)) window.Owner = parent;
        return ShowModal(window) == true;
    }

    private void OnOk(object sender, RoutedEventArgs args)
    {
        // A wrong limit keeps the window open: the error under the field says what to enter.
        if (!PaletteSettings.ParseMaxSelectionCount(MaxSelectionCountBox.Text).HasValue)
        {
            MaxSelectionCountBox.Focus();
            MaxSelectionCountBox.SelectAll();
            return;
        }
        settings.CopyFrom(Draft);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs args) => DialogResult = false;
}
