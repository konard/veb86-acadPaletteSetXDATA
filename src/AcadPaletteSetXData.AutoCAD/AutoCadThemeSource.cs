using System;
using System.Globalization;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadPaletteSetXData.AutoCAD;

internal sealed class AutoCadThemeSource : IThemeSource, IDisposable
{
    public AutoCadThemeSource()
    {
        AcApplication.SystemVariableChanged += OnSystemVariableChanged;
    }

    public PaletteTheme CurrentTheme =>
        Convert.ToInt32(AcApplication.GetSystemVariable("COLORTHEME"), CultureInfo.InvariantCulture) == 1
            ? PaletteTheme.Light : PaletteTheme.Dark;

    public event EventHandler? ThemeChanged;

    private void OnSystemVariableChanged(object sender, SystemVariableChangedEventArgs args)
    {
        if (args.Changed && string.Equals(args.Name, "COLORTHEME", StringComparison.OrdinalIgnoreCase))
            ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        AcApplication.SystemVariableChanged -= OnSystemVariableChanged;
    }
}
