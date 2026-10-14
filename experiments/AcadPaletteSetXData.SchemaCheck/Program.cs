using System;
using System.IO;
using AcadPaletteSetXData.Core;

// Validates schema.json files (TZXDATA 4.5) and optionally rewrites them in the canonical form.
// Usage: SchemaCheck [--format] <schema.json>...   Exit code 1 when any file has errors.
var format = false;
var failed = false;
foreach (var argument in args)
{
    if (argument == "--format") { format = true; continue; }
    var result = ParameterSchemaJson.LoadFile(argument);
    Console.WriteLine(argument + ": " + (result.HasErrors ? "ошибки" : "без ошибок") + ", замечаний: " + result.Issues.Count);
    foreach (var issue in result.Issues) Console.WriteLine("  " + issue);
    failed |= result.HasErrors;
    if (format && result.Schema != null)
    {
        var canonical = ParameterSchemaJson.Write(result.Schema);
        var changed = canonical != File.ReadAllText(argument);
        if (changed) ParameterSchemaJson.WriteFile(argument, result.Schema);
        Console.WriteLine("  " + (changed ? "переписан в канонической форме" : "уже в канонической форме"));
    }
}
return failed ? 1 : 0;
