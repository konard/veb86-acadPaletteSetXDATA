using System;
using System.Drawing;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.UI;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadPaletteSetXData.AutoCAD;

internal sealed class AutoCadPaletteHost : IPaletteHost
{
    // A stable ID lets AutoCAD associate the palette with its saved window state.
    private static readonly Guid PaletteId = new Guid("c167ea9c-6f53-47a6-ad70-3e731d87da12");
    private readonly PaletteViewModel viewModel = new PaletteViewModel();
    private readonly PaletteView view;
    private readonly PaletteSet palette;
    private readonly AutoCadSelectionSource selectionSource;
    private readonly SelectionController selectionController;

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
            selectionSource = new AutoCadSelectionSource();
            selectionController = new SelectionController(selectionSource, viewModel.Selection);
            if (Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1") viewModel.Grid.Built += OnGridBuilt;
        }
        catch
        {
            selectionSource?.Dispose();
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

    // TZXDATA 10.6: the build time of the VST next to the read time of the selection.
    private void OnGridBuilt(object? sender, EventArgs args) =>
        AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n[XDATAPALETTE] {0}\n", viewModel.Grid.BuildSummary);

    public void Dispose()
    {
        viewModel.Grid.Built -= OnGridBuilt;
        selectionController.Dispose();
        selectionSource.Dispose();
        palette.Dispose();
        view.Dispose();
    }
}
