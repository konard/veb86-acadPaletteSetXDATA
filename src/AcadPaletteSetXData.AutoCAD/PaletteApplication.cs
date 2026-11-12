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
        // Issue #39: the «Bobrov» menu and ribbon tab are added on the first Idle, when the main window is ready.
        BobrovInterface.Start();
    }

    private static bool WriteLoadMessage()
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null) return false;
        document.Editor.WriteMessage(
            "\nacadPaletteSetXDATA загружено. Команда XDATAPALETTE — показать/скрыть панель XDATA; " +
            "меню и вкладка ленты Bobrov — включить/выключить.\n");
        return true;
    }

    private static void OnIdle(object? sender, System.EventArgs args)
    {
        if (WriteLoadMessage()) AcApplication.Idle -= OnIdle;
    }

    [CommandMethod("XDATAPALETTE", CommandFlags.Session | CommandFlags.UsePickSet)]
    public static void TogglePalette() => Run(current => current.Toggle());

    // Issue #39: the «Включить»/«Выключить» buttons of the «Bobrov» menu and ribbon tab.
    [CommandMethod(BobrovUi.ShowCommand, CommandFlags.Session | CommandFlags.UsePickSet)]
    public static void ShowPalette() => Run(current => current.Show());

    [CommandMethod(BobrovUi.HideCommand, CommandFlags.Session | CommandFlags.UsePickSet)]
    public static void HidePalette()
    {
        // Nothing to hide before the palette was ever shown: do not create it just to keep it hidden.
        if (controller != null) Run(current => current.Hide());
    }

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
        BobrovInterface.Stop();
        controller?.Dispose();
        themeSource?.Dispose();
        controller = null;
        themeSource = null;
    }
}
