using System;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcMenuApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// Puts the «Bobrov» menu on the menu bar and the «Bobrov» tab on the ribbon (issue #39).
/// The bundle loads the plugin at AutoCAD startup, before the main window is ready, so both are
/// created on the first <c>Idle</c>; the ribbon may appear later still (<c>ItemInitialized</c>).
/// A workspace switch (<c>WSCURRENT</c>) reloads the menu bar and the ribbon from the CUIX, so both are added again.
/// </summary>
internal static class BobrovInterface
{
    private static bool started;
    private static bool waitingForRibbon;
    private static bool firstInstall = true;

    public static void Start()
    {
        if (started) return;
        started = true;
        AcApplication.SystemVariableChanged += OnSystemVariableChanged;
        ScheduleInstall();
    }

    public static void Stop()
    {
        if (!started) return;
        started = false;
        AcApplication.Idle -= OnIdle;
        AcApplication.SystemVariableChanged -= OnSystemVariableChanged;
        StopWaitingForRibbon();
    }

    private static void ScheduleInstall()
    {
        AcApplication.Idle -= OnIdle;
        AcApplication.Idle += OnIdle;
    }

    private static void OnIdle(object? sender, EventArgs args)
    {
        AcApplication.Idle -= OnIdle;
        InstallMenu();
        InstallRibbon();
    }

    private static void OnSystemVariableChanged(object sender, SystemVariableChangedEventArgs args)
    {
        if (args.Changed && string.Equals(args.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase)) ScheduleInstall();
    }

    private static void InstallMenu()
    {
        try
        {
            // AutoCAD 2021 hides the menu bar by default; like TeplovPrint, show it once on load so «Bobrov» is visible.
            if (firstInstall)
            {
                firstInstall = false;
                try { AcApplication.SetSystemVariable("MENUBAR", 1); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { }
            }

            var failure = BobrovMenuInstaller.Install(AcMenuApplication.MenuGroups, AcMenuApplication.MenuBar);
            if (failure != null) WriteMessage("\nМеню " + BobrovUi.MenuName + " недоступно: " + failure + "\n");
        }
        catch (System.Exception error)
        {
            WriteMessage("\nНе удалось добавить меню " + BobrovUi.MenuName + ": " + error.Message + "\n");
        }
    }

    private static void InstallRibbon()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
            {
                // The ribbon is closed (RIBBONCLOSE) or not created yet: add the tab when it appears.
                if (!waitingForRibbon)
                {
                    waitingForRibbon = true;
                    ComponentManager.ItemInitialized += OnRibbonItemInitialized;
                }
                return;
            }

            StopWaitingForRibbon();
            BobrovRibbon.AddTo(ribbon, RunMacro);
        }
        catch (System.Exception error)
        {
            WriteMessage("\nНе удалось добавить вкладку ленты " + BobrovUi.RibbonTabTitle + ": " + error.Message + "\n");
        }
    }

    private static void OnRibbonItemInitialized(object? sender, RibbonItemEventArgs args)
    {
        if (ComponentManager.Ribbon != null) InstallRibbon();
    }

    private static void StopWaitingForRibbon()
    {
        if (!waitingForRibbon) return;
        waitingForRibbon = false;
        ComponentManager.ItemInitialized -= OnRibbonItemInitialized;
    }

    private static void RunMacro(string macro) =>
        AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute(macro, true, false, true);

    private static void WriteMessage(string message) =>
        AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(message);
}
