using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Objects of the load test (TZXDATA 10.6) for <c>examples/tzxdata-stage10-load-schema.json</c>: the template <c>LOAD</c>
/// with 48 scalars of five types, the formula <c>L49</c> and the table <c>L50</c> of two rows — 50 parameters.
/// The values are the same as in <c>examples/tzxdata-stage10-load.lsp</c>. Shared by the tests and experiments/AcadPaletteSetXData.LoadReport.
/// </summary>
internal static class LoadData
{
    public const int Objects = 1000;
    public const int Parameters = 50;
    public const string Template = "LOAD";
    public static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "examples", "tzxdata-stage10-load-schema.json");

    /// <summary>The schema cache of one document; <see cref="SchemaCache.LoadCount"/> counts the reads of the schema.</summary>
    public static SchemaCache Schema(string? path = null)
    {
        var load = ParameterSchemaJson.LoadFile(path ?? SchemaPath);
        if (load.Schema is null || load.HasErrors) throw new InvalidOperationException((path ?? SchemaPath) + ": " + string.Join("; ", load.Issues));
        return new SchemaCache(() => new SchemaNodReadResult(load.Schema, null, load.Issues));
    }

    /// <summary>A fresh read of <paramref name="count"/> objects: new snapshots and lists with the same values, as after a new selection event.</summary>
    public static EntityDataSnapshot[] Read(int count = Objects, int first = 0) => Enumerable.Range(first, count).Select(Object).ToArray();

    public static string Handle(int n) => (0x1000 + n).ToString("X");

    /// <summary>
    /// <c>L01</c>, <c>L06</c>… String «Щит-0/1/2»; <c>L02</c>… Double n/2; <c>L03</c>… Integer = the number of the parameter;
    /// <c>L04</c>… Boolean n mod 2; <c>L05</c>… Enum A/B/C; <c>L50</c>: rows M0 (1) and M1 (2).
    /// </summary>
    public static EntityDataSnapshot Object(int n)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)XDataV2.FormatVersion), V(1000, XDataV2.TemplateKey), V(1000, Template) };
        for (var i = 1; i <= 48; i++)
        {
            xdata.Add(V(1000, "L" + i.ToString("00")));
            xdata.Add(((i - 1) % 5) switch
            {
                0 => V(1000, "Щит-" + n % 3),
                1 => V(1040, n * 0.5),
                2 => V(1071, i),
                3 => V(1070, (short)(n % 2)),
                _ => V(1000, "ABC".Substring(n % 3, 1))
            });
        }
        xdata.Add(V(1000, "L50")); xdata.Add(V(1002, "{"));
        for (var row = 0; row < 2; row++)
        {
            xdata.Add(V(1002, "{"));
            xdata.Add(V(1000, "LMat_Mark")); xdata.Add(V(1000, "M" + row));
            xdata.Add(V(1000, "LMat_Count")); xdata.Add(V(1040, 1.0 + row));
            xdata.Add(V(1002, "}"));
        }
        xdata.Add(V(1002, "}"));
        return new EntityDataSnapshot(Handle(n), "Line", xdata, Array.Empty<DataRecord>());
    }

    private static DataValue V(int code, object value) => new DataValue(code, value);
}
