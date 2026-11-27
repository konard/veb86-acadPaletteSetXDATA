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
    // Issue #66: the profiles of the «Навигатор» tab are kept per user in %APPDATA%\AcadPaletteSetXData\navigator.
    private readonly PaletteViewModel viewModel =
        new PaletteViewModel(navigatorStore: new NavigatorProfileStore(NavigatorProfileStore.DefaultDirectory));
    private readonly PaletteView view;
    private readonly PaletteSet palette;
    private readonly AutoCadSelectionSource selectionSource;
    private readonly SelectionController selectionController;
    private readonly AutoCadNavigatorSource navigatorSource;

    public AutoCadPaletteHost()
    {
        // Issue #48: the general settings of the palette are kept per user; a missing or broken file gives the defaults.
        viewModel.Settings.CopyFrom(PaletteSettings.Load(PaletteSettings.DefaultPath));
        viewModel.Settings.PropertyChanged += OnSettingsChanged;
        // «⚙ → Настройки…» is modal to the AutoCAD main window.
        SettingsWindow.ShowModal = window => Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(window);
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
            // Issue #59: the palette follows the selection only while it is shown, and does not read a selection
            // over the limit of the settings. It is created hidden; XDATAPALETTE shows it.
            selectionController = new SelectionController(selectionSource, viewModel.Selection, viewModel.Settings, palette.Visible);
            // Issue #66: the navigator reads the ModelSpace of the active document while its tab is shown.
            navigatorSource = new AutoCadNavigatorSource();
            viewModel.Navigator.Source = navigatorSource;
            palette.StateChanged += OnStateChanged;
            // «⚙» starts the schema commands as commands of the document: one Undo step, no write from the palette.
            viewModel.Grid.SchemaCommandRunner = RunCommand;
            if (Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1") viewModel.Grid.Built += OnGridBuilt;
        }
        catch
        {
            viewModel.Navigator.Shutdown();
            navigatorSource?.Dispose();
            selectionSource?.Dispose();
            palette.Dispose();
            view.Dispose();
            throw;
        }
    }

    public bool Visible
    {
        get => palette.Visible;
        set
        {
            palette.Visible = value;
            selectionController.Active = palette.Visible;
            UpdateNavigator(palette.Visible);
        }
    }

    /// <summary>
    /// Issue #59: the palette closed by its title bar (or shown again) stops (or starts) following the selection, so a
    /// closed palette takes no part in selecting objects.
    /// </summary>
    private void OnStateChanged(object sender, PaletteSetStateEventArgs args)
    {
        if (args.NewState == StateEventIndex.Hide)
        {
            selectionController.Active = false;
            UpdateNavigator(false);
        }
        else if (args.NewState == StateEventIndex.Show)
        {
            selectionController.Active = true;
            UpdateNavigator(true);
        }
    }

    /// <summary>
    /// Issue #66: a closed palette does not read the drawing; the navigator reads it when it is shown again. While the
    /// palette is shown, choosing the tab turns the navigator on and off (<see cref="PaletteViewModel.SelectedTabIndex"/>).
    /// </summary>
    private void UpdateNavigator(bool visible) =>
        viewModel.Navigator.IsShown = visible && viewModel.SelectedTabIndex == PaletteViewModel.NavigatorTabIndex;

    public void ApplyTheme(PaletteTheme theme) => viewModel.Theme = theme;

    private static void RunCommand(string command) =>
        AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute("_" + command + " ", true, false, false);

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (viewModel.Settings.Save(PaletteSettings.DefaultPath)) return;
        AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nНастройки панели XDATA не сохранены: нет доступа к {0}.\n", PaletteSettings.DefaultPath);
    }

    // TZXDATA 10.6: the build time of the VST next to the read time of the selection.
    // The counters of the value cache show that a repeated selection parses nothing (TZXDATA 10.6).
    private void OnGridBuilt(object? sender, EventArgs args)
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        var values = AutoCadSchemaCaches.ValuesFor(document);
        document.Editor.WriteMessage("\n[XDATAPALETTE] {0} Разборов XDATA: {1}, деревьев XDATA: {2}.\n",
            viewModel.Grid.BuildSummary, values.ParseCount, values.TreeParseCount);
    }

    public void Dispose()
    {
        palette.StateChanged -= OnStateChanged;
        viewModel.Grid.Built -= OnGridBuilt;
        viewModel.Settings.PropertyChanged -= OnSettingsChanged;
        // Saves the unsaved column widths and order of the navigator and detaches it from the drawing.
        viewModel.Navigator.Shutdown();
        navigatorSource.Dispose();
        selectionController.Dispose();
        selectionSource.Dispose();
        palette.Dispose();
        view.Dispose();
    }
}
