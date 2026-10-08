using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>Patches documented fields in their existing storage while retaining unknown values.</summary>
public sealed class XDataPatch
{
    public EntityDataSnapshot Apply(EntityDataSnapshot snapshot, SelectionEdit edit)
    {
        if (edit.Kind == SelectionEditKind.Header && !PropertyEditorViewModel.HeaderFields.Contains(edit.Field) ||
            edit.Kind == SelectionEditKind.MaterialField && !PropertyEditorViewModel.MaterialFields.Contains(edit.Field))
            throw new ArgumentException("Неизвестное поле.", nameof(edit));
        var parsed = new XDataParser().Parse(snapshot);
        if (parsed.Warnings.Count != 0) throw new InvalidOperationException("Исправьте ошибки формата перед записью: " + string.Join("; ", parsed.Warnings));
        var found = false;
        var data = new List<DataValue>();
        var apps = new List<List<DataValue>>();
        for (var index = 0; index < snapshot.XData.Count;)
        {
            var end = index + 1;
            while (end < snapshot.XData.Count && snapshot.XData[end].TypeCode != 1001) end++;
            var app = snapshot.XData.Skip(index).Take(end - index).ToList();
            if (app[0].TypeCode == 1001 && XDataParser.IsKnownApp((string)app[0].Value))
                PatchList(app, edit, ref found);
            apps.Add(app); index = end;
        }

        var records = snapshot.Records.ToList();
        var xmls = new Dictionary<int, XDocument>();
        var changedXml = new HashSet<int>();
        for (var index = 0; index < records.Count; index++)
        {
            var values = records[index].Values;
            if (values.Count == 0 || values.Any(v => (v.TypeCode != 1 && v.TypeCode != 1000) || !(v.Value is string))) continue;
            var text = string.Concat(values.Select(v => (string)v.Value));
            if (!text.TrimStart().StartsWith("<", StringComparison.Ordinal)) continue;
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = XDataParser.MaxXmlCharacters });
            var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (!xml.Root!.Name.LocalName.Equals("VisualTreeString", StringComparison.OrdinalIgnoreCase)) continue;
            xmls.Add(index, xml);
            var changed = false;
            if (edit.Kind == SelectionEditKind.Header)
            {
                foreach (var properties in xml.Root.Elements().Where(e => e.Name.LocalName == "Properties"))
                    if (PatchXmlField(properties, edit.Field, edit.Value, ref changed)) found = true;
            }
            else if (edit.Kind != SelectionEditKind.AddMaterial)
            {
                foreach (var material in xml.Root.Elements().Where(e => e.Name.LocalName == "Materials")
                    .SelectMany(e => e.Elements()).Where(e => XDataParser.IsMaterial(e.Name.LocalName)).ToArray())
                {
                    if (XmlField(material, "Name") != edit.MaterialKey) continue;
                    found = true;
                    if (edit.Kind == SelectionEditKind.DeleteMaterial) { material.Remove(); changed = true; }
                    else if (!PatchXmlField(material, edit.Field, edit.Value, ref changed))
                    { material.SetAttributeValue(edit.Field, edit.Value); changed = true; }
                }
            }
            if (changed) changedXml.Add(index);
        }

        if (!found && edit.Kind != SelectionEditKind.DeleteMaterial)
        {
            var material = edit.Material ?? new MaterialItemViewModel { Name = edit.MaterialKey, IsInSpec = null, Count = "" };
            if (edit.Kind == SelectionEditKind.MaterialField)
                typeof(MaterialItemViewModel).GetProperty(edit.Field)!.SetValue(material,
                    edit.Field == "IsInSpec" ? ParseFlag(edit.Value) : (object)edit.Value);
            // Prefer an existing VisualTreeString; otherwise use the last recognized RegApp, or BobrovXDATA.
            if (xmls.Count != 0)
            {
                var index = xmls.Keys.Last(); var root = xmls[index].Root!;
                var sectionName = edit.Kind == SelectionEditKind.Header ? "Properties" : "Materials";
                var section = root.Elements().LastOrDefault(e => e.Name.LocalName == sectionName);
                if (section == null) { section = new XElement(root.Name.Namespace + sectionName); root.Add(section); }
                if (edit.Kind == SelectionEditKind.Header) section.Add(new XElement(root.Name.Namespace + edit.Field, edit.Value));
                else section.Add(new XElement(root.Name.Namespace + "Material", MaterialValues(material).Select(pair => new XAttribute(pair.Key, pair.Value))));
                changedXml.Add(index);
            }
            else
            {
                var app = apps.LastOrDefault(a => a[0].TypeCode == 1001 && XDataParser.IsKnownApp((string)a[0].Value));
                if (app == null) { app = new List<DataValue> { new DataValue(1001, "BobrovXDATA") }; apps.Add(app); }
                if (edit.Kind == SelectionEditKind.Header) app.Add(new DataValue(1000, edit.Field + "=" + edit.Value));
                else
                {
                    app.Add(new DataValue(1000, "Material")); app.Add(new DataValue(1002, "{"));
                    app.AddRange(MaterialValues(material).Select(pair => new DataValue(1000, pair.Key + "=" + pair.Value)));
                    app.Add(new DataValue(1002, "}"));
                }
            }
        }
        foreach (var app in apps) data.AddRange(app);
        foreach (var index in changedXml)
        {
            var text = xmls[index].ToString(SaveOptions.DisableFormatting);
            var code = records[index].Values[0].TypeCode;
            // Keep XML values below AutoCAD's string chunk limits.
            var chunks = new List<DataValue>();
            for (var offset = 0; offset < text.Length;)
            {
                var end = offset; var bytes = 0;
                while (end < text.Length)
                {
                    var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                    var size = Encoding.UTF8.GetByteCount(text.Substring(end, width));
                    if (bytes + size > 240) break;
                    bytes += size; end += width;
                }
                chunks.Add(new DataValue(code, text.Substring(offset, end - offset))); offset = end;
            }
            records[index] = new DataRecord(records[index].Name, chunks);
        }
        var result = new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, data, records);
        var warnings = new XDataParser().Parse(result).Warnings;
        if (warnings.Count != 0) throw new InvalidOperationException(string.Join("; ", warnings));
        return result;
    }

    private static void PatchList(List<DataValue> values, SelectionEdit edit, ref bool found)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (values[index].TypeCode == 1000 && values[index].Value is string label && XDataParser.IsMaterial(label) &&
                index + 1 < values.Count && values[index + 1].TypeCode == 1002 && (string)values[index + 1].Value == "{")
            {
                var end = index + 2; var depth = 1;
                for (; end < values.Count; end++)
                    if (values[end].TypeCode == 1002) { depth += (string)values[end].Value == "{" ? 1 : -1; if (depth == 0) break; }
                var fields = values.Skip(index + 2).Take(end - index - 2).Where(v => v.TypeCode == 1000).ToList();
                var key = fields.Select(v => Split((string)v.Value)).Where(p => p.Key == "Name").Select(p => p.Value).LastOrDefault() ?? "";
                if (edit.Kind != SelectionEditKind.Header && edit.Kind != SelectionEditKind.AddMaterial && key == edit.MaterialKey)
                {
                    found = true;
                    if (edit.Kind == SelectionEditKind.DeleteMaterial)
                    { values.RemoveRange(index, end - index + 1); index--; continue; }
                    var updated = false;
                    for (var item = index + 2; item < end; item++)
                        if (values[item].TypeCode == 1000 && Split((string)values[item].Value).Key == edit.Field)
                        { values[item] = Replace(values[item], edit.Value); updated = true; }
                    if (!updated) { values.Insert(end, new DataValue(1000, edit.Field + "=" + edit.Value)); end++; }
                }
                index = end; continue;
            }
            if (edit.Kind == SelectionEditKind.Header && values[index].TypeCode == 1000 && Split((string)values[index].Value).Key == edit.Field)
            { values[index] = Replace(values[index], edit.Value); found = true; }
        }
    }

    private static DataValue Replace(DataValue old, string value)
    { var text = (string)old.Value; return new DataValue(old.TypeCode, text.Substring(0, text.IndexOf('=') + 1) + value); }
    private static KeyValuePair<string, string> Split(string text)
    { var at = text.IndexOf('='); return new KeyValuePair<string, string>(at > 0 ? XDataParser.Canonical(text.Substring(0, at).Trim()) : "", at > 0 ? text.Substring(at + 1) : ""); }
    private static bool PatchXmlField(XElement node, string field, string value, ref bool changed)
    {
        var found = false;
        foreach (var attribute in node.Attributes().Where(a => XDataParser.Canonical(a.Name.LocalName) == field))
        { if (attribute.Value != value) { attribute.Value = value; changed = true; } found = true; }
        foreach (var element in node.Elements().Where(e => XDataParser.Canonical(e.Name.LocalName) == field))
        { if (element.Value != value) { element.Value = value; changed = true; } found = true; }
        return found;
    }
    private static string XmlField(XElement node, string field) => node.Attributes().Where(a => XDataParser.Canonical(a.Name.LocalName) == field).Select(a => a.Value)
        .Concat(node.Elements().Where(e => XDataParser.Canonical(e.Name.LocalName) == field).Select(e => e.Value)).LastOrDefault() ?? "";
    private static object? ParseFlag(string value) => bool.TryParse(value, out var flag) ? flag : (object?)null;
    private static IEnumerable<KeyValuePair<string, string>> MaterialValues(MaterialItemViewModel row) => PropertyEditorViewModel.MaterialFields
        .Select(field => new KeyValuePair<string, string>(field, typeof(MaterialItemViewModel).GetProperty(field)!.GetValue(row)?.ToString() ?? ""));
}
