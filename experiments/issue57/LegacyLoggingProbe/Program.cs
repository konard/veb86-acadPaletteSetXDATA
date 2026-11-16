using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using AcadPaletteSetXData.Core;

// 1. Syntax errors in the default context (the texts shown in «Диагностика»).
foreach (var format in new[] { "=1 +", "=(1 + 2", "=@@[Power] *", "=1 2", "=@@[Power]" })
    Console.WriteLine("default  " + format + " → " + (FormulaExpression.Compile(format).Error ?? "OK"));

// 2. The same formulas next to the old logging abstractions (the AutoCAD case of issue #57).
var directory = AppContext.BaseDirectory;
var context = new Legacy(directory);
var logging = context.LoadFromAssemblyName(new AssemblyName("Microsoft.Extensions.Logging.Abstractions"));
Console.WriteLine("legacy   " + logging.GetName().Version + ", NullLoggerFactory: " + (logging.GetType("Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory") != null));
var ncalc = context.LoadFromAssemblyName(new AssemblyName("NCalc.Core"));
try
{
    ncalc.GetType("NCalc.Expression")!.GetConstructor(new[] { typeof(string), typeof(CultureInfo) })!.Invoke(new object[] { "1 + 1", CultureInfo.InvariantCulture });
    Console.WriteLine("legacy   new NCalc.Expression(\"1 + 1\") → OK");
}
catch (TargetInvocationException error)
{
    Console.WriteLine("legacy   new NCalc.Expression(\"1 + 1\") → " + error.InnerException!.GetType().Name + ": " + (error.InnerException.InnerException?.Message ?? error.InnerException.Message));
}
var core = context.LoadFromAssemblyName(typeof(FormulaExpression).Assembly.GetName());
var formulas = core.GetType(typeof(FormulaExpression).FullName!)!;
foreach (var format in new[] { "=@@[Power]", "=@@[Power] * 2", "=ROUND(@@[Power] * 3, 1) & \" кВт\"" })
{
    var compiled = formulas.GetMethod("Compile")!.Invoke(null, new object[] { format })!;
    Console.WriteLine("legacy   " + format + " → " + (formulas.GetProperty("Error")!.GetValue(compiled) ?? "OK"));
}
Console.WriteLine("legacy   loaded: " + string.Join(", ", context.Assemblies.Select(item => item.GetName().Name + " " + item.GetName().Version)));

sealed class Legacy(string directory) : AssemblyLoadContext("issue57-legacy-logging")
{
    private static readonly string[] Own = { "AcadPaletteSetXData.Core", "NCalc.Core", "NCalc.Parser", "NCalc.Domain", "ExtendedNumerics.BigDecimal" };

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "Microsoft.Extensions.Logging.Abstractions") return LoadFromAssemblyPath(Path.Combine(directory, "legacy", name.Name + ".dll"));
        return Own.Contains(name.Name) ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }
}
