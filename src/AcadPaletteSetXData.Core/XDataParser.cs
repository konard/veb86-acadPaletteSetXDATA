using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>Builds a lossless typed tree and projects only documented property/material formats.</summary>
public sealed class XDataParser
{
    public const int MaxDepth = 64;
    public const int MaxValues = 16384;
    public const int MaxXmlCharacters = 1048576;

    public ParsedEntityData Parse(EntityDataSnapshot snapshot)
    {
        var result = new ParsedEntityData(snapshot);
        if (snapshot.XData.Count != 0)
        {
            var xdata = new XDataNode("XDATA");
            result.Tree.Add(xdata);
            ParseValues(snapshot.XData, xdata, result, true);
            foreach (var app in xdata.Children.Where(node => IsKnownApp(node.Name))) ProjectList(app, result);
        }
        if (snapshot.Records.Count != 0)
        {
            var records = new XDataNode("ExtensionDictionary / XRecord");
            result.Tree.Add(records);
            foreach (var record in snapshot.Records.Take(MaxValues))
            {
                var node = new XDataNode(record.Name);
                records.Add(node);
                ParseValues(record.Values, node, result, false);
                ParseXml(record, node, result);
            }
            if (snapshot.Records.Count > MaxValues) result.Warnings.Add("Превышен лимит XRecord.");
        }
        return result;
    }

    private static void ParseValues(IReadOnlyList<DataValue> values, XDataNode root, ParsedEntityData result, bool splitApps)
    {
        var current = root;
        var stack = new Stack<XDataNode>();
        string? label = null;
        var ignoredDepth = 0;
        foreach (var value in values.Take(MaxValues))
        {
            var text = Format(value.Value);
            if (splitApps && value.TypeCode == 1001)
            {
                if (stack.Count != 0 || ignoredDepth != 0) result.Warnings.Add("Незакрытый список XDATA.");
                stack.Clear(); ignoredDepth = 0; label = null;
                current = new XDataNode(text, "RegApp", value.TypeCode);
                root.Add(current);
                continue;
            }
            if (value.TypeCode == 1002 && text == "{")
            {
                if (stack.Count >= MaxDepth || ignoredDepth != 0)
                {
                    if (ignoredDepth == 0) result.Warnings.Add("Превышена глубина списка XDATA.");
                    ignoredDepth++; continue;
                }
                var group = new XDataNode(label ?? "Список", "{ }", 1002);
                current.Add(group); stack.Push(current); current = group; label = null;
            }
            else if (value.TypeCode == 1002 && text == "}")
            {
                if (ignoredDepth != 0) { ignoredDepth--; continue; }
                if (stack.Count == 0) result.Warnings.Add("Закрывающая скобка без открывающей в XDATA.");
                else current = stack.Pop();
                label = null;
            }
            else if (ignoredDepth == 0)
            {
                current.Add(new XDataNode("[" + value.TypeCode.ToString(CultureInfo.InvariantCulture) + "]", text, value.TypeCode));
                label = value.TypeCode == 1000 && !text.Contains("=") ? text : null;
            }
        }
        if (stack.Count != 0 || ignoredDepth != 0) result.Warnings.Add("Незакрытый список XDATA.");
        if (values.Count > MaxValues) result.Warnings.Add("Превышен лимит значений XDATA/XRecord.");
    }

