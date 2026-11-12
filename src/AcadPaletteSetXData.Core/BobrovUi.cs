using System;
using System.Collections.Generic;
using System.Reflection;

namespace AcadPaletteSetXData.Core;

/// <summary>One button of the «Bobrov» menu and ribbon tab: a label and the AutoCAD command it starts.</summary>
public sealed class BobrovCommand
{
    public BobrovCommand(string id, string menuLabel, string ribbonLabel, string command, string toolTip, bool turnsOn)
    {
        Id = id;
        MenuLabel = menuLabel;
        RibbonLabel = ribbonLabel;
        Command = command;
        ToolTip = toolTip;
        TurnsOn = turnsOn;
    }

    /// <summary>A stable ribbon item id.</summary>
    public string Id { get; }
    public string MenuLabel { get; }
    public string RibbonLabel { get; }
    public string Command { get; }
    public string ToolTip { get; }
    /// <summary>True for «Включить», false for «Выключить»: selects the icon.</summary>
    public bool TurnsOn { get; }

    /// <summary>The macro of the menu item and ribbon button: cancels the active command, then starts this one.</summary>
    public string Macro => BobrovUi.Macro(Command);
}

/// <summary>
/// The «Bobrov» pull-down menu of the AutoCAD menu bar and the «Bobrov» ribbon tab (issue #39).
/// Both show the same on/off buttons of the XDATA palette.
/// </summary>
public static class BobrovUi
{
    public const string MenuName = "Bobrov";
    public const string RibbonTabTitle = "Bobrov";
    public const string RibbonTabId = "ACADPALETTESETXDATA_BOBROV_TAB";
    public const string RibbonPalettePanelId = "ACADPALETTESETXDATA_BOBROV_PALETTE_PANEL";
    public const string PalettePanelTitle = "Палитра XDATA";
    public const string ShowCommand = "XDATAPALETTE_ON";
    public const string HideCommand = "XDATAPALETTE_OFF";

    public static IReadOnlyList<BobrovCommand> PaletteCommands { get; } = new[]
    {
        new BobrovCommand("ACADPALETTESETXDATA_BOBROV_PALETTE_ON", "Включить палитру XDATA", "Включить",
            ShowCommand, "Показать панель «Свойства XDATA» (" + ShowCommand + ")", true),
        new BobrovCommand("ACADPALETTESETXDATA_BOBROV_PALETTE_OFF", "Выключить палитру XDATA", "Выключить",
            HideCommand, "Скрыть панель «Свойства XDATA» (" + HideCommand + ")", false),
    };

    /// <summary>
    /// <c>^C^C_COMMAND </c>: two Esc characters cancel a running command, «_» selects the global
    /// command name in a localized AutoCAD, the space ends the command line input.
    /// </summary>
    public static string Macro(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command name is required.", nameof(command));
        return "\u0003\u0003_" + command + " ";
    }
}

/// <summary>
/// Adds the «Bobrov» pull-down menu to the menu bar through the late-bound AutoCAD COM objects
/// (<c>Application.MenuGroups</c>, <c>Application.MenuBar</c>), as TeplovPrint does: no COM interop
/// assembly is shipped, and the menu is rebuilt on every load instead of being saved into a CUIX.
/// </summary>
public static class BobrovMenuInstaller
{
    private const string PreferredMenuGroup = "ACAD";

    /// <summary>
    /// Creates or refills the «Bobrov» menu and puts it on the menu bar. Returns null on success,
    /// otherwise the reason why the menu is unavailable; never throws for a missing COM member.
    /// </summary>
    public static string? Install(object? menuGroups, object? menuBar)
    {
        if (menuGroups == null || menuBar == null) return "AutoCAD не предоставляет строку меню.";
        var group = Invoke(menuGroups, "Item", PreferredMenuGroup) ?? Invoke(menuGroups, "Item", 0);
        if (group == null) return "нет группы меню.";
        var menus = Get(group, "Menus");
        if (menus == null) return "нет коллекции меню группы.";
        var menu = Invoke(menus, "Item", BobrovUi.MenuName) ?? Invoke(menus, "Add", BobrovUi.MenuName);
        if (menu == null) return "не удалось создать меню " + BobrovUi.MenuName + ".";

        // A reload of the plugin finds the menu of the previous load: refill it instead of doubling the items.
        var oldCount = Count(menu);
        for (var i = 0; i < oldCount; i++)
        {
            var first = Invoke(menu, "Item", 0);
            if (first == null || !Call(first, "Delete")) break;
        }
        if (Count(menu) != 0) return "не удалось очистить меню " + BobrovUi.MenuName + ".";

        foreach (var command in BobrovUi.PaletteCommands)
            if (Invoke(menu, "AddMenuItem", Count(menu), command.MenuLabel, command.Macro) == null)
                return "не удалось добавить пункт «" + command.MenuLabel + "».";

        if (!OnMenuBar(menu)) Call(menu, "InsertInMenuBar", Count(menuBar));
        return OnMenuBar(menu) ? null : "меню не удалось добавить в строку меню.";
    }

    private static bool OnMenuBar(object menu)
    {
        try { return Convert.ToBoolean(Get(menu, "OnMenuBar") ?? false); }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
    }

    private static int Count(object collection)
    {
        try { return Convert.ToInt32(Get(collection, "Count") ?? 0); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
    }

    private static object? Get(object target, string name)
    {
        try { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null); }
        catch (Exception error) when (IsLateBindingFailure(error)) { return null; }
    }

    private static object? Invoke(object target, string name, params object[] args)
    {
        try { return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args); }
        catch (Exception error) when (IsLateBindingFailure(error)) { return null; }
    }

    // A COM method without a result (Delete, InsertInMenuBar): true when it did not fail.
    private static bool Call(object target, string name, params object[] args)
    {
        try { target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args); return true; }
        catch (Exception error) when (IsLateBindingFailure(error)) { return false; }
    }

    private static bool IsLateBindingFailure(Exception error) =>
        error is TargetInvocationException || error is MissingMemberException || error is ArgumentException
        || error is System.Runtime.InteropServices.COMException || error is InvalidCastException;
}
