using System;

namespace AcadPaletteSetXData.Core;

public interface IThemeSource
{
    PaletteTheme CurrentTheme { get; }
    event EventHandler ThemeChanged;
}
