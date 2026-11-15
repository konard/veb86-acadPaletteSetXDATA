using System;
using System.IO;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// The quiet moment for the startup work of the plugin (issue #47): a drawing is active, no document
/// is being opened, and no command, script or LISP runs. See <see cref="StartupReadiness"/>.
/// <para>
/// With <c>XDATAPALETTE_TRACE=1</c> in the environment the decisions are written to
/// <c>%TEMP%\acadPaletteSetXDATA-trace.log</c> and to the debugger output (DebugView); the trace is off
/// by default and never writes to the command line, which would disturb the startup itself.
/// </para>
/// </summary>
internal static class AutoCadStartup
{
    private static readonly StartupReadiness Readiness = new StartupReadiness();
    private static readonly bool Tracing = Environment.GetEnvironmentVariable("XDATAPALETTE_TRACE") == "1";
    private static bool tracking;
    private static string? lastWaitReason;

    public static void Start()
    {
        if (tracking) return;
        tracking = true;
        var documents = AcApplication.DocumentManager;
        documents.DocumentCreateStarted += OnDocumentCreateStarted;
        documents.DocumentCreated += OnDocumentCreateFinished;
        documents.DocumentCreationCanceled += OnDocumentCreateFinished;
    }

    public static void Stop()
    {
        if (!tracking) return;
        tracking = false;
        var documents = AcApplication.DocumentManager;
        documents.DocumentCreateStarted -= OnDocumentCreateStarted;
        documents.DocumentCreated -= OnDocumentCreateFinished;
        documents.DocumentCreationCanceled -= OnDocumentCreateFinished;
        Readiness.DocumentCreateFinished();
    }

    /// <summary>True when the startup work may run now; otherwise the caller waits for the next Idle.</summary>
    public static bool IsReady(string who)
    {
        string? reason;
        try
        {
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            reason = document == null
                ? Readiness.WaitReason(false, true, true, null)
                : Readiness.WaitReason(true, AcApplication.IsQuiescent, document.Editor.IsQuiescent,
                    Convert.ToString(document.CommandInProgress));
        }
        catch (System.Exception error)
        {
            // The document is being closed or AutoCAD shuts down: try again on the next Idle.
            reason = error.Message;
        }

        if (Tracing && reason != lastWaitReason)
        {
            lastWaitReason = reason;
            Trace(who + ": " + (reason == null ? "AutoCAD готов" : "ожидание — " + reason));
        }
        return reason == null;
    }

    public static void Trace(string message)
    {
        if (!Tracing) return;
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [XDATAPALETTE] " + message;
        System.Diagnostics.Trace.WriteLine(line);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "acadPaletteSetXDATA-trace.log"), line + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void OnDocumentCreateStarted(object sender, DocumentCollectionEventArgs args)
    {
        Readiness.DocumentCreateStarted();
        Trace("чертёж открывается");
    }

    private static void OnDocumentCreateFinished(object sender, DocumentCollectionEventArgs args)
    {
        Readiness.DocumentCreateFinished();
        Trace("чертёж открыт или открытие отменено");
    }
}
