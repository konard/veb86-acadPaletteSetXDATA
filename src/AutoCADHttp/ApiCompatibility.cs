using System;

namespace AutoCADHttp
{
    // These references make compilation verify all three Autodesk API assemblies.
    // They do not register an application, commands, or an HTTP server.
    internal static class ApiCompatibility
    {
        internal static Type CoreApi => typeof(Autodesk.AutoCAD.ApplicationServices.Core.Application);
        internal static Type DatabaseApi => typeof(Autodesk.AutoCAD.DatabaseServices.Database);
        internal static Type ManagedApi => typeof(Autodesk.AutoCAD.EditorInput.Editor);
    }
}
