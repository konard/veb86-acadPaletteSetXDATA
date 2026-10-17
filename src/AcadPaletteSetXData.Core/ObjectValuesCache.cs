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
/// The entry also keeps the XDATA tree of the object (<see cref="Parse"/>), so that a repeated selection parses nothing (10.6).
/// A write invalidates only the changed objects; a new schema drops the whole cache.
/// </summary>
public sealed class ObjectValuesCache
{
    public const int DefaultCapacity = 10000;
    private readonly SchemaCache schema;
    private readonly ParameterTypeRegistry types;
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> order = new LinkedList<Entry>();

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
    /// <summary>How many times <see cref="XDataParser"/> built the XDATA tree of an object; a repeated selection keeps it (TZXDATA 10.6).</summary>
    public int TreeParseCount { get; private set; }
    public SchemaSnapshot Schema => schema.Get();

    /// <summary>Values of the object; parses its XDATA only if the cached entry has different values or another schema.</summary>
    public ObjectValues Get(EntityDataSnapshot snapshot) => GetEntry(snapshot).Values;

    public IReadOnlyList<ObjectValues> Get(IEnumerable<EntityDataSnapshot> snapshots) => snapshots.Select(Get).ToArray();

    /// <summary>
    /// The XDATA tree, the fields of the legacy formats and the format warnings of the object (<see cref="XDataParser"/>)
    /// for the «XDATA» tab and the legacy rows of the VST. Like the values, it is parsed once per version of the object.
    /// Every call returns a new <see cref="ParsedEntityData"/> with its own warnings that shares the cached tree,
    /// so the selection can add its warnings (duplicates of <c>DEVICE_ID</c>, formulas) without changing the cache.
    /// </summary>
    public ParsedEntityData Parse(EntityDataSnapshot snapshot)
    {
        var entry = GetEntry(snapshot);
        if (entry.Parsed is null || entry.Warning != snapshot.Warning || !SameRecords(entry.Records!, snapshot.Records))
        {
            TreeParseCount++;
            entry.Parsed = new XDataParser().Parse(snapshot);
            entry.Records = snapshot.Records;
            entry.Warning = snapshot.Warning;
        }
        return entry.Parsed.Copy();
    }

    /// <summary>Cached values without a database read; null if the object was not read or was invalidated.</summary>
    public ObjectValues? Find(string handle) => entries.TryGetValue(handle, out var node) ? node.Value.Values : null;

    private Entry GetEntry(EntityDataSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var current = schema.Get();
        if (entries.TryGetValue(snapshot.Handle, out var node))
        {
            var cached = node.Value.Values;
            if (ReferenceEquals(cached.Schema, current) && SameValues(cached.XData, snapshot.XData) &&
                cached.EntityType == snapshot.EntityType)
            {
                order.Remove(node); order.AddFirst(node);
                return node.Value;
            }
            Remove(node);
        }
        ParseCount++;
        var entry = new Entry(XDataV2.Read(snapshot, current, types));
        entries.Add(snapshot.Handle, order.AddFirst(entry));
        while (entries.Count > Capacity) Remove(order.Last!);
        return entry;
    }

    public void Invalidate(IEnumerable<string> handles)
    {
        foreach (var handle in handles)
            if (entries.TryGetValue(handle, out var node)) Remove(node);
    }

    public void Clear() { entries.Clear(); order.Clear(); }

    private void Remove(LinkedListNode<Entry> node)
    {
        entries.Remove(node.Value.Values.Handle);
        order.Remove(node);
    }

    private static bool SameRecords(IReadOnlyList<DataRecord> left, IReadOnlyList<DataRecord> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (left[i].Name != right[i].Name || !SameValues(left[i].Values, right[i].Values)) return false;
        return true;
    }

    private sealed class Entry
    {
        public Entry(ObjectValues values) => Values = values;
        public ObjectValues Values { get; }
        /// <summary>The tree of <see cref="Parse"/> and the XRecords and read warning it was built from; null until asked.</summary>
        public ParsedEntityData? Parsed;
        public IReadOnlyList<DataRecord>? Records;
        public string? Warning;
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
