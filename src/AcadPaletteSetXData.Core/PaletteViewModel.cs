using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AcadPaletteSetXData.Core;

public sealed class PaletteViewModel : INotifyPropertyChanged
{
    private PaletteTheme theme;
    private int selectedTabIndex;

    public PaletteViewModel(bool enableDraftPreview = false)
    {
        Selection = new SelectionViewModel(Properties, enableDraftPreview);
        Grid = new PropertyGridViewModel(Selection);
        Tabs = Array.AsReadOnly(new[]
        {
            new PaletteTab("Свойства", "Свойства объектов", "", Grid),
            new PaletteTab("XDATA", "Расширенные данные", "", Selection),
            new PaletteTab("Команды", "Управление панелью",
                "Введите XDATAPALETTE в командной строке AutoCAD, чтобы показать или скрыть панель.")
        });
    }

    public PropertyEditorViewModel Properties { get; } = new PropertyEditorViewModel();
    public SelectionViewModel Selection { get; }
    /// <summary>The read-only VST of the «Свойства» tab (TZXDATA 8, stage 5).</summary>
    public PropertyGridViewModel Grid { get; }
    public IReadOnlyList<PaletteTab> Tabs { get; }

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
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
