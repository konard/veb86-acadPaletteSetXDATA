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

namespace Autodesk.AutoCAD.ApplicationServices.Core
{
    public static class Application
    {
        public static FakeDocumentManager DocumentManager { get; } = new FakeDocumentManager();
        public static event EventHandler? Idle;
        public static int IdleSubscribers => Idle?.GetInvocationList().Length ?? 0;
        public static void RaiseIdle() => Idle?.Invoke(null, EventArgs.Empty);
    }
    public sealed class FakeDocumentManager
    {
        public FakeDocument? MdiActiveDocument { get; set; }
    }
    public sealed class FakeDocument
    {
        public FakeEditor Editor { get; } = new FakeEditor();
    }
    public sealed class FakeEditor
    {
        public List<string> Messages { get; } = new List<string>();
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

    // The real menu/ribbon glue needs AcMgd and AdWindows; the tests only check when it is started and stopped.
    internal static class BobrovInterface
    {
        public static bool Running { get; private set; }
        public static int Starts { get; set; }
        public static void Start() { if (Running) return; Running = true; Starts++; }
        public static void Stop() => Running = false;
    }
}
