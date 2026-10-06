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
        // NETLOAD registers the command without opening a palette or requiring a document.
    }

    [CommandMethod("XDATAPALETTE", CommandFlags.Session)]
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
        controller?.Dispose();
        themeSource?.Dispose();
        controller = null;
        themeSource = null;
    }
}
