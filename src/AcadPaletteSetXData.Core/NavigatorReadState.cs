using System;
using System.Collections.Generic;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Issue #66: what the navigator read of one document and what changed since then, without AutoCAD types (the AutoCAD
/// adapter uses <c>ObjectId</c> as <typeparamref name="TId"/>). Events of the database only record identities here;
/// the objects are opened by the next read. The first read, a full read and a read after Undo/Redo read every object;
/// otherwise only the changed objects are read again.
/// </summary>
public sealed class NavigatorReadState<TId, TItem> where TId : notnull
{
    /// <summary>Above this number of changed nested objects (attributes, XRecords) the next read reads every object.</summary>
    public const int DefaultNestedLimit = 2000;

    private readonly Dictionary<TId, TItem> objects = new Dictionary<TId, TItem>();
    private readonly List<TId> order = new List<TId>();
    private readonly HashSet<TId> failed = new HashSet<TId>();
    private readonly HashSet<TId> dirty = new HashSet<TId>();
    private readonly HashSet<TId> nested = new HashSet<TId>();
    private readonly int nestedLimit;
    private bool all;

    public NavigatorReadState(string key, int nestedLimit = DefaultNestedLimit)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        this.nestedLimit = nestedLimit;
    }

    /// <summary>The key of the document in <see cref="NavigatorSnapshot.Document"/>; a reopened drawing gets a new one.</summary>
    public string Key { get; }
    /// <summary>A read was completed: changes are recorded from now on.</summary>
    public bool Loaded { get; private set; }
    /// <summary>Something changed since the last read: the navigator should be refreshed.</summary>
    public bool Pending { get; private set; }
    /// <summary>The next read reads every object.</summary>
    public bool NeedsFullRead => !Loaded || all;
    public int FailedCount => failed.Count;
    public int Count => objects.Count;
    /// <summary>The objects read, in the order they were first read.</summary>
    public IEnumerable<TItem> Items => order.Select(id => objects[id]);

    /// <summary>An object of the space changed, was added, erased or restored. False before the first read (nothing to update).</summary>
    public bool MarkObject(TId id)
    {
        if (!Loaded) return false;
        dirty.Add(id);
        Pending = true;
        return true;
    }

    /// <summary>
    /// An object inside an object changed (an XRecord of its extension dictionary): the reader resolves its owner. Too many
    /// of them give a full read.
    /// </summary>
    public bool MarkNested(TId id)
    {
        if (!Loaded) return false;
        if (nested.Count < nestedLimit) nested.Add(id);
        else all = true;
        Pending = true;
        return true;
    }

    /// <summary>Undo/Redo, a change of the space: every object is read again.</summary>
    public bool MarkAll()
    {
        if (!Loaded) return false;
        all = true;
        Pending = true;
        return true;
    }

    /// <summary>Nothing of the objects changed but their values must be computed again (the schema changed).</summary>
    public bool MarkValues()
    {
        if (!Loaded) return false;
        Pending = true;
        return true;
    }

    /// <summary>
    /// Starts a read. Returns null when every object must be read (the objects read before are forgotten); otherwise the
    /// changed objects and the nested objects whose owners must be resolved by the reader. A read that does not reach
    /// <see cref="EndRead"/> (AutoCAD failed) makes the next read a full one.
    /// </summary>
    public NavigatorReadPlan<TId>? BeginRead(bool full)
    {
        NavigatorReadPlan<TId>? plan = null;
        if (full || NeedsFullRead)
        {
            objects.Clear();
            order.Clear();
            failed.Clear();
        }
        else plan = new NavigatorReadPlan<TId>(dirty.ToArray(), nested.ToArray());
        dirty.Clear();
        nested.Clear();
        all = false;
        Pending = false;
        Loaded = false;
        return plan;
    }

    /// <summary>The object was read: it is shown (again).</summary>
    public void Store(TId id, TItem item)
    {
        if (!objects.ContainsKey(id)) order.Add(id);
        objects[id] = item;
        failed.Remove(id);
    }

    /// <summary>The object is erased, moved out of the space or has no data of the plugin: it is not shown.</summary>
    public void Drop(TId id)
    {
        if (objects.Remove(id)) order.Remove(id);
        failed.Remove(id);
    }

    /// <summary>The object could not be read: it is counted, the others are shown.</summary>
    public void Fail(TId id)
    {
        if (objects.Remove(id)) order.Remove(id);
        failed.Add(id);
    }

    /// <summary>The read is over: changes are recorded for the next one.</summary>
    public void EndRead() => Loaded = true;
}

/// <summary>The objects a partial read of the navigator reads again.</summary>
public sealed class NavigatorReadPlan<TId>
{
    public NavigatorReadPlan(IReadOnlyCollection<TId> objects, IReadOnlyCollection<TId> nested)
    {
        Objects = objects;
        Nested = nested;
    }

    public IReadOnlyCollection<TId> Objects { get; }
    /// <summary>Nested objects (XRecords, dictionaries): the reader reads the object of the space they belong to.</summary>
    public IReadOnlyCollection<TId> Nested { get; }
}
