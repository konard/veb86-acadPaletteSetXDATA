using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.Core.Tests;

// The load report of TZXDATA 10.6 without AutoCAD: 1000 objects × 50 parameters of examples/tzxdata-stage10-load-schema.json
// (the objects of examples/tzxdata-stage10-load.lsp). Usage: LoadReport [schema.json]
// Exit code 1 when the VST is built in more than 300 ms or a repeated selection parses XDATA again.
// --profile N [--cold] repeats the selection N times for a profiler (dotnet-trace) and prints nothing else.
const double BudgetMs = 300;
var profile = Array.IndexOf(args, "--profile") is var index && index >= 0 ? int.Parse(args[index + 1], CultureInfo.InvariantCulture) : 0;
var path = args.Where((argument, i) => !argument.StartsWith("--", StringComparison.Ordinal) && (i == 0 || args[i - 1] != "--profile"))
    .FirstOrDefault() ?? "examples/tzxdata-stage10-load-schema.json";
var schema = LoadData.Schema(path);
var cache = new ObjectValuesCache(schema);
var selection = new SelectionViewModel(new PropertyEditorViewModel());
var grid = new PropertyGridViewModel(selection, CultureInfo.InvariantCulture);
var failed = false;
var gcPause = TimeSpan.Zero;
var gcCounts = new int[3];
Console.WriteLine("Схема: " + path + "; объектов " + LoadData.Objects + " × параметров " + LoadData.Parameters + ".");

// JIT warm-up on other objects, so that the first selection below measures the work, not the compilation.
selection.Apply(LoadData.Read(50, 100000), cache: cache);
cache.Clear();
if (profile > 0)
{
    // --cold: every selection starts with an empty cache (parsing); otherwise the same set is selected again.
    var cold = args.Contains("--cold");
    for (var run = 0; run < profile; run++)
    {
        if (cold) cache.Clear();
        selection.Apply(LoadData.Read(), cache: cache);
    }
    return 0;
}

Console.WriteLine();
Console.WriteLine("== Первое выделение (кэш значений пуст), 3 раза ==");
// Each run starts with an empty value cache and a collected heap: the garbage of building the snapshots belongs to
// reading the drawing, the criterion is the time after the snapshots are read. The best run is checked, as in the tests.
var timer = new Stopwatch();
var coldBuild = double.MaxValue;
var coldApply = double.MaxValue;
for (var run = 1; run <= 3; run++)
{
    cache.Clear();
    var snapshots = LoadData.Read();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    Gc();
    timer.Restart();
    cache.Get(snapshots);
    var read = timer.Elapsed.TotalMilliseconds;
    selection.Apply(snapshots, cache: cache);
    var total = timer.Elapsed.TotalMilliseconds;
    coldBuild = Math.Min(coldBuild, grid.BuildTime.TotalMilliseconds);
    coldApply = Math.Min(coldApply, total);
    Console.WriteLine("#" + run + ": " + Ms(total) + " мс: разбор XDATA v2 " + Ms(read) + " мс (разборов " + cache.ParseCount + "), деревьев XDATA " + cache.TreeParseCount + "; " + Gc());
    Console.WriteLine("    " + grid.BuildSummary);
}
Check("строк VST " + grid.RowCount, grid.RowCount > LoadData.Parameters);
Check("VST ≤ " + BudgetMs + " мс (лучшее " + Ms(coldBuild) + ")", coldBuild <= BudgetMs);
Console.WriteLine("выделение целиком с разбором XDATA: лучшее " + Ms(coldApply) + " мс");
var parses = cache.ParseCount;
var trees = cache.TreeParseCount;

Console.WriteLine();
Console.WriteLine("== Повторное выделение того же набора (новые снапшоты, те же значения) ==");
var best = double.MaxValue;
var bestBuild = double.MaxValue;
for (var run = 1; run <= 5; run++)
{
    var snapshots = LoadData.Read();
    timer.Restart();
    cache.Get(snapshots);
    selection.Apply(snapshots, cache: cache);
    var total = timer.Elapsed.TotalMilliseconds;
    best = Math.Min(best, total);
    bestBuild = Math.Min(bestBuild, grid.BuildTime.TotalMilliseconds);
    Console.WriteLine("#" + run + ": " + Ms(total) + " мс, из них VST " + Ms(grid.BuildTime.TotalMilliseconds) + " мс; разборов XDATA " + cache.ParseCount + ", деревьев " + cache.TreeParseCount + "; " + Gc());
}
Check("VST ≤ " + BudgetMs + " мс (лучшее " + Ms(bestBuild) + ")", bestBuild <= BudgetMs);
Check("выделение целиком ≤ " + BudgetMs + " мс (лучшее " + Ms(best) + ")", best <= BudgetMs);
Check("XDATA не разбирается повторно", cache.ParseCount == parses && cache.TreeParseCount == trees);

Console.WriteLine();
Console.WriteLine("== Запись одного объекта ==");
cache.Invalidate(new[] { LoadData.Handle(7) });
cache.Get(LoadData.Read());
selection.Apply(LoadData.Read(), cache: cache);
Console.WriteLine("разборов XDATA " + cache.ParseCount + ", деревьев " + cache.TreeParseCount);
Check("разобран заново только записанный объект", cache.ParseCount == parses + 1 && cache.TreeParseCount == trees + 1);

Console.WriteLine();
Console.WriteLine("== Кэш значений: LRU ==");
var lru = new ObjectValuesCache(LoadData.Schema(path));
timer.Restart();
lru.Get(LoadData.Read(ObjectValuesCache.DefaultCapacity + 2000));
Console.WriteLine(Line("прочитано " + (ObjectValuesCache.DefaultCapacity + 2000) + " объектов", timer.Elapsed.TotalMilliseconds) + "; в кэше " + lru.Count);
Check("не больше " + ObjectValuesCache.DefaultCapacity + " объектов", lru.Count == ObjectValuesCache.DefaultCapacity && lru.Find(LoadData.Handle(0)) is null);
Check("схема прочитана один раз", schema.LoadCount == 1);
return failed ? 1 : 0;

void Check(string text, bool ok)
{
    Console.WriteLine((ok ? "OK: " : "ОШИБКА: ") + text);
    failed |= !ok;
}

// Garbage collections since the previous call: the pauses are part of the measured times.
string Gc()
{
    var pause = GC.GetTotalPauseDuration();
    var counts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
    var text = "GC: " + string.Join("/", counts.Zip(gcCounts, (now, before) => now - before)) + ", паузы " + Ms((pause - gcPause).TotalMilliseconds) + " мс";
    gcPause = pause; gcCounts = counts;
    return text;
}

static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
static string Line(string text, double ms) => text + ": " + Ms(ms) + " мс";
