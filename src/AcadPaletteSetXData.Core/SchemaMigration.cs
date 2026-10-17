using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Node = AcadPaletteSetXData.Core.XDataV2.Tokens.Node;

namespace AcadPaletteSetXData.Core;

/// <summary>A <c>Table</c> target of a legacy list (<c>Material → Materials</c>) and its columns.</summary>
public sealed class MigrationTable
{
    internal MigrationTable(string label, string table, IReadOnlyDictionary<string, string> columns) { Label = label; Table = table; Columns = columns; }
    /// <summary>The legacy label of the list: <c>Material</c>.</summary>
    public string Label { get; }
    /// <summary>The <c>Table</c> parameter of the schema.</summary>
    public string Table { get; }
    /// <summary>Legacy field → column; case-insensitive.</summary>
    public IReadOnlyDictionary<string, string> Columns { get; }
}

/// <summary>
/// The table of correspondence of <c>VELEC_MIGRATE</c> (TZXDATA stage 9): the <c>migration</c> section of the schema.
/// <code>
/// "migration": {
///   "fields": { "Type": "Device_Type", … },
///   "tables": { "Material": { "table": "Materials", "columns": { "Name": "Mat_Mark", … } } },
///   "rename": { "Old_Id": "New_Id" }
/// }
/// </code>
/// A legacy key equal to an Id of the schema needs no entry. Wrong entries are skipped here and reported by
/// <see cref="SchemaValidator"/>.
/// </summary>
public sealed class MigrationMap
{
    public const string SectionName = "migration";
    public const string FieldsName = "fields";
    public const string TablesName = "tables";
    public const string TableName = "table";
    public const string ColumnsName = "columns";
    public const string RenameName = "rename";
    /// <summary>The label of legacy material lists; <c>Материал</c> is the same label.</summary>
    public const string MaterialLabel = "Material";

    private readonly Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MigrationTable> tables = new Dictionary<string, MigrationTable>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Fields => fields;
    public IReadOnlyDictionary<string, MigrationTable> Tables => tables;
    /// <summary>Old Id → new Id of v2 values (TZXDATA П3: an Id is renamed only by the migration).</summary>
    public IReadOnlyDictionary<string, string> Renames => renames;

    public static MigrationMap Read(ParameterSchema? schema)
    {
        var map = new MigrationMap();
        if (schema is null || !(Member(schema.Extra, SectionName) is JsonObject section)) return map;
        Strings(Member(section, FieldsName), map.fields);
        Strings(Member(section, RenameName), map.renames);
        if (Member(section, TablesName) is JsonObject tables)
            foreach (var item in tables)
            {
                if (!(item.Value is JsonObject table) || !(Member(table, TableName) is JsonString target) || map.tables.ContainsKey(item.Key)) continue;
                var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Strings(Member(table, ColumnsName), columns);
                map.tables.Add(item.Key, new MigrationTable(item.Key, target.Value, columns));
            }
        return map;
    }

    /// <summary>The scalar parameter of a legacy field: the entry of the map (also by the Russian key), or the same Id.</summary>
    public string? Field(string key, SchemaSnapshot schema, ParameterTypeRegistry? types = null) =>
        Target(Lookup(fields, key), schema, types) ?? Target(key, schema, types);

    /// <summary>The table of a labelled legacy list.</summary>
    public MigrationTable? Table(string label, SchemaSnapshot schema, ParameterTypeRegistry? types = null)
    {
        var table = tables.TryGetValue(label, out var found) || tables.TryGetValue(XDataParser.IsMaterial(label) ? MaterialLabel : label, out found) ? found : null;
        if (table is null || !schema.Parameters.TryGetValue(table.Table, out var parameter) ||
            (types ?? ParameterTypeRegistry.Default).Find(parameter.Type)?.Kind != ParameterKind.Table) return null;
        return table;
    }

    /// <summary>The column of a legacy field of a row.</summary>
    public static string? Column(MigrationTable table, string key, SchemaSnapshot schema, ParameterTypeRegistry? types = null)
    {
        var columns = schema.Parameters[table.Table].Columns;
        string? Accept(string? id) => id != null && columns?.Any(column => ParameterSchema.SameId(column, id)) == true ? Target(id, schema, types) : null;
        return Accept(Lookup(table.Columns, key)) ?? Accept(key);
    }

