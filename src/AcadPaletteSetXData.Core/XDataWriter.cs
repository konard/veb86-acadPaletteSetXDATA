using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>A locked DWG transaction. Disposal without Commit must discard all staged changes.</summary>
public interface IXDataWriteTransaction : IDisposable
{
    IReadOnlyList<EntityDataSnapshot> Snapshots { get; }
    void Write(EntityDataSnapshot snapshot);
    void Commit();
}

/// <summary>
/// Preflights a confirmed field edit against fresh data before writing an atomic group: the limits of
/// TZXDATA 6.3 (255 bytes per string, 16 KB of XDATA per object) are checked for every object first,
/// and any violation refuses the whole operation.
/// </summary>
public sealed class XDataWriter : ISelectionWriter
{
    private readonly Func<IReadOnlyList<string>, IXDataWriteTransaction> beginTransaction;
    private readonly ObjectValuesCache? values;
    private readonly Func<Guid>? newGuid;

    public XDataWriter(Func<IReadOnlyList<string>, IXDataWriteTransaction> beginTransaction) : this(beginTransaction, null) { }

    /// <param name="values">Value cache of the document: gives the schema for v2 edits and is invalidated for written objects.</param>
    public XDataWriter(Func<IReadOnlyList<string>, IXDataWriteTransaction> beginTransaction, ObjectValuesCache? values, Func<Guid>? newGuid = null)
    {
        this.beginTransaction = beginTransaction ?? throw new ArgumentNullException(nameof(beginTransaction));
        this.values = values;
        this.newGuid = newGuid;
    }

    public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit)
    {
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        XDataPatch? patch = null;
        WriteEach(handles, before =>
        {
            patch ??= new XDataPatch(edit.IsV2 ? values?.Schema : null, newGuid: newGuid);
            return patch.Apply(before, edit, edit.IsV2 ? values?.Get(before) : null);
        });
    }

    /// <summary>
    /// Changes every object in one transaction (one Undo step, TZXDATA П14). <paramref name="change"/> gets fresh data
    /// and returns the new data or the same snapshot; the limits are checked for every object before anything is
    /// written. Returns the written objects.
    /// </summary>
    public IReadOnlyList<EntityDataSnapshot> WriteEach(IReadOnlyList<string> handles, Func<EntityDataSnapshot, EntityDataSnapshot> change)
    {
        if (handles == null) throw new ArgumentNullException(nameof(handles));
        if (change == null) throw new ArgumentNullException(nameof(change));
        if (handles.Count == 0 || handles.Distinct(StringComparer.Ordinal).Count() != handles.Count)
            throw new InvalidOperationException("Нет объектов для записи или повторяются идентификаторы.");

        using var transaction = beginTransaction(handles);
        var snapshots = transaction.Snapshots;
        if (snapshots.Count != handles.Count ||
            !new HashSet<string>(snapshots.Select(s => s.Handle), StringComparer.Ordinal).SetEquals(handles))
            throw new InvalidOperationException("Выделение изменилось; повторите ввод после обновления панели.");

        var changes = new List<EntityDataSnapshot>();
        foreach (var before in snapshots)
        {
            var after = change(before);
            if (ReferenceEquals(after, before)) continue;
            if (!string.Equals(after.Handle, before.Handle, StringComparison.Ordinal))
                throw new InvalidOperationException("Изменение объекта " + before.Handle + " вернуло данные другого объекта.");
            foreach (var value in after.XData.Where(v => v.TypeCode == 1000))
                if (!(value.Value is string text) || Encoding.UTF8.GetByteCount(text) > ParameterTypeRegistry.MaxStringBytes)
                    throw new InvalidOperationException("Строка XDATA превышает 255 байт; используйте более короткое значение.");
            if (SameData(before, after)) continue;
            var size = XDataV2.EstimateSize(after.XData);
            // The estimate is conservative: an object already above it may still shrink.
            if (size > XDataV2.MaxXDataBytes && size > XDataV2.EstimateSize(before.XData))
                throw new InvalidOperationException("XDATA объекта " + before.Handle + " превысит " + XDataV2.MaxXDataBytes +
                    " байт (оценка " + size + "); операция отменена для всех объектов.");
            changes.Add(after);
        }

        // Nothing is opened for write until every target has been parsed and validated.
        if (changes.Count == 0) return changes;
        foreach (var snapshot in changes) transaction.Write(snapshot);
        transaction.Commit();
        values?.Invalidate(changes.Select(snapshot => snapshot.Handle));
        return changes;
    }

    /// <summary>Same XDATA and the same records in the same order; a removed or added record is a change.</summary>
    internal static bool SameData(EntityDataSnapshot before, EntityDataSnapshot after) =>
        SameValues(before.XData, after.XData) && before.Records.Count == after.Records.Count &&
        before.Records.Zip(after.Records, (a, b) => a.Name == b.Name && SameValues(a.Values, b.Values)).All(equal => equal);

    internal static bool SameValues(IReadOnlyList<DataValue> before, IReadOnlyList<DataValue> after) =>
        before.Count == after.Count && before.Zip(after, (a, b) => a.TypeCode == b.TypeCode &&
            (a.Value is byte[] bytes && b.Value is byte[] other ? bytes.SequenceEqual(other) : Equals(a.Value, b.Value))).All(equal => equal);
}
