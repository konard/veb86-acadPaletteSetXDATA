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
        // Keep startup independent of palette creation; defer output if no drawing is open.
        AcApplication.Idle -= OnIdle;
        if (!WriteLoadMessage()) AcApplication.Idle += OnIdle;
    }

    private static bool WriteLoadMessage()
    {
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
    public static void TogglePalette()
    {
        try
        {
            if (controller == null)
            {
                themeSource = new AutoCadThemeSource();
                controller = new PaletteController(() => new AutoCadPaletteHost(), themeSource);
            }

            controller.Toggle();
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
        controller?.Dispose();
        themeSource?.Dispose();
        controller = null;
        themeSource = null;
    }
}