    /// <summary>The schema editor renames a parameter: its references in the section follow (keys stay legacy names).</summary>
    internal static void RenameParameter(ParameterSchema schema, string old, string value)
    {
        if (!(Member(schema.Extra, SectionName) is JsonObject section)) return;
        RenameValues(Member(section, FieldsName), old, value, null);
        RenameValues(Member(section, RenameName), old, value, null);
        if (!(Member(section, TablesName) is JsonObject tables)) return;
        foreach (var table in tables.Select(item => item.Value).OfType<JsonObject>())
        {
            RenameValues(table, old, value, TableName);
            RenameValues(Member(table, ColumnsName), old, value, null);
        }
    }

    private static void RenameValues(JsonValue? json, string old, string value, string? only)
    {
        if (!(json is JsonObject map)) return;
        var keys = map.Where(item => item.Value is JsonString text && ParameterSchema.SameId(text.Value, old) &&
            (only is null || string.Equals(item.Key, only, StringComparison.OrdinalIgnoreCase))).Select(item => item.Key).ToList();
        foreach (var key in keys) map.Set(key, new JsonString(value));
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var id) || map.TryGetValue(XDataParser.Canonical(key), out id) ? id : null;

    private static string? Target(string? id, SchemaSnapshot schema, ParameterTypeRegistry? types)
    {
        if (id is null || ParameterSchema.ReservedIds.Contains(id, StringComparer.OrdinalIgnoreCase) || !schema.Parameters.TryGetValue(id, out var parameter)) return null;
        var kind = (types ?? ParameterTypeRegistry.Default).Find(parameter.Type)?.Kind;
        return kind is null || kind == ParameterKind.Table || kind == ParameterKind.CalculatedString ? null : parameter.Id;
    }

    private static JsonValue? Member(JsonObject json, string name) =>
        json.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Strings(JsonValue? json, Dictionary<string, string> target)
    {
        if (!(json is JsonObject map)) return;
        foreach (var item in map)
            if (item.Value is JsonString text && !target.ContainsKey(item.Key)) target.Add(item.Key, text.Value);
    }
}

/// <summary>The result of <c>VELEC_MIGRATE</c> for one object.</summary>
public sealed class ObjectMigration
{
    internal ObjectMigration(EntityDataSnapshot before, EntityDataSnapshot after, IReadOnlyList<string> sources, IReadOnlyList<string> migrated,
        IReadOnlyList<string> renamed, IReadOnlyList<string> warnings, IReadOnlyList<string> errors, bool complete, bool sourcesRemoved, bool newDeviceId)
    {
        Before = before; After = after; Sources = sources; Migrated = migrated; Renamed = renamed; Warnings = warnings; Errors = errors;
        Complete = complete; SourcesRemoved = sourcesRemoved; NewDeviceId = newDeviceId;
    }

    public string Handle => Before.Handle;
    public string EntityType => Before.EntityType;
    public EntityDataSnapshot Before { get; }
    /// <summary>The same instance as <see cref="Before"/> when nothing changes.</summary>
    public EntityDataSnapshot After { get; }
    public bool Changed => !ReferenceEquals(Before, After);
    /// <summary>Legacy formats found on the object.</summary>
    public IReadOnlyList<string> Sources { get; }
    /// <summary>Parameters written by this migration.</summary>
    public IReadOnlyList<string> Migrated { get; }
    /// <summary>Renamed Ids: <c>Old → New</c>.</summary>
    public IReadOnlyList<string> Renamed { get; }
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>The object is left as it is.</summary>
    public IReadOnlyList<string> Errors { get; }
    /// <summary>Every legacy value has a place in v2: only then the sources may be removed.</summary>
    public bool Complete { get; }
    public bool SourcesRemoved { get; }
    public bool NewDeviceId { get; }
    public bool HasLegacy => Sources.Count != 0;
}

/// <summary>
/// <c>VELEC_MIGRATE</c> (TZXDATA stage 9): <c>ESMT_LEP_v1.0</c>, v1 <see cref="DrawingNames.RegApp"/> strings
/// <c>key=value</c>, XML <c>VisualTreeString</c> and the earlier RegApp <see cref="DrawingNames.LegacyRegApp"/> become
/// <see cref="DrawingNames.RegApp"/> v2 by the table of correspondence of the schema (<see cref="MigrationMap"/>).
/// Values already stored in v2 are never overwritten, so a repeated migration changes nothing. The sources are kept:
/// v1 strings, converted in place, are copied to <see cref="DrawingNames.BackupRegApp"/>; they are removed only by
/// <c>removeSources</c> and only when every value has been migrated (TZXDATA 13).
/// </summary>
public sealed class SchemaMigration
{
    private readonly SchemaSnapshot schema;
    private readonly ParameterTypeRegistry types;
    private readonly Func<Guid> newGuid;
    private readonly XDataPatch patch;

