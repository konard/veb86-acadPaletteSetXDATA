using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>A kind of problem found by <c>VELEC_CHECK</c> (TZXDATA stage 9).</summary>
public enum DrawingIssueKind
{
    /// <summary>The same <c>DEVICE_ID</c> on several objects: the copies get new GUIDs.</summary>
    DuplicateDeviceId,
    /// <summary>A value whose Id is not in the schema.</summary>
    NotInSchema,
    /// <summary>An <c>Enum</c> key that is not in the enum.</summary>
    UnknownEnumKey,
    /// <summary>The stored group code or value does not fit the schema type.</summary>
    TypeMismatch,
    /// <summary>The schema type is unknown to this plugin version.</summary>
    UnknownType,
    /// <summary>The value has the right type but breaks <c>editor</c> (min, max, maxLength).</summary>
    InvalidValue,
    /// <summary>A string of XDATA longer than 255 bytes of UTF-8.</summary>
    StringTooLong,
    /// <summary>XDATA of the object larger than 16 KB.</summary>
    XDataTooLarge,
    /// <summary>A stored <c>Persist</c> result differs from the formula.</summary>
    StalePersist,
    /// <summary>Damaged XDATA v2: an unclosed list, a repeated Id, <c>DEVICE_ID</c> not a GUID, an unknown template.</summary>
    Format,
    /// <summary>XDATA of a newer format version: read-only.</summary>
    NewerFormat,
    /// <summary>Data of the formats of <c>VELEC_MIGRATE</c>.</summary>
    LegacyData
}

/// <summary>One problem of one object.</summary>
public sealed class DrawingIssue
{
    internal DrawingIssue(DrawingIssueKind kind, string handle, string? id, string message) { Kind = kind; Handle = handle; Id = id; Message = message; }
    public DrawingIssueKind Kind { get; }
    public string Handle { get; }
    /// <summary>The parameter, <c>Table/column</c>, or null for the whole object.</summary>
    public string? Id { get; }
    public string Message { get; }
    public override string ToString() => "Объект " + Handle + (Id is null ? "" : ", " + Id) + ": " + Message;
}

/// <summary>What <c>VELEC_CHECK</c> may fix after the report.</summary>
[Flags]
public enum DrawingFix
{
    None = 0,
    /// <summary>New <c>DEVICE_ID</c> for the copies; the object with the smaller handle keeps its ID.</summary>
    Duplicates = 1,
    /// <summary>Rewrite the <c>Persist</c> results of the whole drawing.</summary>
    Persist = 2,
    All = Duplicates | Persist
}

/// <summary>The report of <c>VELEC_CHECK</c>.</summary>
public sealed class DrawingCheckReport
{
    internal DrawingCheckReport(int objects, IReadOnlyList<DrawingIssue> issues, IReadOnlyList<DeviceIdDuplicate> duplicates, IReadOnlyList<string> stalePersist)
    { Objects = objects; Issues = issues; Duplicates = duplicates; StalePersist = stalePersist; }

    public int Objects { get; }
    public IReadOnlyList<DrawingIssue> Issues { get; }
    public IReadOnlyList<DeviceIdDuplicate> Duplicates { get; }
    /// <summary>Objects whose <c>Persist</c> results <see cref="SelectionEdit.RecalculatePersisted"/> rewrites.</summary>
    public IReadOnlyList<string> StalePersist { get; }
    public bool IsClean => Issues.Count == 0;

