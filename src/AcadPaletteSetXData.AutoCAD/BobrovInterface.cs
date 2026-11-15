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
/// created on an <c>Idle</c>; the ribbon may appear later still (<c>ItemInitialized</c>).
/// A workspace switch (<c>WSCURRENT</c>) reloads the menu bar and the ribbon from the CUIX, so both are checked again.
/// <para>
/// Issue #47: <c>Idle</c> is also raised while the startup drawing is being opened and while a command,
/// a script or LISP waits for input. Changing the menu bar or <c>MENUBAR</c> then made «Выберите объекты»
/// flicker and blocked the drawing until the prompt was cancelled. The installation now waits for a quiet
/// Idle (<see cref="AutoCadStartup"/>), changes only what is missing, and ignores the <c>WSCURRENT</c> changes
/// it raises itself, so it cannot run in a loop.
/// </para>
/// </summary>
internal static class BobrovInterface
{
    private static readonly BobrovInstallScheduler Scheduler = new BobrovInstallScheduler(Install);
    private static bool started;
    private static bool waitingForRibbon;
    private static bool menuBarShown;

    public static void Start()
    {
        if (started) return;
        started = true;
        AcApplication.SystemVariableChanged += OnSystemVariableChanged;
        ScheduleInstall("загрузка");
    }

    public static void Stop()
    {
        if (!started) return;
        started = false;
        Scheduler.Cancel();
        AcApplication.Idle -= OnIdle;
        AcApplication.SystemVariableChanged -= OnSystemVariableChanged;
        StopWaitingForRibbon();
    }

    private static void ScheduleInstall(string reason)
    {
        if (!Scheduler.Request())
        {
            AutoCadStartup.Trace("Bobrov: " + reason + " во время установки — пропущено");
            return;
        }

        AutoCadStartup.Trace("Bobrov: установка запрошена (" + reason + ")");
        AcApplication.Idle -= OnIdle;
        AcApplication.Idle += OnIdle;
    }

    private static void OnIdle(object? sender, EventArgs args)
    {
        if (!Scheduler.Pending)
        {
            AcApplication.Idle -= OnIdle;
            return;
        }

        // Wait for an Idle where no drawing is being opened and no command, script or LISP runs.
        if (!AutoCadStartup.IsReady("Bobrov")) return;
        AcApplication.Idle -= OnIdle;
        Scheduler.OnIdle(true);
    }

    private static void OnSystemVariableChanged(object sender, SystemVariableChangedEventArgs args)
    {
        if (started && args.Changed && string.Equals(args.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
            ScheduleInstall("WSCURRENT");
    }

    private static void Install()
    {
        InstallMenu();
        InstallRibbon();
    }

    private static void InstallMenu()
    {
        try
        {
            ShowMenuBarOnce();
            var groups = AcMenuApplication.MenuGroups;
            // Issue #47: change the menu bar only when the menu is missing; a check after WSCURRENT changes nothing.
            if (BobrovMenuInstaller.IsInstalled(groups))
            {
                AutoCadStartup.Trace("Bobrov: меню уже в строке меню");
                return;
            }

            AutoCadStartup.Trace("Bobrov: добавление меню");
            var failure = BobrovMenuInstaller.Install(groups, AcMenuApplication.MenuBar);
            if (failure != null) WriteMessage("\nМеню " + BobrovUi.MenuName + " недоступно: " + failure + "\n");
        }
        catch (System.Exception error)
        {
            WriteMessage("\nНе удалось добавить меню " + BobrovUi.MenuName + ": " + error.Message + "\n");
        }
    }

    // AutoCAD 2021 hides the menu bar by default; like TeplovPrint, show it once per session so «Bobrov» is visible.
    // Issue #47: only when it is hidden — setting an unchanged MENUBAR still rebuilds the window and raises WSCURRENT.
    private static void ShowMenuBarOnce()
    {
        if (menuBarShown) return;
        menuBarShown = true;
        try
        {
            if (Convert.ToInt32(AcApplication.GetSystemVariable("MENUBAR")) != 0) return;
            AutoCadStartup.Trace("Bobrov: MENUBAR=1");
            AcApplication.SetSystemVariable("MENUBAR", 1);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        catch (FormatException) { }
        catch (InvalidCastException) { }
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
            if (BobrovRibbon.AddTo(ribbon, RunMacro)) AutoCadStartup.Trace("Bobrov: вкладка ленты добавлена");
        }
        catch (System.Exception error)
        {
            WriteMessage("\nНе удалось добавить вкладку ленты " + BobrovUi.RibbonTabTitle + ": " + error.Message + "\n");
        }
    }

    private static void OnRibbonItemInitialized(object? sender, RibbonItemEventArgs args)
    {
        // Issue #47: the ribbon appears while AutoCAD still starts; add the tab on a quiet Idle like the menu.
        if (ComponentManager.Ribbon == null) return;
        StopWaitingForRibbon();
        ScheduleInstall("лента создана");
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