    public SchemaMigration(SchemaSnapshot schema, bool removeSources, ParameterTypeRegistry? types = null, Func<Guid>? newGuid = null, CultureInfo? culture = null)
    {
        this.schema = schema?.Schema != null ? schema : throw new InvalidOperationException("Схема параметров не загружена; выполните VELEC_SCHEMA_IMPORT.");
        RemoveSources = removeSources;
        this.types = types ?? ParameterTypeRegistry.Default;
        this.newGuid = newGuid ?? Guid.NewGuid;
        patch = new XDataPatch(schema, this.types, this.newGuid, culture);
        Map = MigrationMap.Read(schema.Schema);
    }

    public MigrationMap Map { get; }
    public bool RemoveSources { get; }

    /// <summary>Legacy formats of an object without migrating it (<c>VELEC_CHECK</c>).</summary>
    public static IReadOnlyList<string> FindSources(EntityDataSnapshot snapshot)
    {
        var sources = new List<string>();
        var format = XDataV2.FormatOf(snapshot.XData);
        foreach (var section in Sections(snapshot.XData))
        {
            var kind = Kind(section.Name);
            if (kind == SourceKind.None || kind == SourceKind.Backup) continue;
            if (kind == SourceKind.Current && format != XDataFormat.V1) continue;
            if (section.End > section.Start + 1) sources.Add(SourceName(section.Name, kind));
        }
        foreach (var record in snapshot.Records)
        {
            var root = XDataParser.LoadXml(record, out _);
            if (root != null && XDataParser.IsVisualTree(root)) sources.Add(RecordSource(record));
        }
        return sources.Distinct().ToArray();
    }

    public ObjectMigration Migrate(EntityDataSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var run = new Run(this, snapshot);
        return run.Execute();
    }

    /// <summary>The report of a planned or done migration for the command line.</summary>
    public static IReadOnlyList<string> Report(IReadOnlyList<ObjectMigration> results, bool removeSources)
    {
        var lines = new List<string>();
        foreach (var result in results.Where(item => item.Changed || item.Errors.Count != 0 || item.Warnings.Count != 0))
        {
            var text = "Объект " + result.Handle + " (" + result.EntityType + ")";
            if (result.Sources.Count != 0) text += ": " + string.Join(", ", result.Sources);
            if (result.Errors.Count != 0) text += " — не изменяется";
            else if (!result.Changed) text += " — уже перенесено";
            lines.Add(text);
            if (result.Migrated.Count != 0) lines.Add("  перенесено: " + string.Join(", ", result.Migrated));
            if (result.Renamed.Count != 0) lines.Add("  переименовано: " + string.Join(", ", result.Renamed));
            if (result.NewDeviceId) lines.Add("  создан " + XDataV2.DeviceIdKey);
            if (result.SourcesRemoved) lines.Add("  исходные данные удаляются");
            foreach (var warning in result.Warnings) lines.Add("  ! " + warning);
            foreach (var error in result.Errors) lines.Add("  × " + error);
        }
        var legacy = results.Count(item => item.HasLegacy);
        var changed = results.Count(item => item.Changed);
        var failed = results.Count(item => item.Errors.Count != 0);
        var incomplete = results.Count(item => item.HasLegacy && !item.Complete && item.Errors.Count == 0);
        lines.Add(string.Format(CultureInfo.InvariantCulture,
            "Объектов: {0}, с прежними данными: {1}, изменяется: {2}, без изменений: {3}, с ошибками: {4}, перенесено не полностью: {5}.",
            results.Count, legacy, changed, results.Count - changed - failed, failed, incomplete));
        lines.Add(removeSources
            ? "Исходные данные удаляются у объектов, перенесённых полностью."
            : "Исходные данные сохраняются (строки v1 — в " + DrawingNames.BackupRegApp + ").");
        return lines;
    }

    private enum SourceKind { None, Esmt, Legacy, Current, Backup }

    private static SourceKind Kind(string app) =>
        Same(app, DrawingNames.EsmtRegApp) ? SourceKind.Esmt :
        Same(app, DrawingNames.LegacyRegApp) ? SourceKind.Legacy :
        Same(app, DrawingNames.RegApp) ? SourceKind.Current :
        Same(app, DrawingNames.BackupRegApp) ? SourceKind.Backup : SourceKind.None;

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string SourceName(string app, SourceKind kind) => kind == SourceKind.Current ? app + " v1" : app;

