using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class XDataWriterTests
{
    [Fact]
    public void UnchangedXmlFieldKeepsOriginalBufferAndFormatting()
    {
        var before = Xml("B");
        var after = new XDataPatch().Apply(before, SelectionEdit.Header("Type", "Cable"));
        Assert.Same(before.Records[0], after.Records[0]);
    }

    [Fact]
    public void SharedFieldEditPreservesEveryUniqueValueAcrossXdataAndXml()
    {
        var database = new MemoryDatabase(Xdata("A"), Xml("B"));
        var original = database.Snapshots;
        var writer = new XDataWriter(database.Begin);
        writer.WriteSelection(new[] { "A", "B" }, SelectionEdit.Header("Type", "Device"));
        Assert.Equal(1, database.Commits);
        Assert.Equal(2, database.Writes);
        for (var index = 0; index < original.Length; index++)
        {
            var before = new XDataParser().Parse(original[index]);
            var after = new XDataParser().Parse(database.Snapshots[index]);
            Assert.Equal("Device", after.Properties["Type"]);
            Assert.Equal(before.Properties["Name"], after.Properties["Name"]);
            Assert.Equal(before.Properties["Number"], after.Properties["Number"]);
            Assert.Equal(before.Materials.Single().Count, after.Materials.Single().Count);
            Assert.Equal(before.Materials.Single().IsInSpec, after.Materials.Single().IsInSpec);
            Assert.Equal(before.Materials.Single().Comment, after.Materials.Single().Comment);
            Assert.Same(original[index].Records.Last(), database.Snapshots[index].Records.Last());
        }
        Assert.Same(original[0].XData[1], database.Snapshots[0].XData[1]);
        Assert.Contains("Custom=\"keep\"", XmlText(database.Snapshots[1]));
        Assert.Contains("<Unknown", XmlText(database.Snapshots[1]));
        Assert.Contains("Extra=\"keep\"", XmlText(database.Snapshots[1]));
        Assert.True(database.Disposed);
    }

    [Fact]
    public void InvalidSecondObjectIsRejectedBeforeWritingAnyObject()
    {
        var bad = new EntityDataSnapshot("B", "Line", new[] { V(1001, "BobrovXDATA"), V(1002, "{") }, Array.Empty<DataRecord>());
        var database = new MemoryDatabase(Xdata("A"), bad);
        var original = database.Snapshots;
        Assert.Throws<InvalidOperationException>(() => new XDataWriter(database.Begin)
            .WriteSelection(new[] { "A", "B" }, SelectionEdit.Header("Name", "Changed")));
        Assert.Same(original, database.Snapshots);
        Assert.Equal(0, database.Writes);
        Assert.Equal(0, database.Commits);
        Assert.True(database.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteOrCommitFailureRollsBackTheWholeGroup(bool failCommit)
    {
        var database = new MemoryDatabase(Xdata("A"), Xml("B")) { FailWrite = !failCommit, FailCommit = failCommit };
        var original = database.Snapshots;
        Assert.Throws<InvalidOperationException>(() => new XDataWriter(database.Begin)
            .WriteSelection(new[] { "A", "B" }, SelectionEdit.Header("Name", "Changed")));
        Assert.Same(original, database.Snapshots);
        Assert.Equal(0, database.Commits);
        Assert.True(database.Disposed);
    }

    [Fact]
    public void UsesFreshValuesAndDoesNotCreateWritesForNoOps()
    {
        var database = new MemoryDatabase(Xdata("A"), Xml("B"));
        var writer = new XDataWriter(database.Begin);
        writer.WriteSelection(new[] { "A", "B" }, SelectionEdit.Header("Type", "Cable"));
        writer.WriteSelection(new[] { "A", "B" }, SelectionEdit.Delete("Missing"));
        Assert.Equal(0, database.Writes);
        Assert.Equal(0, database.Commits);
        database.Snapshots = new[] { new XDataPatch().Apply(Xdata("A"), SelectionEdit.Header("Number", "externally changed")), Xml("B") };
        writer.WriteSelection(new[] { "A", "B" }, SelectionEdit.Header("Name", "New"));
        Assert.Equal("externally changed", new XDataParser().Parse(database.Snapshots[0]).Properties["Number"]);
    }

    [Theory]
    [InlineData(250, true)]
    [InlineData(251, false)]
    public void XdataStringLimitIncludesKeyAndIsCheckedBeforeWriting(int length, bool valid)
    {
        var database = new MemoryDatabase(Xdata("A"));
        var edit = SelectionEdit.Header("Name", new string('a', length)); // Name= is five UTF-8 bytes.
        var writer = new XDataWriter(database.Begin);
        if (valid) writer.WriteSelection(new[] { "A" }, edit);
        else Assert.Throws<InvalidOperationException>(() => writer.WriteSelection(new[] { "A" }, edit));
        Assert.Equal(valid ? 1 : 0, database.Commits);
        Assert.Equal(valid ? 1 : 0, database.Writes);
        Assert.True(database.Disposed);
    }

    [Fact]
    public void XdataByteLimitRejectsMultibyteValuesButLongXmlRemainsWritable()
    {
        var name = new string('Ж', 126);
        var xdata = new MemoryDatabase(Xdata("A"));
        Assert.Throws<InvalidOperationException>(() => new XDataWriter(xdata.Begin)
            .WriteSelection(new[] { "A" }, SelectionEdit.Header("Name", name)));
        Assert.Equal(0, xdata.Writes);
        var xml = new MemoryDatabase(Xml("B"));
        new XDataWriter(xml.Begin).WriteSelection(new[] { "B" }, SelectionEdit.Header("Name", name));
        Assert.Equal(name, new XDataParser().Parse(xml.Snapshots[0]).Properties["Name"]);
        Assert.All(xml.Snapshots[0].Records[0].Values, v => Assert.InRange(Encoding.UTF8.GetByteCount((string)v.Value), 1, 240));
    }

    [Theory]
    [InlineData("A", "C")]
    [InlineData("A", "A")]
    public void RejectsChangedOrDuplicateTargetsBeforeWriting(string first, string second)
    {
        var database = new MemoryDatabase(Xdata("A"), Xml("B"));
        Assert.Throws<InvalidOperationException>(() => new XDataWriter(database.Begin)
            .WriteSelection(new[] { first, second }, SelectionEdit.Header("Name", "Changed")));
        Assert.Equal(0, database.Writes);
        Assert.Equal(0, database.Commits);
    }

    private static DataValue V(int code, object value) => new DataValue(code, value);
    private static DataRecord Opaque() => new DataRecord("Unrelated", new[] { V(90, 42), V(310, new byte[] { 1, 2, 3 }) });
    private static EntityDataSnapshot Xdata(string handle) => new EntityDataSnapshot(handle, "Line", new[]
    {
        V(1001, "OTHER"), V(1004, new byte[] { 7, 8 }), V(1000, "Name=untouched"),
        V(1001, "BobrovXDATA"), V(1000, "Type=Cable"), V(1000, "Name=First"), V(1000, "Number=021"),
        V(1000, "Material"), V(1002, "{"), V(1000, "Name=CD35"), V(1000, "Count=1"), V(1000, "IsInSpec=true"),
        V(1000, "Comment=First comment"), V(1002, "}")
    }, new[] { Opaque() });
    private static EntityDataSnapshot Xml(string handle) => new EntityDataSnapshot(handle, "Polyline", Array.Empty<DataValue>(), new[]
    {
        new DataRecord("Nested/BobrovXDATA", new[] { V(1, "<VisualTreeString>\n <Properties Type='Cable' Number='022' Name='Second' Custom='keep'/>\n <Materials><Material Name='CD35' Count='2' IsInSpec='false' Comment='Second comment' Extra='keep'/></Materials><Unknown flag='yes'/></VisualTreeString>") }),
        Opaque()
    });
    private static string XmlText(EntityDataSnapshot snapshot) => string.Concat(snapshot.Records[0].Values.Select(v => (string)v.Value));

    // A transaction double models staging and failures; native Undo/Redo is tested in AutoCAD separately.
    private sealed class MemoryDatabase
    {
        public MemoryDatabase(params EntityDataSnapshot[] snapshots) => Snapshots = snapshots;
        public EntityDataSnapshot[] Snapshots { get; set; }
        public int Writes { get; set; }
        public int Commits { get; set; }
        public bool Disposed { get; set; }
        public bool FailWrite { get; set; }
        public bool FailCommit { get; set; }
        public IXDataWriteTransaction Begin(IReadOnlyList<string> handles) => new Transaction(this);

        private sealed class Transaction : IXDataWriteTransaction
        {
            private readonly MemoryDatabase database;
            private readonly EntityDataSnapshot[] staged;
            public Transaction(MemoryDatabase database) { this.database = database; Snapshots = database.Snapshots; staged = database.Snapshots.ToArray(); }
            public IReadOnlyList<EntityDataSnapshot> Snapshots { get; }
            public void Write(EntityDataSnapshot snapshot)
            {
                database.Writes++;
                if (database.FailWrite && database.Writes == 2) throw new InvalidOperationException("write failed");
                staged[Array.FindIndex(staged, s => s.Handle == snapshot.Handle)] = snapshot;
            }
            public void Commit()
            {
                if (database.FailCommit) throw new InvalidOperationException("commit failed");
                database.Snapshots = staged;
                database.Commits++;
            }
            public void Dispose() => database.Disposed = true;
        }
    }
}
