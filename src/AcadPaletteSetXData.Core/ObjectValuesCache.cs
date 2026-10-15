using System;
using System.Collections.Generic;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>A selection source that also provides the value cache of its document (TZXDATA 10.2).</summary>
public interface IObjectValuesSource
{
    /// <summary>Null when no document is active.</summary>
    ObjectValuesCache? Values { get; }
}

/// <summary>
/// Value cache of one document (TZXDATA 10.2): handle → <see cref="ObjectValues"/>, bounded by LRU.
/// XDATA of an object is parsed once; a fresh read with equal values reuses the entry without parsing.
/// A write invalidates only the changed objects; a new schema drops the whole cache.
/// </summary>
public sealed class ObjectValuesCache
{
    public const int DefaultCapacity = 10000;
    private readonly SchemaCache schema;
    private readonly ParameterTypeRegistry types;
    private readonly Dictionary<string, LinkedListNode<ObjectValues>> entries = new Dictionary<string, LinkedListNode<ObjectValues>>(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<ObjectValues> order = new LinkedList<ObjectValues>();

    public ObjectValuesCache(SchemaCache schema, int capacity = DefaultCapacity, ParameterTypeRegistry? types = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.schema = schema ?? throw new ArgumentNullException(nameof(schema));
        this.types = types ?? ParameterTypeRegistry.Default;
        Capacity = capacity;
        schema.Invalidated += (_, __) => Clear();
    }

    public int Capacity { get; }
    public int Count => entries.Count;
    /// <summary>How many times XDATA was parsed; used by tests to check that the cache is reused (TZXDATA 10.6).</summary>
    public int ParseCount { get; private set; }
    public SchemaSnapshot Schema => schema.Get();

    /// <summary>Values of the object; parses its XDATA only if the cached entry has different values or another schema.</summary>
    public ObjectValues Get(EntityDataSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var current = schema.Get();
        if (entries.TryGetValue(snapshot.Handle, out var node))
        {
            var cached = node.Value;
            if (ReferenceEquals(cached.Schema, current) && SameValues(cached.XData, snapshot.XData) &&
                cached.EntityType == snapshot.EntityType)
            {
                order.Remove(node); order.AddFirst(node);
                return cached;
            }
            Remove(node);
        }
        ParseCount++;
        var values = XDataV2.Read(snapshot, current, types);
        entries.Add(snapshot.Handle, order.AddFirst(values));
        while (entries.Count > Capacity) Remove(order.Last!);
        return values;
    }

    public IReadOnlyList<ObjectValues> Get(IEnumerable<EntityDataSnapshot> snapshots) => snapshots.Select(Get).ToArray();

    /// <summary>Cached values without a database read; null if the object was not read or was invalidated.</summary>
    public ObjectValues? Find(string handle) => entries.TryGetValue(handle, out var node) ? node.Value : null;

    public void Invalidate(IEnumerable<string> handles)
    {
        foreach (var handle in handles)
            if (entries.TryGetValue(handle, out var node)) Remove(node);
    }

    public void Clear() { entries.Clear(); order.Clear(); }

    private void Remove(LinkedListNode<ObjectValues> node)
    {
        entries.Remove(node.Value.Handle);
        order.Remove(node);
    }

    internal static bool SameValues(IReadOnlyList<DataValue> left, IReadOnlyList<DataValue> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i]; var b = right[i];
            if (a.TypeCode != b.TypeCode) return false;
            if (a.Value is byte[] bytes && b.Value is byte[] other ? !bytes.SequenceEqual(other) : !Equals(a.Value, b.Value)) return false;
        }
        return true;
    }
}