    private static string RecordSource(DataRecord record) => "XML VisualTreeString (XRecord " + record.Name + ")";

    private readonly struct Section
    {
        public Section(string name, int start, int end) { Name = name; Start = start; End = end; }
        public string Name { get; }
        /// <summary>Index of the 1001.</summary>
        public int Start { get; }
        public int End { get; }
    }

    private static List<Section> Sections(IReadOnlyList<DataValue> xdata)
    {
        var sections = new List<Section>();
        for (var i = 0; i < xdata.Count; i++)
        {
            if (xdata[i].TypeCode != 1001) continue;
            var end = i + 1;
            while (end < xdata.Count && xdata[end].TypeCode != 1001) end++;
            sections.Add(new Section(xdata[i].Value as string ?? "", i, end));
            i = end - 1;
        }
        return sections;
    }

    /// <summary>The version of a section that starts with <c>(1070 . N)</c>, or null.</summary>
    private static long? Version(IReadOnlyList<DataValue> xdata, Section section) =>
        section.Start + 1 < section.End && xdata[section.Start + 1].TypeCode == 1070
            ? xdata[section.Start + 1].Value switch { short s => s, int n => n, long l => l, _ => 0L }
            : (long?)null;

    private sealed class Field
    {
        public Field(string key, string value, string source) { Key = key; Value = value; Source = source; }
        public string Key { get; }
        public string Value { get; }
        public string Source { get; }
    }

    private sealed class LegacyRow
    {
        public LegacyRow(MigrationTable table, string source) { Table = table; Source = source; }
        public MigrationTable Table { get; }
        public string Source { get; }
        public List<Field> Fields { get; } = new List<Field>();
    }

    /// <summary>One object: the legacy values are read in the order the VST shows them, later sources win.</summary>
    private sealed class Run
    {
        private readonly SchemaMigration owner;
        private readonly EntityDataSnapshot snapshot;
        private readonly string name;
        private readonly List<string> sources = new List<string>();
        private readonly List<Field> fields = new List<Field>();
        private readonly List<LegacyRow> rows = new List<LegacyRow>();
        private readonly List<Node> typed = new List<Node>();
        private readonly List<string> migrated = new List<string>();
        private readonly List<string> renamed = new List<string>();
        private readonly List<string> warnings = new List<string>();
        private readonly List<string> errors = new List<string>();
        private bool complete = true;

        public Run(SchemaMigration owner, EntityDataSnapshot snapshot) { this.owner = owner; this.snapshot = snapshot; name = "Объект " + snapshot.Handle + ": "; }

        private SchemaSnapshot Schema => owner.schema;