    private static void ProjectList(XDataNode parent, ParsedEntityData result)
    {
        foreach (var node in parent.Children)
        {
            if (node.TypeCode == 1002 && IsMaterial(node.Name))
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CollectFields(node, fields);
                result.Materials.Add(Material(fields));
            }
            else if (node.Children.Count != 0) ProjectList(node, result);
            else if (node.TypeCode == 1000 && TryField(node.Value, out var key, out var value))
                SetHeader(result.Properties, key, value);
        }
    }

    private static void CollectFields(XDataNode parent, IDictionary<string, string> fields)
    {
        foreach (var node in parent.Children)
        {
            if (node.TypeCode == 1000 && TryField(node.Value, out var key, out var value)) fields[Canonical(key)] = value;
            if (node.Children.Count != 0) CollectFields(node, fields);
        }
    }

    private static bool TryField(string text, out string key, out string value)
    {
        var index = text.IndexOf('=');
        key = index > 0 ? text.Substring(0, index).Trim() : "";
        value = index > 0 ? text.Substring(index + 1) : "";
        return key.Length != 0;
    }

    private static void ParseXml(DataRecord record, XDataNode node, ParsedEntityData result)
    {
        // String chunks form one XML payload only when every value is textual.
        if (record.Values.Count == 0 || record.Values.Any(value => (value.TypeCode != 1 && value.TypeCode != 1000) || !(value.Value is string))) return;
        var length = record.Values.Sum(value => (long)((string)value.Value).Length);
        if (length > MaxXmlCharacters) { result.Warnings.Add(record.Name + ": превышен лимит XML."); return; }
        var xml = string.Concat(record.Values.Select(value => (string)value.Value));
        if (!xml.TrimStart().StartsWith("<", StringComparison.Ordinal)) return;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters };
            // Validate depth and node count before materializing the XML graph.
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
            {
                var count = 0;
                while (reader.Read())
                    if (reader.Depth > MaxDepth || ++count > MaxValues) throw new XmlException("Превышены пределы дерева XML.");
            }
            using var safeReader = XmlReader.Create(new StringReader(xml), settings);
            var document = XDocument.Load(safeReader);
            var root = document.Root!;
            node.Add(XmlNode(root));
            if (!root.Name.LocalName.Equals("VisualTreeString", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var properties in root.Elements().Where(element => element.Name.LocalName == "Properties"))
            {
                foreach (var field in properties.Attributes()) SetHeader(result.Properties, field.Name.LocalName, field.Value);
                foreach (var field in properties.Elements()) SetHeader(result.Properties, field.Name.LocalName, field.Value);
            }
            foreach (var materials in root.Elements().Where(element => element.Name.LocalName == "Materials"))
                foreach (var material in materials.Elements().Where(element => IsMaterial(element.Name.LocalName)))
                {
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in material.Attributes()) fields[Canonical(field.Name.LocalName)] = field.Value;
                    foreach (var field in material.Elements()) fields[Canonical(field.Name.LocalName)] = field.Value;
                    result.Materials.Add(Material(fields));
                }
        }
        catch (XmlException error) { result.Warnings.Add(record.Name + ": " + error.Message); }
    }

    private static XDataNode XmlNode(XElement element)
    {
        var node = new XDataNode(element.Name.ToString(), string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value)));
        foreach (var attribute in element.Attributes()) node.Add(new XDataNode("@" + attribute.Name, attribute.Value));
        foreach (var child in element.Elements()) node.Add(XmlNode(child));
        return node;
    }

    private static void SetHeader(IDictionary<string, string> fields, string key, string value)
    {
        var canonical = Canonical(key);
        if (canonical == "Type" || canonical == "Number" || canonical == "Name" || canonical == "Title" || canonical == "ProjectReference")
            fields[canonical] = value;
    }

    private static MaterialItemViewModel Material(IDictionary<string, string> source)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in source) fields[Canonical(field.Key)] = field.Value;
        string Get(string key, string fallback = "") => fields.TryGetValue(key, out var value) ? value : fallback;
        var flag = Get("IsInSpec");
        bool? inSpec = flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase) ? true :
            flag == "0" || flag.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : (bool?)null;
        return new MaterialItemViewModel
        {
            Category = Get("Category"), Name = Get("Name"), Count = Get("Count"),
            IsInSpec = inSpec, Comment = Get("Comment")
        };
    }

    internal static string Canonical(string key)
    {
        switch (key.ToLowerInvariant())
        {
            case "type": case "тип": return "Type";
            case "number": case "номер": return "Number";
            case "name": case "название": case "марка": return "Name";
            case "title": case "заголовок": return "Title";
            case "projectreference": case "проект": return "ProjectReference";
            case "category": case "категория": return "Category";
            case "count": case "количество": return "Count";
            case "isinspec": case "вспецификацию": return "IsInSpec";
            case "comment": case "примечание": return "Comment";
            default: return key;
        }
    }

    internal static bool IsMaterial(string name) => name.Equals("Material", StringComparison.OrdinalIgnoreCase) || name == "Материал";
    internal static bool IsKnownApp(string name) => name.Equals("ESMT_LEP_v1.0", StringComparison.OrdinalIgnoreCase) || name.Equals("BobrovXDATA", StringComparison.OrdinalIgnoreCase);
    private static string Format(object value) => value is byte[] bytes ? BitConverter.ToString(bytes).Replace("-", "") :
        value is IFormattable formatted ? formatted.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "";
}
