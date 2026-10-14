using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

public enum SchemaImportMode
{
    /// <summary>«Объединить»: add new objects, update objects with the same Id, delete nothing.</summary>
    Merge,
    /// <summary>«Заменить»: the drawing schema becomes equal to the JSON.</summary>
    Replace
}

public enum SchemaSection { Parameter, Enum, Template }

public enum SchemaChangeKind { Added, Changed, Removed }

public enum TypeConflictResolution
{
    /// <summary>«Оставить»: the drawing keeps its parameter unchanged.</summary>
    Keep,
    /// <summary>«Заменить»: the parameter from JSON replaces the drawing parameter.</summary>
    Replace
}

/// <summary>One added, changed or removed schema object.</summary>
public sealed class SchemaChange
{
    public SchemaChange(SchemaSection section, SchemaChangeKind kind, string id, string? oldType = null, string? newType = null)
    {
        Section = section; Kind = kind; Id = id; OldType = oldType; NewType = newType;
    }
    public SchemaSection Section { get; }
    public SchemaChangeKind Kind { get; }
    public string Id { get; }
    /// <summary>Parameter type in the drawing for changed parameters.</summary>
    public string? OldType { get; }
    public string? NewType { get; }
    public bool TypeChanged => Kind == SchemaChangeKind.Changed && !SchemaImport.SameType(OldType, NewType);
}

/// <summary>A parameter whose type differs between the drawing and JSON (TZXDATA 5.3).</summary>
public sealed class SchemaTypeConflict
{
    public SchemaTypeConflict(string id, string? oldType, string? newType) { Id = id; OldType = oldType; NewType = newType; }
    public string Id { get; }
    public string? OldType { get; }
    public string? NewType { get; }
    /// <summary>Only used by <see cref="SchemaImportMode.Merge"/>; <see cref="SchemaImportMode.Replace"/> always replaces.</summary>
    public TypeConflictResolution Resolution { get; set; } = TypeConflictResolution.Keep;
}

/// <summary>
/// Result of comparing the drawing schema with an imported JSON. Nothing is written by the plan:
/// the caller shows <see cref="SchemaImportReport"/>, lets the user resolve <see cref="Conflicts"/>,
/// checks <see cref="Validate"/> and writes <see cref="BuildResult"/> with <see cref="SchemaNodWriter"/>.
/// </summary>
public sealed class SchemaImportPlan
{
    internal SchemaImportPlan(ParameterSchema? current, ParameterSchema imported, SchemaImportMode mode, IReadOnlyList<SchemaTypeConflict> conflicts)
    {
        Current = current; Imported = imported; Mode = mode; Conflicts = conflicts;
    }

    public SchemaImportMode Mode { get; }
    /// <summary>Schema of the drawing; null when the drawing has none.</summary>
    public ParameterSchema? Current { get; }
    public ParameterSchema Imported { get; }
    public IReadOnlyList<SchemaTypeConflict> Conflicts { get; }

    /// <summary>The schema the drawing will have, with the current conflict resolutions.</summary>
    public ParameterSchema BuildResult()
    {
        if (Mode == SchemaImportMode.Replace || Current is null) return ParameterSchemaJson.Clone(Imported);
        var result = ParameterSchemaJson.Clone(Current);
        result.Format = Imported.Format;
        result.Version = Imported.Version;
        foreach (var property in Imported.Extra) result.Extra.Set(property.Key, property.Value);
        var kept = new HashSet<string>(Conflicts.Where(c => c.Resolution == TypeConflictResolution.Keep).Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        Merge(result.Parameters, Imported.Parameters, p => p.Id, id => kept.Contains(id));
        Merge(result.Enums, Imported.Enums, e => e.Id, _ => false);
        Merge(result.Templates, Imported.Templates, t => t.Id, _ => false);
        return ParameterSchemaJson.Clone(result);
    }

    /// <summary>Added, changed and removed objects for the current resolutions.</summary>
    public IReadOnlyList<SchemaChange> Changes => SchemaImport.Compare(Current, BuildResult());

    /// <summary>Checks the resulting schema (TZXDATA 4.5); errors forbid writing it.</summary>
    public IReadOnlyList<SchemaIssue> Validate(ParameterTypeRegistry? types = null) => new SchemaValidator(types).Validate(BuildResult());

    private static void Merge<T>(List<T> target, IEnumerable<T> source, Func<T, string?> id, Func<string, bool> keep) where T : class
    {
        foreach (var item in source)
        {
            var index = target.FindIndex(existing => ParameterSchema.SameId(id(existing), id(item)));
            if (index < 0) target.Add(item);
            else if (!keep(id(item)!)) target[index] = item;
        }
    }
}

public static class SchemaImport
{
    /// <param name="current">Schema read from NOD, or null if the drawing has none.</param>
    /// <param name="imported">Schema read from JSON without syntax or field type errors.</param>
    public static SchemaImportPlan Plan(ParameterSchema? current, ParameterSchema imported, SchemaImportMode mode)
    {
        if (imported is null) throw new ArgumentNullException(nameof(imported));
        var conflicts = new List<SchemaTypeConflict>();
        if (current != null)
            foreach (var parameter in imported.Parameters)
            {
                var existing = current.FindParameter(parameter.Id);
                if (existing != null && !SameType(existing.Type, parameter.Type) &&
                    !conflicts.Any(c => ParameterSchema.SameId(c.Id, parameter.Id)))
                    conflicts.Add(new SchemaTypeConflict(parameter.Id!, existing.Type, parameter.Type));
            }
        return new SchemaImportPlan(current is null ? null : ParameterSchemaJson.Clone(current), ParameterSchemaJson.Clone(imported), mode, conflicts);
    }