        public ObjectMigration Execute()
        {
            var xdata = snapshot.XData;
            var sections = Sections(xdata);
            var app = XDataV2.FindApp(xdata);
            var format = XDataV2.FormatOf(xdata);
            if (app != null && app.Value.Others) return Fail("XDATA " + DrawingNames.RegApp + " повторяется; объект не изменяется.");
            if (format == XDataFormat.Newer) return Fail("XDATA " + DrawingNames.RegApp + " записан более новой версией формата; объект не изменяется.");
            if (sections.Count(section => Kind(section.Name) == SourceKind.Backup) > 1)
                return Fail("XDATA " + DrawingNames.BackupRegApp + " повторяется; объект не изменяется.");
            var hasBackup = sections.Any(section => Kind(section.Name) == SourceKind.Backup && section.End > section.Start + 1);
            if (format == XDataFormat.V1 && hasBackup)
                return Fail("есть и строки v1 " + DrawingNames.RegApp + ", и их копия " + DrawingNames.BackupRegApp + "; объект не изменяется.");

            foreach (var section in sections)
            {
                var kind = Kind(section.Name);
                if (kind == SourceKind.None || kind == SourceKind.Current && format != XDataFormat.V1 || section.End == section.Start + 1) continue;
                var source = SourceName(section.Name, kind);
                if (kind != SourceKind.Backup) sources.Add(source);
                var version = Version(xdata, section);
                if (version >= XDataV2.FormatVersion)
                {
                    // An earlier plugin wrote v2 pairs under SMARTLINE: they are copied as they are.
                    if (kind != SourceKind.Legacy || version > XDataV2.FormatVersion) { Incomplete(source + ": неизвестная версия формата " + version + "."); continue; }
                    typed.AddRange(XDataV2.Tokens.ReadPairs(xdata, section.Start + 2, section.End, out var damaged));
                    if (damaged) Incomplete(source + ": незакрытый или лишний список XDATA.");
                    continue;
                }
                ReadStrings(xdata, version is null ? section.Start + 1 : section.Start + 2, section.End, source, null);
            }
            var xml = new List<DataRecord>();
            foreach (var record in snapshot.Records)
            {
                var root = XDataParser.LoadXml(record, out var error);
                if (error != null) warnings.Add(error);
                if (root is null || !XDataParser.IsVisualTree(root)) continue;
                xml.Add(record);
                sources.Add(RecordSource(record));
                ReadXml(root, RecordSource(record));
            }

            var nodes = new List<Node>();
            if (format == XDataFormat.V2)
            {
                nodes = XDataV2.Tokens.ReadPairs(xdata, app!.Value.Start + 2, app.Value.End, out var damaged);
                if (damaged) return Fail("незакрытый или лишний список XDATA " + DrawingNames.RegApp + "; объект не изменяется.");
            }
            Rename(nodes);
            var hadDeviceId = nodes.Any(node => ParameterSchema.SameId(node.Id, XDataV2.DeviceIdKey));
            foreach (var node in typed)
            {
                if (node.Id is null) { Incomplete(DrawingNames.LegacyRegApp + ": значение без Id не перенесено."); continue; }
                if (ParameterSchema.SameId(node.Id, XDataV2.FormatKey)) continue;
                var id = owner.Map.Renames.TryGetValue(node.Id, out var target) ? target : node.Id;
                if (nodes.Any(item => ParameterSchema.SameId(item.Id, id))) continue;
                var all = new List<DataValue> { new DataValue(1000, id) };
                all.AddRange(node.Value);
                if (ParameterSchema.SameId(id, XDataV2.DeviceIdKey) || ParameterSchema.SameId(id, XDataV2.TemplateKey))
                    XDataPatch.SetSystem(nodes, id.ToUpperInvariant(), node.Value.Count == 1 && node.Value[0].Value is string text ? text : XDataV2.FormatValue(node.Value[0].Value));
                else nodes.Add(new Node(id, all, 1));
                migrated.Add(id);
            }
            WriteFields(nodes);
            WriteTables(nodes);

            var newDeviceId = false;
            if (sources.Count != 0 && !nodes.Any(node => ParameterSchema.SameId(node.Id, XDataV2.DeviceIdKey)))
            {
                XDataPatch.SetSystem(nodes, XDataV2.DeviceIdKey, XDataV2.NewDeviceId(owner.newGuid()));
                newDeviceId = !hadDeviceId;
            }

            var remove = owner.RemoveSources && complete && sources.Count != 0;
            if (owner.RemoveSources && !complete && sources.Count != 0) warnings.Add("перенесено не полностью: исходные данные сохраняются.");
            var after = Build(xdata, sections, app, format, nodes, remove);
            var records = remove && xml.Count != 0 ? snapshot.Records.Where(record => !xml.Contains(record)).ToArray() : snapshot.Records;
            var result = new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, after, records, snapshot.Warning);
            if (XDataV2.FormatOf(after) == XDataFormat.V2)
            {
                try { result = owner.patch.Apply(result, SelectionEdit.RecalculatePersisted()); }
                catch (InvalidOperationException error) { warnings.Add(error.Message); }
            }

            if (XDataWriter.SameData(snapshot, result)) return Done(snapshot, false, false);
            var tooLong = result.XData.FirstOrDefault(value => value.TypeCode == 1000 && value.Value is string text &&
                ParameterTypeRegistry.Utf8Length(text) > ParameterTypeRegistry.MaxStringBytes);
            if (tooLong != null) return Fail("строка XDATA длиннее " + ParameterTypeRegistry.MaxStringBytes + " байт; объект не изменяется.");
            var size = XDataV2.EstimateSize(result.XData);
            if (size > XDataV2.MaxXDataBytes && size > XDataV2.EstimateSize(xdata))
                return Fail("XDATA превысит " + XDataV2.MaxXDataBytes + " байт (оценка " + size + "); объект не изменяется.");
            return Done(result, remove, newDeviceId);
        }

