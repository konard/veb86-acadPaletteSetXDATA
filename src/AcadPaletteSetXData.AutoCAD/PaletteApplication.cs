using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AcadPaletteSetXData.AutoCAD.PaletteApplication))]
[assembly: CommandClass(typeof(AcadPaletteSetXData.AutoCAD.PaletteApplication))]

namespace AcadPaletteSetXData.AutoCAD;

public sealed class PaletteApplication : IExtensionApplication
{
    private static PaletteController? controller;
    private static AutoCadThemeSource? themeSource;

    public void Initialize()
    {
        // Keep startup independent of palette creation.
        // Issue #47: the bundle loads the plugin at AutoCAD startup, while the startup drawing is still being
        // opened; the message waits for an Idle where no drawing opens and nothing runs.
        // Issue #53: the plugin adds no menu and no ribbon tab; menus are built by a separate module
        // that calls the XDATAPALETTE command (see docs/menu-module-spec.md).
        AutoCadStartup.Start();
        AcApplication.Idle -= OnIdle;
        if (!WriteLoadMessage()) AcApplication.Idle += OnIdle;
    }

    private static bool WriteLoadMessage()
    {
        if (!AutoCadStartup.IsReady("сообщение о загрузке")) return false;
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return false;
        document.Editor.WriteMessage(
            "\nacadPaletteSetXDATA загружено. Команда XDATAPALETTE — показать/скрыть панель XDATA.\n");
        return true;
    }

    private static void OnIdle(object? sender, System.EventArgs args)
    {
        if (WriteLoadMessage()) AcApplication.Idle -= OnIdle;
    }

    [CommandMethod("XDATAPALETTE", CommandFlags.Session | CommandFlags.UsePickSet)]
    public static void TogglePalette() => Run(current => current.Toggle());

    private static void Run(System.Action<PaletteController> action)
    {
        try
        {
            if (controller == null)
            {
                themeSource = new AutoCadThemeSource();
                controller = new PaletteController(() => new AutoCadPaletteHost(), themeSource);
            }

            action(controller);
        }
        catch (System.Exception error)
        {
            AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                "\nНе удалось открыть панель XDATA: {0}\n", error.Message);
        }
    }

    public void Terminate()
    {
        AcApplication.Idle -= OnIdle;
        AutoCadStartup.Stop();
        controller?.Dispose();
        themeSource?.Dispose();
        controller = null;
        themeSource = null;
    }
}
