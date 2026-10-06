using System;
using System.Drawing;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.UI;
using Autodesk.AutoCAD.Windows;

namespace AcadPaletteSetXData.AutoCAD;

internal sealed class AutoCadPaletteHost : IPaletteHost
{
    // A stable ID lets AutoCAD associate the palette with its saved window state.
    private static readonly Guid PaletteId = new Guid("c167ea9c-6f53-47a6-ad70-3e731d87da12");
    private readonly PaletteViewModel viewModel = new PaletteViewModel();
    private readonly PaletteView view;
    private readonly PaletteSet palette;

    public AutoCadPaletteHost()
    {
        view = new PaletteView(viewModel);
        palette = new PaletteSet("Свойства XDATA", PaletteId);
        try
        {
            palette.Style = PaletteSetStyles.ShowAutoHideButton
                | PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu;
            palette.MinimumSize = new Size(280, 320);
            palette.Size = new Size(420, 600);
            palette.DockEnabled = DockSides.Left | DockSides.Right;
            palette.KeepFocus = false;
            // One native page: WPF owns the vertical right-side navigation.
            palette.AddVisual("XDATA", view, true);
        }
        catch
        {
            palette.Dispose();
            view.Dispose();
            throw;
        }
    }

    public bool Visible
    {
        get => palette.Visible;
        set => palette.Visible = value;
    }

    public void ApplyTheme(PaletteTheme theme) => viewModel.Theme = theme;

    public void Dispose()
    {
        palette.Dispose();
        view.Dispose();
    }
}