        private ObjectMigration Done(EntityDataSnapshot after, bool removed, bool newDeviceId)
        {
            var changed = !ReferenceEquals(after, snapshot);
            return new ObjectMigration(snapshot, after, sources.ToArray(), changed ? migrated.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>(),
                changed ? renamed.ToArray() : Array.Empty<string>(), warnings.Distinct().ToArray(), errors.ToArray(), complete, removed && changed, newDeviceId && changed);
        }

        private ObjectMigration Fail(string error)
        {
            errors.Add(error);
            if (sources.Count == 0) sources.AddRange(FindSources(snapshot));
            return new ObjectMigration(snapshot, snapshot, sources.ToArray(), Array.Empty<string>(), Array.Empty<string>(), warnings.Distinct().ToArray(),
                errors.ToArray(), false, false, false);
        }

        private void Incomplete(string warning) { complete = false; warnings.Add(warning); }

        /// <summary>
        /// Strings <c>key=value</c> as the VST reads them: a string without <c>=</c> labels the next list; a labelled
        /// list mapped to a table is a row, an unlabelled list is read through.
        /// </summary>
        private void ReadStrings(IReadOnlyList<DataValue> values, int start, int end, string source, LegacyRow? row)
        {
            string? label = null;
            var i = start;
            while (i < end)
            {
                var value = values[i];
                if (XDataV2.Tokens.IsBracket(value, "{"))
                {
                    var damaged = false;
                    var next = XDataV2.Tokens.Skip(values, i, end, ref damaged);
                    var close = damaged ? next : next - 1;
                    if (damaged) Incomplete(source + ": незакрытый список XDATA.");
                    if (label is null) ReadStrings(values, i + 1, close, source, row);
                    else if (row != null) Incomplete(source + ": вложенный список «" + label + "» не перенесён.");
                    else
                    {
                        var table = owner.Map.Table(label, Schema, owner.types);
                        if (table is null) Incomplete(source + ": список «" + label + "» не сопоставлен с таблицей схемы.");
                        else
                        {
                            var item = new LegacyRow(table, source);
                            ReadStrings(values, i + 1, close, source, item);
                            rows.Add(item);
                        }
                    }
                    label = null;
                    i = next;
                    continue;
                }
                if (label != null) Incomplete(source + ": строка «" + label + "» без списка не перенесена.");
                label = null;
                if (XDataV2.Tokens.IsBracket(value, "}")) Incomplete(source + ": закрывающая скобка без открывающей.");
                else if (value.TypeCode == 1000 && value.Value is string text)
                {
                    var index = text.IndexOf('=');
                    if (index > 0 && text.Substring(0, index).Trim().Length != 0)
                        (row?.Fields ?? fields).Add(new Field(text.Substring(0, index).Trim(), text.Substring(index + 1), source));
                    else label = text;
                }
                else Incomplete(source + ": значение с кодом " + value.TypeCode.ToString(CultureInfo.InvariantCulture) + " не перенесено.");
                i++;
            }
            if (label != null) Incomplete(source + ": строка «" + label + "» без списка не перенесена.");
        }

        private void ReadXml(XElement root, string source)
        {
            if (root.Attributes().Any()) Incomplete(source + ": атрибуты VisualTreeString не перенесены.");
            foreach (var element in root.Elements())
            {
                if (element.Name.LocalName == "Properties")
                {
                    foreach (var field in element.Attributes()) fields.Add(new Field(field.Name.LocalName, field.Value, source));
                    foreach (var field in element.Elements()) fields.Add(new Field(field.Name.LocalName, field.Value, source));
                }
                else if (element.Name.LocalName == "Materials")
                {
                    foreach (var material in element.Elements())
                    {
                        var table = owner.Map.Table(material.Name.LocalName, Schema, owner.types);
                        if (table is null) { Incomplete(source + ": элемент «" + material.Name.LocalName + "» не сопоставлен с таблицей схемы."); continue; }
                        var row = new LegacyRow(table, source);
                        foreach (var field in material.Attributes()) row.Fields.Add(new Field(field.Name.LocalName, field.Value, source));
                        foreach (var field in material.Elements()) row.Fields.Add(new Field(field.Name.LocalName, field.Value, source));
                        rows.Add(row);
                    }
                }
                else Incomplete(source + ": элемент «" + element.Name.LocalName + "» не перенесён.");
            }
        }

