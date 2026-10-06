using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AcadPaletteSetXData.Core;

public sealed class PaletteViewModel : INotifyPropertyChanged
{
    private PaletteTheme theme;
    private int selectedTabIndex;

    public IReadOnlyList<PaletteTab> Tabs { get; } = Array.AsReadOnly(new[]
    {
        new PaletteTab("Свойства", "Свойства объектов",
            "Здесь будут отображаться свойства выбранных объектов."),
        new PaletteTab("XDATA", "Расширенные данные",
            "Здесь будут отображаться расширенные данные XDATA выбранных объектов."),
        new PaletteTab("Команды", "Управление панелью",
            "Введите XDATAPALETTE в командной строке AutoCAD, чтобы показать или скрыть панель.")
    });

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
