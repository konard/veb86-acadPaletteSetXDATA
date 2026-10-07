using System;

namespace acadPaletteSetXDATA
{
    // These references make compilation verify all three Autodesk API assemblies.
    // They do not register an application or commands.
    internal static class ApiCompatibility
    {
        internal static Type CoreApi => typeof(Autodesk.AutoCAD.ApplicationServices.Core.Application);
        internal static Type DatabaseApi => typeof(Autodesk.AutoCAD.DatabaseServices.Database);
        internal static Type ManagedApi => typeof(Autodesk.AutoCAD.EditorInput.Editor);
    }
}
