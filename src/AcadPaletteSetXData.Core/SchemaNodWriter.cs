using System;
using System.Collections.Generic;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Records under <c>VELEC</c> as one transaction (AutoCAD: one document lock, one transaction, one Undo step).
/// Paths are those of <see cref="SchemaNod"/>: <c>INFO</c>, <c>PARAMETERS/Power</c>.
/// </summary>
public interface ISchemaNodTransaction : IDisposable
{
    /// <summary>Current records; read once when the transaction starts.</summary>
    IReadOnlyList<DataRecord> Records { get; }
    /// <summary>Problems met while reading <see cref="Records"/>.</summary>
    IReadOnlyList<string> Warnings { get; }
    /// <summary>Creates the record (and its section dictionary) or replaces its values.</summary>
    void Write(DataRecord record);
    void Erase(string path);
    /// <summary>Without a commit nothing is written.</summary>
    void Commit();
}

public sealed class SchemaNodWriteResult
{
    public SchemaNodWriteResult(IReadOnlyList<string> written, IReadOnlyList<string> erased)
    {
        Written = written; Erased = erased;
    }
    public IReadOnlyList<string> Written { get; }
    public IReadOnlyList<string> Erased { get; }
    /// <summary>False when the drawing already had this schema: nothing was committed and no Undo step was made.</summary>
    public bool Changed => Written.Count != 0 || Erased.Count != 0;
}

public static class SchemaNodWriter
{
    /// <summary>
    /// Makes the NOD schema equal to <paramref name="schema"/> touching only records that differ:
    /// changed and new objects are written, objects absent from the schema are erased, foreign records
    /// under <c>VELEC</c> are kept. When the schema is unchanged the import stamp is not rewritten.
    /// </summary>
    /// <exception cref="InvalidOperationException">The schema has validation errors (TZXDATA 4.5).</exception>
    public static SchemaNodWriteResult Write(ISchemaNodTransaction transaction, ParameterSchema schema, SchemaNodInfo info, ParameterTypeRegistry? types = null)
    {
        if (transaction is null) throw new ArgumentNullException(nameof(transaction));
        var errors = new SchemaValidator(types).Validate(schema).Where(issue => issue.Severity == SchemaIssueSeverity.Error).ToList();
        if (errors.Count != 0) throw new InvalidOperationException("Схема содержит ошибки: " + string.Join("; ", errors.Select(error => error.ToString())));

        var target = SchemaNod.ToRecords(schema, info);
        var existing = new Dictionary<string, DataRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in transaction.Records)
            if (!existing.ContainsKey(record.Name)) existing.Add(record.Name, record);

        var written = new List<string>();
        var erased = new List<string>();
        foreach (var record in target)
        {
            if (record.Name == SchemaNod.InfoName) continue;
            if (!existing.TryGetValue(record.Name, out var old) || !SameValues(old.Values, record.Values) || old.Name != record.Name)
                written.Add(record.Name);
        }
        var targetNames = new HashSet<string>(target.Select(record => record.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var record in transaction.Records)
            if (SchemaNod.SplitPath(record.Name) != null && !targetNames.Contains(record.Name) && !erased.Contains(record.Name, StringComparer.OrdinalIgnoreCase))
                erased.Add(record.Name);
        // A record whose key differs only in case is erased first and written with the canonical Id.
        foreach (var name in written.Where(name => existing.TryGetValue(name, out var old) && old.Name != name).ToList())
            erased.Add(existing[name].Name);

        var infoRecord = target[0];
        var infoChanged = !existing.TryGetValue(SchemaNod.InfoName, out var oldInfo) || !SameInfo(oldInfo, infoRecord);
        if (written.Count == 0 && erased.Count == 0 && !infoChanged) return new SchemaNodWriteResult(written, erased);

        foreach (var name in erased) transaction.Erase(name);
        transaction.Write(infoRecord);
        written.Insert(0, infoRecord.Name);
        foreach (var record in target.Where(record => written.Contains(record.Name) && record.Name != SchemaNod.InfoName)) transaction.Write(record);
        transaction.Commit();
        return new SchemaNodWriteResult(written, erased);
    }

    /// <summary>INFO differs in more than its import stamp (time, source, mode).</summary>
    private static bool SameInfo(DataRecord old, DataRecord target) =>
        SameValues(WithoutStamp(old.Values), WithoutStamp(target.Values));

    private static IReadOnlyList<DataValue> WithoutStamp(IReadOnlyList<DataValue> values)
    {
        var result = new List<DataValue>();
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].TypeCode == SchemaNod.SystemNameCode && values[i].Value is string name && SchemaNod.StampFields.Contains(name))
            {
                // Skip the name and its value fragments.
                var code = i + 1 < values.Count ? values[i + 1].TypeCode : 0;
                while (i + 1 < values.Count && values[i + 1].TypeCode == code) i++;
                continue;
            }
            result.Add(values[i]);
        }
        return result;
    }

    internal static bool SameValues(IReadOnlyList<DataValue> left, IReadOnlyList<DataValue> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].TypeCode != right[i].TypeCode) return false;
            if (!SameValue(left[i].Value, right[i].Value)) return false;
        }
        return true;
    }

    private static bool SameValue(object left, object right)
    {
        if (Equals(left, right)) return true;
        // AutoCAD may return 290 as short/int and 90 as long; compare numbers by value.
        if (left is bool || right is bool) return ToLong(left) is long a && ToLong(right) is long b && (a != 0) == (b != 0);
        return ToLong(left) is long x && ToLong(right) is long y && x == y;
    }

    private static long? ToLong(object value) => value switch
    {
        bool flag => flag ? 1 : 0,
        short s => s,
        int n => n,
        long l => l,
        byte b => b,
        _ => null
    };
}
