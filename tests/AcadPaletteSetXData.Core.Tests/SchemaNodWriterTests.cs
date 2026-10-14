using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class SchemaNodWriterTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");

    [Fact]
    public void FirstImportWritesEveryRecordInOneCommit()
    {
        var drawing = new FakeDrawing();
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        var result = drawing.Import(schema, SchemaImportMode.Replace);

        Assert.True(result.Changed);
        Assert.Equal(1, drawing.Commits);
        Assert.Empty(result.Erased);
        Assert.Equal(1 + schema.Parameters.Count + schema.Enums.Count + schema.Templates.Count, result.Written.Count);
        Assert.Equal(ParameterSchemaJson.Write(schema), ParameterSchemaJson.Write(drawing.Read().Schema!));
    }

    [Fact]
    public void ReimportOfTheSameSchemaChangesNothing()
    {
        var drawing = new FakeDrawing();
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        drawing.Import(schema, SchemaImportMode.Replace);
        var result = drawing.Import(ParameterSchemaJson.Clone(schema), SchemaImportMode.Merge, "другой.json");
        Assert.False(result.Changed);
        Assert.Equal(1, drawing.Commits);
        Assert.Equal("Replace", drawing.Read().Info!.Mode);
    }

    [Fact]
    public void OnlyChangedObjectsAndInfoAreWritten()
    {
        var drawing = new FakeDrawing();
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        drawing.Import(schema, SchemaImportMode.Replace);
        var changed = ParameterSchemaJson.Clone(schema);
        changed.FindParameter("Power")!.Caption = "Мощность, кВт";

        var result = drawing.Import(changed, SchemaImportMode.Merge, "new.json");
        Assert.Equal(new[] { "INFO", "PARAMETERS/Power" }, result.Written);
        Assert.Empty(result.Erased);
        var read = drawing.Read();
        Assert.Empty(read.Issues);
        Assert.Equal(("new.json", "Merge"), (read.Info!.Source, read.Info.Mode));
        Assert.Equal("Мощность, кВт", read.Schema!.FindParameter("Power")!.Caption);
    }

    [Fact]
    public void ReplaceErasesMissingObjectsAndKeepsForeignRecords()
    {
        var drawing = new FakeDrawing();
        drawing.Store.Add(new DataRecord("OTHER", new[] { new DataValue(1, "чужая запись") }));
        var schema = ParameterSchemaJson.LoadFile(ExamplePath).Schema!;
        drawing.Import(schema, SchemaImportMode.Replace);
        var smaller = ParameterSchemaJson.Clone(schema);
        smaller.Templates.RemoveAll(template => template.Id == "MOTOR");

        var result = drawing.Import(smaller, SchemaImportMode.Replace);
        Assert.Equal(new[] { "TEMPLATES/MOTOR" }, result.Erased);
        Assert.Contains(drawing.Store, record => record.Name == "OTHER");
        Assert.DoesNotContain(drawing.Store, record => record.Name == "TEMPLATES/MOTOR");
        Assert.Equal(ParameterSchemaJson.Write(smaller), ParameterSchemaJson.Write(drawing.Read().Schema!));
    }

    [Fact]
    public void AnIdWhoseCaseChangedIsRewrittenUnderTheNewKey()
    {
        var drawing = new FakeDrawing();
        var schema = SchemaNodTests.Schema("{ 'id': 'power', 'type': 'String', 'caption': 'c' }");
        drawing.Import(schema, SchemaImportMode.Replace);
        schema.Parameters[0].Id = "Power";
        var result = drawing.Import(schema, SchemaImportMode.Replace);
        Assert.Equal(new[] { "PARAMETERS/power" }, result.Erased);
        Assert.Contains("PARAMETERS/Power", result.Written);
        Assert.Equal(new[] { "INFO", "PARAMETERS/Power" }, drawing.Store.Select(record => record.Name));
    }

    [Fact]
    public void ASchemaWithErrorsIsNotWritten()
    {
        var drawing = new FakeDrawing();
        var schema = SchemaNodTests.Schema("{ 'id': 'P', 'type': 'String', 'caption': 'c' }, { 'id': 'p', 'type': 'String', 'caption': 'c' }");
        Assert.Throws<InvalidOperationException>(() => drawing.Import(schema, SchemaImportMode.Replace));
        Assert.Equal(0, drawing.Commits);
        Assert.Empty(drawing.Store);
    }

    [Fact]
    public void CacheReadsNodOnceUntilInvalidated()
    {
        var drawing = new FakeDrawing();
        drawing.Import(ParameterSchemaJson.LoadFile(ExamplePath).Schema!, SchemaImportMode.Replace);
        var cache = new SchemaCache(drawing.Read);
        var invalidated = 0;
        cache.Invalidated += (sender, args) => invalidated++;

        cache.Invalidate();
        Assert.Equal(0, invalidated);
        var first = cache.Get();
        Assert.Same(first, cache.Get());
        Assert.Equal(1, cache.LoadCount);
        Assert.Equal("Double", first.Parameters["power"].Type);
        Assert.Equal("_DC_24V", first.Enums["VOLTAGE"].Items[2].Key);
        Assert.True(first.Templates.ContainsKey("motor"));

        cache.Invalidate();
        Assert.Equal(1, invalidated);
        Assert.False(cache.IsLoaded);
        Assert.NotSame(first, cache.Get());
        Assert.Equal(2, cache.LoadCount);

        var empty = new SchemaCache(new FakeDrawing().Read).Get();
        Assert.Null(empty.Schema);
        Assert.Empty(empty.Parameters);
    }

    /// <summary>Records of <c>VELEC</c>; a transaction applies its changes only on commit, like AutoCAD.</summary>
    private sealed class FakeDrawing
    {
        public List<DataRecord> Store { get; } = new List<DataRecord>();
        public int Commits { get; private set; }

        public SchemaNodReadResult Read() => SchemaNod.FromRecords(Store.ToList());

        public SchemaNodWriteResult Import(ParameterSchema schema, SchemaImportMode mode, string source = "schema.json")
        {
            using var transaction = new Transaction(this);
            return SchemaNodWriter.Write(transaction, schema, SchemaNodInfo.Now(source, mode));
        }

        private sealed class Transaction : ISchemaNodTransaction
        {
            private readonly FakeDrawing drawing;
            private readonly List<Action> pending = new List<Action>();
            public Transaction(FakeDrawing drawing) { this.drawing = drawing; Records = drawing.Store.ToList(); }
            public IReadOnlyList<DataRecord> Records { get; }
            public IReadOnlyList<string> Warnings => Array.Empty<string>();
            public void Write(DataRecord record) => pending.Add(() =>
            {
                var index = drawing.Store.FindIndex(existing => string.Equals(existing.Name, record.Name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) drawing.Store.Add(record);
                else drawing.Store[index] = new DataRecord(drawing.Store[index].Name, record.Values);
            });
            public void Erase(string path) => pending.Add(() =>
                Assert.Equal(1, drawing.Store.RemoveAll(existing => string.Equals(existing.Name, path, StringComparison.OrdinalIgnoreCase))));
            public void Commit() { pending.ForEach(action => action()); pending.Clear(); drawing.Commits++; }
            public void Dispose() => pending.Clear();
        }
    }
}
