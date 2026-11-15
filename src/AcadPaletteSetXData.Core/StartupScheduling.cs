namespace AcadPaletteSetXData.Core;

/// <summary>
/// Decides when AutoCAD is quiet enough for the startup work of the plugin (issue #47): the load
/// message in the command line. Since issue #53 the plugin adds no menu and no ribbon tab.
/// <para>
/// The bundle loads the plugin at AutoCAD startup (issue #39). <c>Application.Idle</c> is raised
/// whenever the message queue is empty, also while the startup drawing is still being opened and
/// while a command, a script or LISP (acaddoc.lsp, S::STARTUP) waits for input. Changing the menu
/// bar through COM or a system variable (the removed «Bobrov» menu of issue #39) or writing to the command line
/// at that moment interferes with the active command:
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
