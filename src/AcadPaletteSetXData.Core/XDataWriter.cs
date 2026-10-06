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

/// <summary>Preflights a confirmed field edit against fresh data before writing an atomic group.</summary>
public sealed class XDataWriter : ISelectionWriter
{
    private readonly Func<IReadOnlyList<string>, IXDataWriteTransaction> beginTransaction;

    public XDataWriter(Func<IReadOnlyList<string>, IXDataWriteTransaction> beginTransaction) =>
        this.beginTransaction = beginTransaction ?? throw new ArgumentNullException(nameof(beginTransaction));

    public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit)
    {
        if (handles == null) throw new ArgumentNullException(nameof(handles));
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        if (handles.Count == 0 || handles.Distinct(StringComparer.Ordinal).Count() != handles.Count)
            throw new InvalidOperationException("Нет объектов для записи или повторяются идентификаторы.");

        using var transaction = beginTransaction(handles);
        var snapshots = transaction.Snapshots;
        if (snapshots.Count != handles.Count ||
            !new HashSet<string>(snapshots.Select(s => s.Handle), StringComparer.Ordinal).SetEquals(handles))
            throw new InvalidOperationException("Выделение изменилось; повторите ввод после обновления панели.");

        var changes = new List<EntityDataSnapshot>();
        var patch = new XDataPatch();
        foreach (var before in snapshots)
        {
            var after = patch.Apply(before, edit);
            foreach (var value in after.XData.Where(v => v.TypeCode == 1000))
                if (!(value.Value is string text) || Encoding.UTF8.GetByteCount(text) > 255)
                    throw new InvalidOperationException("Строка XDATA превышает 255 байт; используйте более короткое значение.");
            if (!SameData(before, after)) changes.Add(after);
        }

        // Nothing is opened for write until every target has been parsed and validated.
        if (changes.Count == 0) return;
        foreach (var snapshot in changes) transaction.Write(snapshot);
        transaction.Commit();
    }

    private static bool SameData(EntityDataSnapshot before, EntityDataSnapshot after) =>
        SameValues(before.XData, after.XData) && before.Records.Count == after.Records.Count &&
        before.Records.Zip(after.Records, (a, b) => a.Name == b.Name && SameValues(a.Values, b.Values)).All(equal => equal);

    private static bool SameValues(IReadOnlyList<DataValue> before, IReadOnlyList<DataValue> after) =>
        before.Count == after.Count && before.Zip(after, (a, b) => a.TypeCode == b.TypeCode &&
            (a.Value is byte[] bytes && b.Value is byte[] other ? bytes.SequenceEqual(other) : Equals(a.Value, b.Value))).All(equal => equal);
}