    /// <summary>Objects changed by <paramref name="fix"/>.</summary>
    public IReadOnlyList<string> Targets(DrawingFix fix)
    {
        var handles = new List<string>();
        if ((fix & DrawingFix.Duplicates) != 0) handles.AddRange(Duplicates.SelectMany(duplicate => duplicate.Copies));
        if ((fix & DrawingFix.Persist) != 0) handles.AddRange(StalePersist);
        return handles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(XDataV2.HandleOrder).ThenBy(handle => handle, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The report for the command line or a text window: problems grouped by kind, then the totals.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        foreach (var group in Issues.GroupBy(issue => issue.Kind).OrderBy(group => group.Key))
        {
            lines.Add(Title(group.Key) + ": " + group.Count().ToString(CultureInfo.InvariantCulture));
            if (group.Key == DrawingIssueKind.DuplicateDeviceId)
            {
                foreach (var duplicate in Duplicates)
                    lines.Add("  " + duplicate.DeviceId + ": сохраняет " + duplicate.Original + ", новый GUID — " + string.Join(", ", duplicate.Copies));
                continue;
            }
            foreach (var issue in group) lines.Add("  " + issue);
        }
        lines.Add(IsClean
            ? string.Format(CultureInfo.InvariantCulture, "Проверено объектов: {0}; замечаний нет.", Objects)
            : string.Format(CultureInfo.InvariantCulture, "Проверено объектов: {0}; замечаний: {1}, объектов с замечаниями: {2}.", Objects, Issues.Count,
                Issues.Select(issue => issue.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        if (Duplicates.Count != 0 || StalePersist.Count != 0)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "Исправимо: дубли DEVICE_ID — объектов {0}, Persist — объектов {1}.",
                Targets(DrawingFix.Duplicates).Count, StalePersist.Count));
        return lines;
    }

    public static string Title(DrawingIssueKind kind) => kind switch
    {
        DrawingIssueKind.DuplicateDeviceId => "Дубли " + XDataV2.DeviceIdKey,
        DrawingIssueKind.NotInSchema => "Данные вне схемы",
        DrawingIssueKind.UnknownEnumKey => "Ключи вне справочника",
        DrawingIssueKind.TypeMismatch => "Несоответствие типу",
        DrawingIssueKind.UnknownType => "Неизвестный тип схемы",
        DrawingIssueKind.InvalidValue => "Значения вне ограничений редактора",
        DrawingIssueKind.StringTooLong => "Строки длиннее " + ParameterTypeRegistry.MaxStringBytes + " байт",
        DrawingIssueKind.XDataTooLarge => "XDATA больше " + XDataV2.MaxXDataBytes + " байт",
        DrawingIssueKind.StalePersist => "Устаревшие результаты Persist",
        DrawingIssueKind.Format => "Повреждённые данные",
        DrawingIssueKind.NewerFormat => "Более новая версия формата",
        DrawingIssueKind.LegacyData => "Прежние форматы (VELEC_MIGRATE)",
        _ => kind.ToString()
    };
}

/// <summary>
/// <c>VELEC_CHECK</c> (TZXDATA stage 9): duplicates of <c>DEVICE_ID</c>, data outside the schema, <c>Enum</c> keys,
/// the limits of 255 bytes per string and 16 KB per object, and the recalculation of <c>Persist</c> over the drawing.
/// </summary>
public sealed class DrawingCheck
{
    private readonly SchemaSnapshot schema;
    private readonly ParameterTypeRegistry types;
    private readonly XDataPatch patch;

    public DrawingCheck(SchemaSnapshot schema, ParameterTypeRegistry? types = null, Func<Guid>? newGuid = null, CultureInfo? culture = null)
    {
        this.schema = schema ?? throw new ArgumentNullException(nameof(schema));
        this.types = types ?? ParameterTypeRegistry.Default;
        patch = new XDataPatch(schema.Schema is null ? null : schema, this.types, newGuid, culture);
    }

    public DrawingCheckReport Run(IReadOnlyList<EntityDataSnapshot> snapshots)
    {
        if (snapshots is null) throw new ArgumentNullException(nameof(snapshots));
        var issues = new List<DrawingIssue>();
        var objects = new List<ObjectValues>();
        var stale = new List<string>();
        foreach (var snapshot in snapshots)
        {
            var values = XDataV2.Read(snapshot, schema, types);
            objects.Add(values);
            Check(snapshot, values, issues, stale);
        }
        var duplicates = XDataV2.FindDuplicateDeviceIds(objects);
        var first = duplicates.SelectMany(duplicate => duplicate.Copies.Select(copy => new DrawingIssue(DrawingIssueKind.DuplicateDeviceId, copy,
            XDataV2.DeviceIdKey, duplicate.DeviceId + " уже у объекта " + duplicate.Original + "; предлагается новый GUID.")));
        return new DrawingCheckReport(snapshots.Count, first.Concat(issues).ToArray(), duplicates, stale);
    }

    /// <summary>Objects that the fixes left as they are, with the reason.</summary>
    public IReadOnlyList<string> FixErrors => fixErrors;
    private readonly List<string> fixErrors = new List<string>();

    /// <summary>The object after the chosen fixes, or the same instance; an object that cannot be fixed is kept and listed in <see cref="FixErrors"/>.</summary>
    public EntityDataSnapshot Fix(EntityDataSnapshot snapshot, DrawingCheckReport report, DrawingFix fix)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (report is null) throw new ArgumentNullException(nameof(report));
        var result = snapshot;
        try
        {
            if ((fix & DrawingFix.Duplicates) != 0 && report.Duplicates.Any(duplicate => duplicate.Copies.Contains(snapshot.Handle, StringComparer.OrdinalIgnoreCase)))
                result = patch.Apply(result, SelectionEdit.NewDeviceId(), null);
            if ((fix & DrawingFix.Persist) != 0 && schema.Schema != null && report.StalePersist.Contains(snapshot.Handle, StringComparer.OrdinalIgnoreCase))
                result = patch.Apply(result, SelectionEdit.RecalculatePersisted(), null);
        }
        catch (InvalidOperationException error)
        {
            fixErrors.Add(error.Message);
            return snapshot;
        }
        return XDataWriter.SameData(snapshot, result) ? snapshot : result;
    }

    private void Check(EntityDataSnapshot snapshot, ObjectValues values, List<DrawingIssue> issues, List<string> stale)
    {
        var handle = snapshot.Handle;
        var covered = new HashSet<string>(StringComparer.Ordinal);
        void Add(DrawingIssueKind kind, string? id, string message) => issues.Add(new DrawingIssue(kind, handle, id, message));

        if (values.Format == XDataFormat.Newer) Add(DrawingIssueKind.NewerFormat, null, "XDATA " + DrawingNames.RegApp + " записан более новой версией формата; только чтение.");
        if (values.XDataSize > XDataV2.MaxXDataBytes)
            Add(DrawingIssueKind.XDataTooLarge, null, "оценка размера XDATA " + values.XDataSize.ToString(CultureInfo.InvariantCulture) + " байт.");
        foreach (var value in snapshot.XData.Where(item => item.TypeCode == 1000 && item.Value is string))
        {
            var bytes = ParameterTypeRegistry.Utf8Length((string)value.Value);
            if (bytes > ParameterTypeRegistry.MaxStringBytes)
                Add(DrawingIssueKind.StringTooLong, null, "строка XDATA " + bytes.ToString(CultureInfo.InvariantCulture) + " байт: «" + Short((string)value.Value) + "».");
        }
        foreach (var value in values.Values)
        {
            if (value.Warning != null) covered.Add(value.Id + ": " + value.Warning);
            CheckValue(value, value.Id, Add);
            if (value.Rows is null) continue;
            foreach (var cell in value.Rows.SelectMany(row => row.Cells))
            {
                if (cell.Warning != null) covered.Add(value.Id + "/" + cell.Id + ": " + cell.Warning);
                CheckValue(cell, value.Id + "/" + cell.Id, Add);
            }
        }
        foreach (var warning in values.Warnings)
        {
            if (covered.Contains(warning) || warning.StartsWith("Размер XDATA", StringComparison.Ordinal) ||
                values.Format == XDataFormat.Newer && warning.Contains("более новой версией")) continue;
            Add(DrawingIssueKind.Format, null, warning);
        }
        var sources = SchemaMigration.FindSources(snapshot);
        if (sources.Count != 0) Add(DrawingIssueKind.LegacyData, null, string.Join(", ", sources) + "; перенесите командой VELEC_MIGRATE.");
        if (snapshot.Warning != null) Add(DrawingIssueKind.Format, null, snapshot.Warning);

        if (values.Format != XDataFormat.V2 || schema.Schema is null) return;
        EntityDataSnapshot recalculated;
        try { recalculated = patch.Apply(snapshot, SelectionEdit.RecalculatePersisted(), null); }
        catch (InvalidOperationException error)
        {
            Add(DrawingIssueKind.StalePersist, null, "пересчёт Persist невозможен: " + error.Message);
            return;
        }
        if (XDataWriter.SameData(snapshot, recalculated)) return;
        stale.Add(handle);
        var after = XDataV2.Read(recalculated, schema, types);
        var changed = after.Values.Where(value => !value.IsTable && (values.Find(value.Id) is null || values.Find(value.Id)!.Text != value.Text))
            .Select(value => value.Id).ToArray();
        Add(DrawingIssueKind.StalePersist, changed.Length == 0 ? null : string.Join(", ", changed), "результат формулы отличается от записанного.");
    }

    private void CheckValue(ObjectValue value, string id, Action<DrawingIssueKind, string?, string> add)
    {
        switch (value.State)
        {
            case ObjectValueState.NotInSchema: add(DrawingIssueKind.NotInSchema, id, "Id не описан в схеме, значение «" + Short(value.Text) + "»."); return;
            case ObjectValueState.UnknownEnumKey: add(DrawingIssueKind.UnknownEnumKey, id, value.Warning ?? "ключ «" + Short(value.Text) + "» отсутствует в справочнике."); return;
            case ObjectValueState.TypeMismatch: add(DrawingIssueKind.TypeMismatch, id, value.Warning ?? "значение не соответствует типу схемы."); return;
            case ObjectValueState.UnknownType: add(DrawingIssueKind.UnknownType, id, value.Warning ?? "тип схемы неизвестен этой версии модуля."); return;
        }
        if (value.IsTable || value.Parameter is null || value.Type is null || schema.Schema is null) return;
        if (value.Type.Kind == ParameterKind.Table || value.Type.Kind == ParameterKind.CalculatedString) return;
        var error = XDataPatch.Check(value.Parameter, value.Type, value.Value, schema.Schema, out _);
        if (error != null) add(DrawingIssueKind.InvalidValue, id, "«" + Short(value.Text) + "»: " + error);
    }

    private static string Short(string text) => text.Length <= 40 ? text : text.Substring(0, 40) + "…";
}
