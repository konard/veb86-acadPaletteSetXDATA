using System;
using System.Collections.Generic;
using AcadPaletteSetXData.Core;

// Only the host boundary is mocked; tests compile the production entry point.
namespace Autodesk.AutoCAD.Runtime
{
    public interface IExtensionApplication { void Initialize(); void Terminate(); }
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class ExtensionApplicationAttribute : Attribute
    {
        public ExtensionApplicationAttribute(Type type) { }
    }
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class CommandClassAttribute : Attribute
    {
        public CommandClassAttribute(Type type) { }
    }
    [Flags]
    public enum CommandFlags { Session = 1, UsePickSet = 2 }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CommandMethodAttribute : Attribute
    {
        public CommandMethodAttribute(string name, CommandFlags flags) { GlobalName = name; Flags = flags; }
        public string GlobalName { get; }
        public CommandFlags Flags { get; }
    }
}

namespace Autodesk.AutoCAD.ApplicationServices
{
    public sealed class DocumentCollectionEventArgs : EventArgs
    {
        public DocumentCollectionEventArgs(Core.FakeDocument? document) => Document = document;
        public Core.FakeDocument? Document { get; }
    }
    public delegate void DocumentCollectionEventHandler(object sender, DocumentCollectionEventArgs e);
}

namespace Autodesk.AutoCAD.ApplicationServices.Core
{
    public static class Application
    {
        public static FakeDocumentManager DocumentManager { get; } = new FakeDocumentManager();
        public static event EventHandler? Idle;
        public static int IdleSubscribers => Idle?.GetInvocationList().Length ?? 0;
        public static void RaiseIdle() => Idle?.Invoke(null, EventArgs.Empty);
        // Issue #47: false while a command, a script or LISP is active.
        public static bool IsQuiescent { get; set; } = true;
    }
    public sealed class FakeDocumentManager
    {
        public FakeDocument? MdiActiveDocument { get; set; }
        public event DocumentCollectionEventHandler? DocumentCreateStarted;
        public event DocumentCollectionEventHandler? DocumentCreated;
        public event DocumentCollectionEventHandler? DocumentCreationCanceled;
        public int CreationSubscribers =>
            (DocumentCreateStarted?.GetInvocationList().Length ?? 0) + (DocumentCreated?.GetInvocationList().Length ?? 0)
            + (DocumentCreationCanceled?.GetInvocationList().Length ?? 0);
        public void RaiseCreateStarted(FakeDocument document) => DocumentCreateStarted?.Invoke(this, new DocumentCollectionEventArgs(document));
        public void RaiseCreated(FakeDocument document) => DocumentCreated?.Invoke(this, new DocumentCollectionEventArgs(document));
        public void RaiseCreationCanceled(FakeDocument document) => DocumentCreationCanceled?.Invoke(this, new DocumentCollectionEventArgs(document));
    }
    public sealed class FakeDocument
    {
        public FakeEditor Editor { get; } = new FakeEditor();
        public string CommandInProgress { get; set; } = "";
    }
    public sealed class FakeEditor
    {
        public List<string> Messages { get; } = new List<string>();
        public bool IsQuiescent { get; set; } = true;
        public void WriteMessage(string message, params object[] args) => Messages.Add(string.Format(message, args));
    }
}

namespace AcadPaletteSetXData.AutoCAD
{
    internal sealed class AutoCadThemeSource : IThemeSource, IDisposable
    {
        public PaletteTheme CurrentTheme => PaletteTheme.Dark;
        public event EventHandler? ThemeChanged { add { } remove { } }
        public void Dispose() { }
    }
    internal sealed class AutoCadPaletteHost : IPaletteHost
    {
        public static int Creations { get; set; }
        public static AutoCadPaletteHost? Last { get; private set; }
        public AutoCadPaletteHost() { Creations++; Last = this; }
        public bool Visible { get; set; }
        public void ApplyTheme(PaletteTheme theme) { }
        public void Dispose() { }
    }
}
