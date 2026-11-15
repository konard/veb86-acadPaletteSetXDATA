using System;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Decides when AutoCAD is quiet enough for the startup work of the plugin (issue #47): the load
/// message, the «Bobrov» menu, <c>MENUBAR</c> and the ribbon tab.
/// <para>
/// The bundle loads the plugin at AutoCAD startup (issue #39). <c>Application.Idle</c> is raised
/// whenever the message queue is empty, also while the startup drawing is still being opened and
/// while a command, a script or LISP (acaddoc.lsp, S::STARTUP) waits for input. Changing the menu
/// bar through COM or a system variable at that moment interferes with the active command:
/// «Выберите объекты» flickers in the command line and the drawing does not open until the
/// prompt is cancelled. The startup work therefore waits for an Idle where nothing is running.
/// </para>
/// </summary>
public sealed class StartupReadiness
{
    private bool openingDocument;

    /// <summary>Between <c>DocumentCreateStarted</c> and <c>DocumentCreated</c>/<c>DocumentCreationCanceled</c>.</summary>
    public bool OpeningDocument => openingDocument;

    public void DocumentCreateStarted() => openingDocument = true;

    public void DocumentCreateFinished() => openingDocument = false;

    /// <summary>
    /// True when a drawing is active, no document is being opened and neither AutoCAD nor the editor
    /// runs a command, a script or LISP (<c>Application.IsQuiescent</c>, <c>Editor.IsQuiescent</c>,
    /// <c>Document.CommandInProgress</c>).
    /// </summary>
    public bool IsReady(bool hasActiveDocument, bool applicationQuiescent, bool editorQuiescent, string? commandInProgress) =>
        hasActiveDocument && !openingDocument && applicationQuiescent && editorQuiescent
        && string.IsNullOrEmpty(commandInProgress);

    /// <summary>Why the startup work waits; null when it may run. For the trace only.</summary>
    public string? WaitReason(bool hasActiveDocument, bool applicationQuiescent, bool editorQuiescent, string? commandInProgress)
    {
        if (!hasActiveDocument) return "нет активного чертежа";
        if (openingDocument) return "чертёж открывается";
        if (!applicationQuiescent) return "AutoCAD выполняет команду, сценарий или LISP";
        if (!editorQuiescent) return "редактор ждёт ввода";
        if (!string.IsNullOrEmpty(commandInProgress)) return "выполняется команда " + commandInProgress;
        return null;
    }
}

/// <summary>
/// Runs the «Bobrov» menu and ribbon installation on a quiet Idle (issue #47).
/// <list type="bullet">
/// <item>The first request comes from the load; a workspace switch (<c>WSCURRENT</c>) requests a check again.</item>
/// <item>The installation runs only when <see cref="StartupReadiness"/> allows it; otherwise it keeps waiting.</item>
/// <item>A request raised by the installation itself (the menu bar or <c>MENUBAR</c> changes <c>WSCURRENT</c>)
/// is ignored, so the installation cannot schedule itself again and again.</item>
/// <item>The installer is expected to change only what is missing: a check that finds the menu and the tab
/// in place changes nothing, so a repeated <c>WSCURRENT</c> settles instead of looping.</item>
/// </list>
/// </summary>
public sealed class BobrovInstallScheduler
{
    private readonly Action install;
    private bool pending;
    private bool installing;

    public BobrovInstallScheduler(Action install) =>
        this.install = install ?? throw new ArgumentNullException(nameof(install));

    /// <summary>True while an installation waits for a quiet Idle; the host keeps its Idle handler only then.</summary>
    public bool Pending => pending;

    /// <summary>True while the installation runs.</summary>
    public bool Installing => installing;

    /// <summary>How many times the installation ran; for the trace and the tests.</summary>
    public int Runs { get; private set; }

    /// <summary>Asks for an installation on a quiet Idle; returns false when the request came from the installation itself.</summary>
    public bool Request()
    {
        if (installing) return false;
        pending = true;
        return true;
    }

    public void Cancel() => pending = false;

    /// <summary>Called on Idle: installs when something is pending and AutoCAD is ready. Returns true when it ran.</summary>
    public bool OnIdle(bool ready)
    {
        if (!pending || installing || !ready) return false;
        pending = false;
        installing = true;
        try
        {
            Runs++;
            install();
        }
        finally
        {
            installing = false;
        }
        return true;
    }
}
