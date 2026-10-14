using System;
using System.Collections.Generic;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// How many objects of a drawing use each parameter and template (for the import report, TZXDATA 5.3).
/// Reads only the <see cref="DrawingNames.RegApp"/> XDATA: format v2 (<c>(1070 . 2)</c>, pairs of
/// <c>Id</c> + value, tables as nested lists, TZXDATA 6.1) and the current v1 strings <c>key=value</c>.
/// </summary>
public sealed class SchemaUsage
{
    private const string TemplateKey = "TEMPLATE";
    private readonly Dictionary<string, int> parameters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> templates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Objects with <see cref="DrawingNames.RegApp"/> XDATA.</summary>
    public int ObjectCount { get; private set; }

    /// <summary>Number of objects using the parameter or template; enums are not stored in XDATA and give 0.</summary>
    public int Count(SchemaSection section, string? id)
    {
        if (id is null) return 0;
        var map = section == SchemaSection.Parameter ? parameters : section == SchemaSection.Template ? templates : null;
        return map != null && map.TryGetValue(id, out var count) ? count : 0;
    }

    public static SchemaUsage Scan(IEnumerable<IReadOnlyList<DataValue>> objects)
    {
        var usage = new SchemaUsage();
        foreach (var xdata in objects) usage.Add(xdata);
        return usage;
    }

    /// <summary>Adds the XDATA of one object (all applications, as read from the entity).</summary>
    public void Add(IReadOnlyList<DataValue> xdata)
    {
        var values = AppValues(xdata);
        if (values is null) return;
        ObjectCount++;
        var usedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values.Count != 0 && values[0].TypeCode == 1070 && IsInteger(values[0].Value, 2))
            ReadV2(values, 1, usedParameters, usedTemplates);
        else
            foreach (var value in values)
                if (value.TypeCode == 1000 && value.Value is string text)
                {
                    var index = text.IndexOf('=');
                    var key = index > 0 ? text.Substring(0, index).Trim() : "";
                    if (key.Length != 0) usedParameters.Add(key);
                }
        foreach (var id in usedParameters) parameters[id] = Count(SchemaSection.Parameter, id) + 1;
        foreach (var id in usedTemplates) templates[id] = Count(SchemaSection.Template, id) + 1;
    }

    private static List<DataValue>? AppValues(IReadOnlyList<DataValue> xdata)
    {
        List<DataValue>? result = null;
        var inside = false;
        foreach (var value in xdata.Take(XDataParser.MaxValues))
        {
            if (value.TypeCode == 1001)
            {
                inside = value.Value is string app && string.Equals(app, DrawingNames.RegApp, StringComparison.OrdinalIgnoreCase);
                if (inside) result ??= new List<DataValue>();
                continue;
            }
            if (inside) result!.Add(value);
        }
        return result;
    }

    /// <summary>Pairs of <c>(1000 . Id)</c> + value; a list value is a table whose rows are lists of pairs.</summary>
    private static int ReadV2(List<DataValue> values, int i, HashSet<string> usedParameters, HashSet<string> usedTemplates, int depth = 0)
    {
        while (i < values.Count)
        {
            if (IsBracket(values[i], "}")) return i + 1;
            if (!(values[i].TypeCode == 1000 && values[i].Value is string id)) { i = Skip(values, i); continue; }
            i++;
            if (i >= values.Count) break;
            if (string.Equals(id, TemplateKey, StringComparison.OrdinalIgnoreCase))
            {
                if (values[i].TypeCode == 1000 && values[i].Value is string template && template.Length != 0) usedTemplates.Add(template);
            }
            else if (!ParameterSchema.ReservedIds.Contains(id, StringComparer.OrdinalIgnoreCase)) usedParameters.Add(id);

            if (IsBracket(values[i], "{") && depth < XDataParser.MaxDepth)
            {
                i++;
                // Rows: { (1000 . column) value ... }
                while (i < values.Count && !IsBracket(values[i], "}"))
                    i = IsBracket(values[i], "{") ? ReadV2(values, i + 1, usedParameters, usedTemplates, depth + 1) : i + 1;
                i++;
            }
            else i = Skip(values, i);
        }
        return i;
    }

    /// <summary>Skips one value, or a whole list.</summary>
    private static int Skip(List<DataValue> values, int i)
    {
        if (!IsBracket(values[i], "{")) return i + 1;
        var level = 0;
        do
        {
            if (IsBracket(values[i], "{")) level++;
            else if (IsBracket(values[i], "}")) level--;
            i++;
        } while (i < values.Count && level > 0);
        return i;
    }

    private static bool IsBracket(DataValue value, string bracket) => value.TypeCode == 1002 && value.Value is string text && text == bracket;

    private static bool IsInteger(object value, long expected) => value switch
    {
        short s => s == expected,
        int n => n == expected,
        long l => l == expected,
        _ => false
    };
}