    /// <summary>Type names are case-insensitive as in <see cref="ParameterTypeRegistry"/>.</summary>
    public static bool SameType(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Objects are matched by Id (case-insensitive) and compared by their canonical JSON.</summary>
    public static IReadOnlyList<SchemaChange> Compare(ParameterSchema? before, ParameterSchema after)
    {
        var changes = new List<SchemaChange>();
        var empty = new ParameterSchema();
        before ??= empty;
        Compare(changes, SchemaSection.Parameter, before.Parameters, after.Parameters, p => p.Id, ParameterSchemaJson.ToJson, p => p.Type);
        Compare(changes, SchemaSection.Enum, before.Enums, after.Enums, e => e.Id, ParameterSchemaJson.ToJson, null);
        Compare(changes, SchemaSection.Template, before.Templates, after.Templates, t => t.Id, ParameterSchemaJson.ToJson, null);
        return changes;
    }

    private static void Compare<T>(List<SchemaChange> changes, SchemaSection section, IReadOnlyList<T> before, IReadOnlyList<T> after,
        Func<T, string?> id, Func<T, JsonObject> json, Func<T, string?>? type) where T : class
    {
        foreach (var item in after)
        {
            var old = before.FirstOrDefault(existing => ParameterSchema.SameId(id(existing), id(item)));
            if (old is null) changes.Add(new SchemaChange(section, SchemaChangeKind.Added, id(item) ?? ""));
            else if (!JsonValue.DeepEquals(json(old), json(item)))
                changes.Add(new SchemaChange(section, SchemaChangeKind.Changed, id(item) ?? "", type?.Invoke(old), type?.Invoke(item)));
        }
        foreach (var item in before)
            if (!after.Any(existing => ParameterSchema.SameId(id(existing), id(item))))
                changes.Add(new SchemaChange(section, SchemaChangeKind.Removed, id(item) ?? "", type?.Invoke(item)));
    }
}

/// <summary>Russian text of the report shown before an import is written (TZXDATA 5.3).</summary>
public static class SchemaImportReport
{
    public static IReadOnlyList<string> Format(SchemaImportPlan plan, IReadOnlyList<SchemaIssue> issues, SchemaUsage? usage = null)
    {
        var lines = new List<string>();
        var changes = plan.Changes;
        lines.Add("Режим: " + (plan.Mode == SchemaImportMode.Merge ? "Объединить" : "Заменить") +
            (plan.Current is null ? " (в чертеже нет схемы)" : ""));
        foreach (var section in new[] { SchemaSection.Parameter, SchemaSection.Enum, SchemaSection.Template })
        {
            var items = changes.Where(c => c.Section == section).ToList();
            lines.Add(SectionTitle(section) + ": добавлено " + Count(items, SchemaChangeKind.Added) +
                ", изменено " + Count(items, SchemaChangeKind.Changed) + ", удалено " + Count(items, SchemaChangeKind.Removed));
            foreach (var change in items)
                lines.Add("  " + (change.Kind == SchemaChangeKind.Added ? "+ " : change.Kind == SchemaChangeKind.Changed ? "~ " : "- ") +
                    change.Id + (change.TypeChanged ? " (тип " + change.OldType + " → " + change.NewType + ")" : "") + Used(usage, change.Section, change.Id));
        }
        if (changes.Count == 0) lines.Add("Схема чертежа не изменится.");

        if (plan.Conflicts.Count != 0)
        {
            lines.Add("Конфликты типов: " + plan.Conflicts.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var conflict in plan.Conflicts)
                lines.Add("  " + conflict.Id + ": в чертеже " + (conflict.OldType ?? "(нет)") + ", в файле " + (conflict.NewType ?? "(нет)") + " — " +
                    (plan.Mode == SchemaImportMode.Replace || conflict.Resolution == TypeConflictResolution.Replace ? "заменить" : "оставить") +
                    Used(usage, SchemaSection.Parameter, conflict.Id));
        }

        var removedInUse = changes.Where(c => c.Kind == SchemaChangeKind.Removed && usage != null && usage.Count(c.Section, c.Id) != 0).ToList();
        if (removedInUse.Count != 0)
        {
            lines.Add("Удаляются из схемы, но используются объектами: " + removedInUse.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var change in removedInUse)
                lines.Add("  " + SectionTitle(change.Section, singular: true) + " " + change.Id + " — объектов: " +
                    usage!.Count(change.Section, change.Id).ToString(CultureInfo.InvariantCulture));
            lines.Add("  Значения в объектах сохраняются и показываются как «Не описаны в схеме».");
        }

        foreach (var issue in issues) lines.Add(issue.ToString());
        return lines;
    }

    private static string Count(IEnumerable<SchemaChange> changes, SchemaChangeKind kind) =>
        changes.Count(c => c.Kind == kind).ToString(CultureInfo.InvariantCulture);

    private static string Used(SchemaUsage? usage, SchemaSection section, string id)
    {
        var count = usage?.Count(section, id) ?? 0;
        return count == 0 ? "" : " [используется объектами: " + count.ToString(CultureInfo.InvariantCulture) + "]";
    }

    private static string SectionTitle(SchemaSection section, bool singular = false) => section switch
    {
        SchemaSection.Parameter => singular ? "Параметр" : "Параметры",
        SchemaSection.Enum => singular ? "Справочник" : "Справочники",
        _ => singular ? "Шаблон" : "Шаблоны"
    };
}
