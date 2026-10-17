using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;
using Xunit.Abstractions;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// The load test of TZXDATA 10.6 on <c>examples/tzxdata-stage10-load-schema.json</c>: 1000 objects × 50 parameters,
/// the caches of 10.1–10.2 and the merge of 10.4 without access to the drawing. The same objects are checked by
/// experiments/AcadPaletteSetXData.LoadReport in CI and created in AutoCAD by examples/tzxdata-stage10-load.lsp.
/// The tests run alone, after the parallel ones, so that other tests do not take the processor from the measurement.
/// </summary>
[Collection(LoadTests.Collection)]
public sealed class LoadTests
{
    public const string Collection = "TZXDATA 10.6";
    private const double BudgetMs = 300;
    private readonly ITestOutputHelper output;

    public LoadTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void ThousandObjectsAreBuiltWithinTheBudgetAndARepeatedSelectionDoesNotParseXData()
    {
        var schema = LoadData.Schema();
        var cache = new ObjectValuesCache(schema);
        var (selection, grid) = Model();
        selection.Apply(LoadData.Read(), cache: cache);
        // Each object is parsed once: the v2 values and the XDATA tree.
        Assert.Equal(LoadData.Objects, cache.ParseCount);
        Assert.Equal(LoadData.Objects, cache.TreeParseCount);

        var best = TimeSpan.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            // A new selection event: fresh snapshots with the same values.
            selection.Apply(LoadData.Read(), cache: cache);
            if (grid.BuildTime < best) best = grid.BuildTime;
        }
        output.WriteLine(grid.BuildSummary + " Лучшее: " + best.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " мс.");
        Assert.Equal(LoadData.Objects, cache.ParseCount);
        Assert.Equal(LoadData.Objects, cache.TreeParseCount);
        Assert.Equal(1, schema.LoadCount);
        Assert.Equal(LoadData.Objects, selection.Entities.Count);
        Assert.Equal("Нагрузочный объект", grid.TemplateText);
        Assert.Equal(ObjectCalculation.Various, Row(grid, "L01").Value);
        Assert.Equal("3", Row(grid, "L03").Value);
        Assert.Equal(ObjectCalculation.Various, Row(grid, "L49").Value);
        Assert.Equal(PropertyRowKind.Table, grid.Roots.Single(row => row.Id == "L50").Kind);
        Assert.True(best.TotalMilliseconds <= BudgetMs, "Построение VST заняло " + best.TotalMilliseconds + " мс.");
    }

    [Fact]
    public void WritingOneObjectParsesOnlyThatObjectAgain()
    {
        var cache = new ObjectValuesCache(LoadData.Schema());
        var database = new MemoryDatabase(LoadData.Read());
        var (selection, _) = Model();
        selection.Apply(database.Read(), cache: cache);
        var handle = LoadData.Handle(7);
        new XDataWriter(database.Begin, cache).WriteSelection(new[] { handle }, SelectionEdit.SetParameter("L01", "Записано"));
        Assert.Equal(1, database.Commits);

        selection.Apply(database.Read(), cache: cache);
        Assert.Equal(LoadData.Objects + 1, cache.ParseCount);
        Assert.Equal(LoadData.Objects + 1, cache.TreeParseCount);
        Assert.Equal("Записано", selection.Values.Single(values => values.Handle == handle).Find("L01")!.Value);
    }

    [Fact]
    public void AnExternalChangeAndItsUndoAreNoticed()
    {
        var schema = LoadData.Schema();
        var cache = new ObjectValuesCache(schema);
        var (selection, _) = Model();
        var snapshots = LoadData.Read();
        selection.Apply(snapshots, cache: cache);

        // Another command (or Redo) changed object 3: only it is parsed again.
        var changed = snapshots.ToArray();
        changed[3] = new XDataPatch(schema.Get()).Apply(snapshots[3], SelectionEdit.SetParameter("L01", "Снаружи"), cache.Get(snapshots[3]));
        selection.Apply(changed, cache: cache);
        Assert.Equal("Снаружи", selection.Values[3].Find("L01")!.Value);
        Assert.Equal(LoadData.Objects + 1, cache.TreeParseCount);

        // Undo returns the old XDATA: the cache does not keep the changed values.
        selection.Apply(LoadData.Read(), cache: cache);
        Assert.Equal("Щит-0", selection.Values[3].Find("L01")!.Value);
        Assert.Equal(LoadData.Objects + 2, cache.ParseCount);
        Assert.Equal(LoadData.Objects + 2, cache.TreeParseCount);
    }

    [Fact]
    public void ANewSchemaParsesTheSelectionOnceAgain()
    {
        var schema = LoadData.Schema();
        var cache = new ObjectValuesCache(schema);
        var (selection, _) = Model();
        selection.Apply(LoadData.Read(), cache: cache);
        // VELEC, import, Undo/Redo of the schema or another document (10.1) invalidate the schema cache.
        schema.Invalidate();
        Assert.Equal(0, cache.Count);
        selection.Apply(LoadData.Read(), cache: cache);
        selection.Apply(LoadData.Read(), cache: cache);
        Assert.Equal(2 * LoadData.Objects, cache.ParseCount);
        Assert.Equal(2 * LoadData.Objects, cache.TreeParseCount);
        Assert.Equal(2, schema.LoadCount);
    }

    [Fact]
    public void TheValueCacheKeepsTheLastTenThousandObjects()
    {
        var cache = new ObjectValuesCache(LoadData.Schema());
        var total = ObjectValuesCache.DefaultCapacity + 2000;
        cache.Get(LoadData.Read(total));
        Assert.Equal(ObjectValuesCache.DefaultCapacity, cache.Count);
        Assert.Null(cache.Find(LoadData.Handle(0)));
        Assert.Null(cache.Find(LoadData.Handle(1999)));
        Assert.NotNull(cache.Find(LoadData.Handle(2000)));
        // The recently read objects are still cached.
        cache.Get(LoadData.Read(LoadData.Objects, total - LoadData.Objects));
        Assert.Equal(total, cache.ParseCount);
    }

    [Fact]
    public void TheMergeDoesNotReadTheDrawing()
    {
        var model = new PaletteViewModel();
        var source = new Source { Snapshot = LoadData.Read(), Values = new ObjectValuesCache(LoadData.Schema()) };
        using (new SelectionController(source, model.Selection))
        {
            var built = 0;
            model.Grid.Built += (_, _) => built++;
            model.Grid.CollapseAllCommand.Execute(null);
            model.Grid.ExpandAllCommand.Execute(null);
            model.Grid.Toggle(model.Grid.Roots[0]);
            Assert.Equal(1, source.Reads);
            Assert.Equal(LoadData.Objects, source.Values.ParseCount);
            Assert.Equal(LoadData.Objects, source.Values.TreeParseCount);
            Assert.Contains("VST: объектов " + LoadData.Objects, model.Grid.BuildSummary);
            output.WriteLine(model.Grid.BuildSummary + " Перестроений без чтения чертежа: " + built + ".");
        }
    }

    [Fact]
    public void WarningsOfASelectionDoNotStayInTheCachedTree()
    {
        var cache = new ObjectValuesCache(LoadData.Schema());
        var snapshot = LoadData.Object(1);
        var first = cache.Parse(snapshot);
        first.Warnings.Add("Предупреждение выделения");
        var second = cache.Parse(LoadData.Object(1));
        Assert.Equal(1, cache.TreeParseCount);
        Assert.Same(first.Tree, second.Tree);
        Assert.DoesNotContain("Предупреждение выделения", second.Warnings);
    }

    private static (SelectionViewModel Selection, PropertyGridViewModel Grid) Model()
    {
        var selection = new SelectionViewModel(new PropertyEditorViewModel());
        return (selection, new PropertyGridViewModel(selection, CultureInfo.InvariantCulture));
    }

    private static PropertyRow Row(PropertyGridViewModel grid, string id) => grid.Find("p:" + id) ?? throw new InvalidOperationException("Нет строки " + id);

    private sealed class Source : ISelectionSource, IObjectValuesSource
    {
        public IReadOnlyList<EntityDataSnapshot> Snapshot { get; set; } = Array.Empty<EntityDataSnapshot>();
        public int Reads { get; private set; }
        public ObjectValuesCache Values { get; set; } = null!;
        ObjectValuesCache? IObjectValuesSource.Values => Values;
        public event EventHandler? SelectionChanged { add { } remove { } }
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() { Reads++; return Snapshot; }
    }

    /// <summary>The drawing: every read returns fresh snapshots, as AutoCAD does after each selection event.</summary>
    private sealed class MemoryDatabase
    {
        private EntityDataSnapshot[] objects;
        public MemoryDatabase(EntityDataSnapshot[] objects) => this.objects = objects;
        public int Commits { get; private set; }
        public IReadOnlyList<EntityDataSnapshot> Read() => objects.Select(Copy).ToArray();
        public IXDataWriteTransaction Begin(IReadOnlyList<string> handles) => new Transaction(this, handles);

        private static EntityDataSnapshot Copy(EntityDataSnapshot snapshot) =>
            new EntityDataSnapshot(snapshot.Handle, snapshot.EntityType, snapshot.XData.ToArray(), snapshot.Records.ToArray());

        private sealed class Transaction : IXDataWriteTransaction
        {
            private readonly MemoryDatabase database;
            private readonly List<EntityDataSnapshot> staged = new List<EntityDataSnapshot>();
            public Transaction(MemoryDatabase database, IReadOnlyList<string> handles)
            {
                this.database = database;
                Snapshots = handles.Select(handle => Copy(database.objects.Single(item => item.Handle == handle))).ToArray();
            }
            public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }
            public void Write(EntityDataSnapshot snapshot) => staged.Add(snapshot);
            public void Commit()
            {
                database.Commits++;
                database.objects = database.objects.Select(item => staged.FirstOrDefault(written => written.Handle == item.Handle) ?? item).ToArray();
            }
            public void Dispose() { }
        }
    }
}

[CollectionDefinition(LoadTests.Collection, DisableParallelization = true)]
public sealed class LoadTestsCollection { }
