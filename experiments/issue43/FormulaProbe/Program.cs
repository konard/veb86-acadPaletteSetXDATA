using System;
using System.Linq;
using System.Threading;
using AcadPaletteSetXData.Core;

var max = FormulaExpression.MaxLength;
var formats = new[]
{
    "=" + new string('-', max - 2) + "1",
    "=" + new string('!', max - 2) + "1",
    "=" + new string('(', max - 1),
    "=" + string.Concat(Enumerable.Repeat("1+", max / 2 - 1)) + "1",
    "=" + string.Concat(Enumerable.Repeat("IF(1,", max / 5 - 1)),
    "=" + string.Concat(Enumerable.Repeat("\"a\"&", max / 4 - 1)) + "1",
    "=" + new string('-', 90) + "1",
    "=" + new string('-', 99) + "1",
};
var thread = new Thread(() =>
{
    foreach (var format in formats)
        Console.WriteLine(format.Substring(0, Math.Min(20, format.Length)) + "… (" + format.Length + "): " + (FormulaExpression.Compile(format).Error ?? "OK"));
}, 256 * 1024);
thread.Start();
thread.Join();