        /// <summary>П3: an Id of stored v2 values, also of table cells, is renamed only here.</summary>
        private void Rename(List<Node> nodes)
        {
            if (owner.Map.Renames.Count == 0) return;
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Id is null) continue;
                if (node.Value.Count >= 2 && XDataV2.Tokens.IsBracket(node.Value[0], "{")) node = nodes[i] = RenameCells(node);
                if (!owner.Map.Renames.TryGetValue(node.Id!, out var target)) continue;
                if (nodes.Any(other => ParameterSchema.SameId(other.Id, target)))
                {
                    warnings.Add(node.Id + ": значение " + target + " уже есть; переименование не выполнено.");
                    continue;
                }
                var all = new List<DataValue> { new DataValue(1000, target) };
                all.AddRange(node.Value);
                nodes[i] = new Node(target, all, 1);
                renamed.Add(node.Id + " → " + target);
            }
        }

        private Node RenameCells(Node table)
        {
            var items = XDataV2.Tokens.ReadRows(table.Value);
            var changed = false;
            for (var i = 0; i < items.Count; i++)
            {
                var cells = items[i].Row;
                if (cells is null) continue;
                var rowChanged = false;
                for (var j = 0; j < cells.Count; j++)
                {
                    if (cells[j].Id is null || !owner.Map.Renames.TryGetValue(cells[j].Id!, out var target)) continue;
                    if (cells.Any(other => ParameterSchema.SameId(other.Id, target)))
                    {
                        warnings.Add(table.Id + "/" + cells[j].Id + ": колонка " + target + " уже есть; переименование не выполнено.");
                        continue;
                    }
                    var all = new List<DataValue> { new DataValue(1000, target) };
                    all.AddRange(cells[j].Value);
                    renamed.Add(table.Id + "/" + cells[j].Id + " → " + target);
                    cells[j] = new Node(target, all, 1);
                    rowChanged = true;
                }
                if (rowChanged) { items[i] = XDataPatch.Row(cells); changed = true; }
            }
            if (!changed) return table;
            var values = new List<DataValue> { table.All[0], new DataValue(1002, "{") };
            foreach (var item in items) values.AddRange(item.All);
            values.Add(new DataValue(1002, "}"));
            return new Node(table.Id, values, 1);
        }

        private void WriteFields(List<Node> nodes)
        {
            var values = new Dictionary<string, (Field Field, DataValue Value)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var field in fields)
            {
                var id = owner.Map.Field(field.Key, Schema, owner.types);
                if (id is null) { Incomplete(field.Source + ": поле «" + field.Key + "» не сопоставлено с параметром схемы."); continue; }
                var value = Convert(id, field.Value, field.Source + ": " + field.Key);
                if (value is null) continue;
                if (values.TryGetValue(id, out var earlier))
                {
                    if (!SameValue(earlier.Value, value))
                        warnings.Add(id + ": «" + earlier.Field.Value + "» (" + earlier.Field.Source + ") заменено на «" + field.Value + "» (" + field.Source + ").");
                }
                else order.Add(id);
                values[id] = (field, value);
            }
            foreach (var id in order)
            {
                var (field, value) = values[id];
                var stored = nodes.FirstOrDefault(node => ParameterSchema.SameId(node.Id, id));
                if (stored != null)
                {
                    // A value stored in v2 wins: the migration never overwrites it (a repeated run changes nothing).
                    if (stored.Value.Count != 1 || !SameValue(stored.Value[0], value))
                        warnings.Add(id + ": в v2 уже записано другое значение; «" + field.Value + "» (" + field.Source + ") не перенесено.");
                    continue;
                }
                XDataPatch.Set(nodes, XDataPatch.Pair(id, value));
                migrated.Add(id);
            }
        }

        private void WriteTables(List<Node> nodes)
        {
            foreach (var group in rows.GroupBy(row => row.Table.Table, StringComparer.OrdinalIgnoreCase))
            {
                var table = Schema.Parameters[group.Key];
                if (nodes.Any(node => ParameterSchema.SameId(node.Id, table.Id))) continue;
                var key = XDataV2.KeyColumn(table, Schema, owner.types);
                var items = new List<DataValue> { new DataValue(1002, "{") };
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in group)
                {
                    var cells = new List<Node>();
                    foreach (var field in row.Fields)
                    {
                        var column = MigrationMap.Column(row.Table, field.Key, Schema, owner.types);
                        if (column is null) { Incomplete(row.Source + ": поле «" + field.Key + "» строки " + table.Id + " не сопоставлено с колонкой."); continue; }
                        var value = Convert(column, field.Value, row.Source + ": " + table.Id + "/" + field.Key);
                        if (value != null) XDataPatch.Set(cells, XDataPatch.Pair(column, value));
                    }
                    if (cells.Count == 0) continue;
                    var rowKey = key is null ? null : XDataPatch.RowKey(cells, key);
                    if (rowKey != null && !keys.Add(rowKey))
                        warnings.Add(table.Id + ": строка с ключом «" + rowKey + "» повторяется; групповое редактирование изменит обе.");
                    items.AddRange(XDataPatch.Row(cells).All);
                }
                if (items.Count == 1) continue;
                items.Add(new DataValue(1002, "}"));
                var all = new List<DataValue> { new DataValue(1000, table.Id!) };
                all.AddRange(items);
                nodes.Add(new Node(table.Id, all, 1));
                migrated.Add(table.Id!);
            }
        }

        /// <summary>
        /// A legacy text in the schema type. An <c>Enum</c> keeps its key, a caption becomes the key; an unknown key is
        /// written with a warning, as <c>VELEC_CHECK</c> reports it. Other wrong values stay in the sources.
        /// </summary>
        private DataValue? Convert(string id, string text, string what)
        {
            var parameter = Schema.Parameters[id];
            var type = owner.types.Find(parameter.Type)!;
            if (type.Kind == ParameterKind.Enum)
            {
                var reference = Schema.Schema!.FindEnum(parameter.Enum);
                var key = reference?.FindItem(text)?.Key ??
                    reference?.Items.FirstOrDefault(item => string.Equals(item.Caption, text, StringComparison.OrdinalIgnoreCase))?.Key ?? text;
                if (ParameterTypeRegistry.Utf8Length(key) > ParameterTypeRegistry.MaxStringBytes)
                {
                    Incomplete(what + ": значение длиннее " + ParameterTypeRegistry.MaxStringBytes + " байт UTF-8 не перенесено.");
                    return null;
                }
                if (reference != null && reference.FindItem(key) is null && parameter.Editor?.AllowCustom != true)
                    warnings.Add(what + ": ключ «" + key + "» отсутствует в справочнике " + reference.Id + ".");
                return type.ToXData(key);
            }
            var error = XDataPatch.Check(parameter, type, text, Schema.Schema!, out var converted);
            if (error != null) { Incomplete(what + ": «" + text + "» не перенесено (" + error + ")."); return null; }
            return type.ToXData(converted);
        }

        private static bool SameValue(DataValue left, DataValue right) =>
            left.TypeCode == right.TypeCode && Equals(left.Value, right.Value);

        /// <summary>
        /// The new XDATA: v2 in place of v1 or of the empty section, with a copy of v1 strings kept in
        /// <see cref="DrawingNames.BackupRegApp"/>; other RegApps stay in their order unless the sources are removed.
        /// </summary>
        private List<DataValue> Build(IReadOnlyList<DataValue> xdata, List<Section> sections, (int Start, int End, bool Others)? app,
            XDataFormat format, List<Node> nodes, bool remove)
        {
            var result = new List<DataValue>(xdata.Count + 16);
            var first = sections.Count == 0 ? xdata.Count : sections[0].Start;
            for (var i = 0; i < first; i++) result.Add(xdata[i]);
            void V2()
            {
                result.Add(app is null ? new DataValue(1001, DrawingNames.RegApp) : xdata[app.Value.Start]);
                result.Add(format == XDataFormat.V2 ? xdata[app!.Value.Start + 1] : new DataValue(1070, (short)XDataV2.FormatVersion));
                foreach (var node in nodes) result.AddRange(node.All);
            }
            var written = false;
            foreach (var section in sections)
            {
                var kind = Kind(section.Name);
                if (kind == SourceKind.Current)
                {
                    if (nodes.Count == 0 && format != XDataFormat.V2) { Copy(result, xdata, section); continue; }
                    V2();
                    written = true;
                    continue;
                }
                if (remove && (kind == SourceKind.Esmt || kind == SourceKind.Legacy || kind == SourceKind.Backup)) continue;
                Copy(result, xdata, section);
            }
            if (!written && nodes.Count != 0) V2();
            if (format == XDataFormat.V1 && nodes.Count != 0 && !remove)
            {
                result.Add(new DataValue(1001, DrawingNames.BackupRegApp));
                for (var i = app!.Value.Start + 1; i < app.Value.End; i++) result.Add(xdata[i]);
            }
            return result;
        }

        private static void Copy(List<DataValue> result, IReadOnlyList<DataValue> xdata, Section section)
        {
            for (var i = section.Start; i < section.End; i++) result.Add(xdata[i]);
        }
    }
}
