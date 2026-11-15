// Checks the formula-mode behaviors described in docs/calculated-string.md (issue #43) on the pinned NCalc.
using System;
using System.Collections.Generic;
using System.Globalization;
using AcadPaletteSetXData.Core;

var culture = CultureInfo.GetCultureInfo("ru-RU");
var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
{
    ["Number"] = "1", ["Voltage"] = "220 В", ["Power"] = 12.5, ["Count"] = 2L, ["Enabled"] = true, ["Empty"] = null, ["Name"] = "Щит",
};
foreach (var format in args.Length > 0 ? args : new[]
{
    "=@@[Count] > 1 ? \"много\" : \"один\"",
    "=@@[Number] + 1",
    "=@@[Number] & 1",
    "=@@[Voltage] > 220",
    "=@@[Voltage] == \"220 В\"",
    "=@@[Name] == \"щит\"",
    "=@@[Empty] + 1",
    "=isNull(@@[Empty])",
    "=IF(isNull(@@[Empty]), 0, @@[Empty]) + 1",
    "=@@[Count] / 4",
    "=7 / 2",
    "=@@[Power] / 0",
    "=true AND 1 < 2",
    "=\"a\" + \"b\"",
    "=6 & 3",
    "=TEXT(@@[Enabled], \"0\")",
    "=ROUND(@@[Power] * 1.005, 1)",
})
{
    var expression = FormulaExpression.Compile(format);
    string result;
    if (expression.Error != null) result = "ERROR(text): " + expression.Error;
    else
        try { result = "«" + expression.Evaluate(f => values[f.Reference!], f => Convert.ToString(values[f.Reference!], culture) ?? "", culture) + "»"; }
        catch (FormulaException e) { result = "ERROR(value): " + e.Message; }
    Console.WriteLine(format + "  →  " + result);
}
