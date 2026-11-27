using System;
using System.Collections.Generic;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// The drawing side of the navigator (issue #66), implemented by the AutoCAD layer: it reads the objects with XDATA
/// of the active document, says when they may have changed and selects an object by its handle. The navigator itself
/// has no AutoCAD types.
/// </summary>
public interface INavigatorSource
{
    /// <summary>
    /// The objects may have changed: a command that changes the drawing ended, an Undo/Redo, another document became
    /// active or was closed. Raised on the UI thread when AutoCAD is idle.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Reads the objects of the active document on the UI thread, under the document lock and a read transaction.
    /// <paramref name="full"/> reads every object again; otherwise only the objects changed since the last read are read.
    /// Unchanged XDATA is not parsed again: the values come from the cache of the existing parser.
    /// </summary>
    NavigatorSnapshot Read(bool full);

    /// <summary>
    /// Finds the object by handle in the active document and passes it to the standard selection of AutoCAD;
    /// <paramref name="zoom"/> also shows it. False when the object does not exist or is erased.
    /// </summary>
    bool Select(string handle, bool zoom);
}

/// <summary>An object of the drawing read for the navigator: its parsed values (from the cache) and its type name.</summary>
public sealed class NavigatorItem
{
    public NavigatorItem(ObjectValues values, string? typeName = null)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
        TypeName = typeName;
    }

    public ObjectValues Values { get; }
    public string Handle => Values.Handle;
    public string? TypeName { get; }
}

/// <summary>The objects of one document, detached from AutoCAD: no ObjectId, DBObject or transaction is kept.</summary>
public sealed class NavigatorSnapshot
{
    public static NavigatorSnapshot Empty { get; } = new NavigatorSnapshot(null, Array.Empty<NavigatorItem>());

    public NavigatorSnapshot(string? document, IReadOnlyList<NavigatorItem> items, SchemaSnapshot? schema = null, string? error = null, int failed = 0)
    {
        Document = document; Items = items ?? Array.Empty<NavigatorItem>(); Schema = schema; Error = error; Failed = failed;
    }

    /// <summary>
    /// A key of the document the objects come from (not the document itself): when it changes the navigator drops
    /// everything it kept of the previous drawing. Null — no document is open.
    /// </summary>
    public string? Document { get; }
    public IReadOnlyList<NavigatorItem> Items { get; }
    /// <summary>The schema of the document, to check the references of a profile.</summary>
    public SchemaSnapshot? Schema { get; }
    /// <summary>A problem of reading the drawing, shown in the navigator.</summary>
    public string? Error { get; }
    /// <summary>Objects that could not be read; the others are shown.</summary>
    public int Failed { get; }
}
