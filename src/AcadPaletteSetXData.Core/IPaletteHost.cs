using System;

namespace AcadPaletteSetXData.Core;

public interface IPaletteHost : IDisposable
{
    bool Visible { get; set; }
    void ApplyTheme(PaletteTheme theme);
}
