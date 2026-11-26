using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AcadPaletteSetXData.Core;

public sealed class PaletteViewModel : INotifyPropertyChanged
{
    private PaletteTheme theme;
    private int selectedTabIndex;

    /// <param name="navigatorStore">The profile files of the «Навигатор» tab; null keeps its profiles in memory.</param>
    public PaletteViewModel(bool enableDraftPreview = false, NavigatorProfileStore? navigatorStore = null)
    {
        Navigator = new NavigatorViewModel(navigatorStore);
        Selection = new SelectionViewModel(Properties, enableDraftPreview);
        Grid = new PropertyGridViewModel(Selection, settings: Settings);
        Tabs = Array.AsReadOnly(new[]
        {
            new PaletteTab("Свойства", "Свойства объектов", "", Grid),
            new PaletteTab("XDATA", "Расширенные данные", "", Selection),
            new PaletteTab("Команды", "Управление панелью",
                "Введите XDATAPALETTE в командной строке AutoCAD, чтобы показать или скрыть панель."),
            new PaletteTab("Навигатор", "Навигатор объектов", "", Navigator)
        });
    }

    public PropertyEditorViewModel Properties { get; } = new PropertyEditorViewModel();
    /// <summary>General settings of the palette (issue #48): the «Настройки…» window of the ⚙ menu.</summary>
    public PaletteSettings Settings { get; } = new PaletteSettings();
    public SelectionViewModel Selection { get; }
    /// <summary>The read-only VST of the «Свойства» tab (TZXDATA 8, stage 5).</summary>
    public PropertyGridViewModel Grid { get; }
    /// <summary>The read-only tree of the objects of the drawing (issue #66), the fourth tab.</summary>
    public NavigatorViewModel Navigator { get; }
    public IReadOnlyList<PaletteTab> Tabs { get; }
    public const int NavigatorTabIndex = 3;

    public PaletteTheme Theme
    {
        get => theme;
        set
        {
            if (theme == value) return;
            theme = value;
            OnPropertyChanged();
        }
    }

    public int SelectedTabIndex
    {
        get => selectedTabIndex;
        set
        {
            if (value < 0 || value >= Tabs.Count)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (selectedTabIndex == value) return;
            selectedTabIndex = value;
            OnPropertyChanged();
            // The navigator reads the drawing only while it is shown.
            Navigator.IsShown = value == NavigatorTabIndex;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
