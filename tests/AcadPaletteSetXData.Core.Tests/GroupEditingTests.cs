using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class GroupEditingTests
{
    [Fact]
    public void ComparesEveryHeaderAndMaterialFieldIncludingMissingRows()
    {
        var model = new PaletteViewModel();
        model.Selection.Apply(new[] { Sample("A", "One", "1", true), Sample("B", "Two", "2", false), Sample("C", "One") });
        Assert.Equal("Кабель", model.Properties.Type);
        Assert.False(model.Properties.IsTypeMixed);
        Assert.True(model.Properties.IsNameMixed);
        Assert.Equal("", model.Properties.Name);
        var row = Assert.Single(model.Properties.Materials);
        Assert.True(row.IsNameMixed);
        Assert.True(row.IsCountMixed);
        Assert.True(row.IsInSpecMixed);
        Assert.True(row.IsCommentMixed);
        Assert.Null(row.IsInSpec);
        model.Selection.SelectedEntity = model.Selection.Entities[1];
        Assert.True(model.Properties.IsNameMixed); // Raw-tree inspection does not change the edit target.
    }

    [Fact]
    public void ConfirmationWritesOnlyTheChangedFieldAndRefreshesAllObjects()
    {
        var source = new MemorySelection { Snapshots = new[] { Sample("A", "One", "1", true), Sample("B", "Two", "2", false) } };
        var model = new PaletteViewModel();
        using var controller = new SelectionController(source, model.Selection);
        Assert.True(model.Properties.CanEdit);
        model.Properties.CommitField("Name");
        Assert.Equal(0, source.Writes); // Merely visiting the mixed field is not a change.
        model.Properties.Name = "Unified";
        Assert.Equal(0, source.Writes);
        model.Properties.CommitField("Name");
        Assert.Equal(1, source.Writes);
        Assert.False(model.Properties.IsNameMixed);
        foreach (var snapshot in source.Snapshots) Assert.Equal("Unified", new XDataParser().Parse(snapshot).Properties["Name"]);
        Assert.Equal(new[] { "1", "2" }, source.Snapshots.Select(s => new XDataParser().Parse(s).Materials.Single().Count));
        Assert.Equal(new bool?[] { true, false }, source.Snapshots.Select(s => new XDataParser().Parse(s).Materials.Single().IsInSpec));
    }

    [Fact]
    public void MaterialChangeAddAndDeleteApplyToTheWholeSelection()
    {
        var source = new MemorySelection { Snapshots = new[] { Sample("A", "One", "1", true), Sample("B", "Two") } };
        var model = new PaletteViewModel();
        using var controller = new SelectionController(source, model.Selection);
        var row = Assert.Single(model.Properties.Materials);
        row.Count = "7";
        model.Properties.CommitMaterialField(row, "Count");
        Assert.All(source.Snapshots, s => Assert.Equal("7", new XDataParser().Parse(s).Materials.Single().Count));
        model.Properties.AddMaterialCommand.Execute(null);
        Assert.All(source.Snapshots, s => Assert.Equal(2, new XDataParser().Parse(s).Materials.Count));
        model.Properties.DeleteMaterialCommand.Execute(model.Properties.Materials.First(r => r.MaterialKey == "CD35"));
        Assert.All(source.Snapshots, s => Assert.Single(new XDataParser().Parse(s).Materials));
    }

    [Fact]
    public void FailedWriteRestoresValuesAndReportsErrorWithoutLosingSelection()
    {
        var source = new MemorySelection { Snapshots = new[] { Sample("A", "One") }, FailWrite = true };
        var model = new PaletteViewModel();
        using var controller = new SelectionController(source, model.Selection);
        model.Properties.Name = "Rejected";
        model.Properties.CommitField("Name");
        Assert.Equal("One", model.Properties.Name);
        Assert.Contains("write failed", model.Selection.Status);
        Assert.True(model.Selection.HasSelection);
        controller.Dispose();
        Assert.False(model.Properties.CanEdit);
    }

    [Fact]
    public void PatchPreservesUnknownDataAndUpdatesXmlAliasesAndDuplicateHeaderSources()
    {
        var original = new EntityDataSnapshot("A", "Line", new[]
        {
            V(1001, "OTHER"), V(1000, "Name=untouched"), V(1070, (short)42),
            V(1001, "BobrovXDATA"), V(1000, "Название=old"), V(1000, "Number=unique")
        }, new[] { new DataRecord("Nested/BobrovXDATA", new[] { V(1, "<VisualTreeString><Properties Name='override' Custom='keep'><Название>override2</Название></Properties><Unknown flag='yes'/><Materials><Material Name='CD35' Count='1' Extra='keep'/></Materials></VisualTreeString>") }) });
        var changed = new XDataPatch().Apply(original, SelectionEdit.Header("Name", "new=value"));
        Assert.Equal("new=value", new XDataParser().Parse(changed).Properties["Name"]);
        Assert.Equal("unique", new XDataParser().Parse(changed).Properties["Number"]);
        Assert.Equal("Name=untouched", changed.XData[1].Value);
        Assert.Equal((short)42, changed.XData[2].Value);
        Assert.Contains("Custom=\"keep\"", string.Concat(changed.Records[0].Values.Select(v => (string)v.Value)));
        Assert.Contains("Unknown", string.Concat(changed.Records[0].Values.Select(v => (string)v.Value)));
        changed = new XDataPatch().Apply(changed, SelectionEdit.MaterialField("CD35", "Count", "3"));
        Assert.Contains("Extra=\"keep\"", string.Concat(changed.Records[0].Values.Select(v => (string)v.Value)));
        Assert.Equal("3", new XDataParser().Parse(changed).Materials.Single().Count);
    }

    [Fact]
    public void LiteralMixedTextIsDataAndEmptyStringCanReplaceAMixedValue()
    {
        var source = new MemorySelection { Snapshots = new[] { Sample("A", "Разное"), Sample("B", "Two") } };
        var model = new PaletteViewModel();
        using var controller = new SelectionController(source, model.Selection);
        model.Properties.Name = "";
        model.Properties.CommitField("Name");
        Assert.False(model.Properties.IsNameMixed);
        Assert.All(source.Snapshots, s => Assert.Equal("", new XDataParser().Parse(s).Properties["Name"]));
        model.Properties.Name = "Разное";
        model.Properties.CommitField("Name");
        Assert.Equal("Разное", model.Properties.Name);
        Assert.False(model.Properties.IsNameMixed);
    }

    [Fact]
    public void IdenticalValuesAndSingleObjectAreNotMixedButCaseDifferencesAre()
    {
        var model = new PaletteViewModel();
        model.Selection.Apply(new[] { Sample("A", "Same", "1", true), Sample("B", "Same", "1", true) });
        Assert.False(model.Properties.IsNameMixed);
        Assert.Equal("Same", model.Properties.Name);
        Assert.False(Assert.Single(model.Properties.Materials).IsMixedValue);
        model.Selection.Apply(new[] { Sample("A", "Same"), Sample("B", "same") });
        Assert.True(model.Properties.IsNameMixed);
        model.Selection.Apply(new[] { Sample("A", "Разное", "1", true) });
        Assert.False(model.Properties.IsNameMixed);
        Assert.Equal("Разное", model.Properties.Name);
    }

    [Fact]
    public void MissingHeaderAndDuplicateMaterialMultiplicityAreMixed()
    {
        var one = Sample("A", "", "1");
        var two = new EntityDataSnapshot("B", "Line", one.XData.Where(v => !Equals(v.Value, "Name=")).ToArray(), Array.Empty<DataRecord>());
        var model = new PaletteViewModel();
        model.Selection.Apply(new[] { one, two });
        Assert.True(model.Properties.IsNameMixed);
        var duplicate = new EntityDataSnapshot("B", "Line", one.XData.Concat(one.XData.Skip(3)).ToArray(), Array.Empty<DataRecord>());
        model.Selection.Apply(new[] { one, duplicate });
        Assert.True(Assert.Single(model.Properties.Materials).IsNameMixed);
        var deleted = new XDataPatch().Apply(duplicate, SelectionEdit.Delete("CD35"));
        Assert.Empty(new XDataParser().Parse(deleted).Materials);
    }

    [Fact]
    public void CheckboxCommitUsesExplicitNullableStateAndRetainsOtherMixedValues()
    {
        var source = new MemorySelection { Snapshots = new[] { Sample("A", "One", "1", true), Sample("B", "Two", "2", false) } };
        var model = new PaletteViewModel();
        using var controller = new SelectionController(source, model.Selection);
        var row = Assert.Single(model.Properties.Materials);
        Assert.True(row.IsInSpecMixed);
        row.IsInSpec = null;
        model.Properties.CommitMaterialField(row, "IsInSpec");
        Assert.Null(Assert.Single(model.Properties.Materials).IsInSpec);
        Assert.False(model.Properties.Materials[0].IsInSpecMixed);
        Assert.True(model.Properties.Materials[0].IsCountMixed);
        Assert.True(model.Properties.IsNameMixed);
    }

    [Fact]
    public void AddsStorageForDataFreeEntitiesAndPreservesUnrelatedXrecordBuffers()
    {
        var record = new DataRecord("Opaque", new[] { V(90, 42) });
        var snapshot = new EntityDataSnapshot("A", "Line", Array.Empty<DataValue>(), new[] { record });
        var changed = new XDataPatch().Apply(snapshot, SelectionEdit.Header("ProjectReference", "a=b"));
        Assert.Equal("BobrovXDATA", changed.XData[0].Value);
        Assert.Equal("a=b", new XDataParser().Parse(changed).Properties["ProjectReference"]);
        Assert.Same(record, changed.Records[0]);
    }

    [Fact]
    public void InvalidFormatsAndDtdRejectPatchesWithoutChangingInputs()
    {
        var bad = new EntityDataSnapshot("A", "Line", new[] { V(1001, "BobrovXDATA"), V(1002, "{") }, Array.Empty<DataRecord>());
        Assert.Throws<InvalidOperationException>(() => new XDataPatch().Apply(bad, SelectionEdit.Header("Name", "new")));
        Assert.Equal(2, bad.XData.Count);
        var xml = new EntityDataSnapshot("A", "Line", Array.Empty<DataValue>(), new[] { new DataRecord("XML", new[] { V(1, "<!DOCTYPE x><VisualTreeString/>") }) });
        Assert.Throws<InvalidOperationException>(() => new XDataPatch().Apply(xml, SelectionEdit.Header("Name", "new")));
    }

    [Fact]
    public void PendingCategoryEditsKeepTheirGroupUntilConfirmation()
    {
        var source = new MemorySelection { Snapshots = new[] { Sample("A", "One", "1"), Sample("B", "Two", "2") } };
        var model = new PaletteViewModel();
        using var controller = new SelectionController(source, model.Selection);
        var row = Assert.Single(model.Properties.Materials);
        row.IsEditing = true;
        row.Category = "Новая категория";
        Assert.Equal("Арматура", row.GroupCategory);
        Assert.Equal(0, source.Writes);
        model.Properties.CommitMaterialField(row, "Category");
        Assert.Equal("Новая категория", Assert.Single(model.Properties.Materials).GroupCategory);
        Assert.True(model.Properties.Materials[0].IsEditing);
    }

    [Fact]
    public void MaterialAliasesUseTheLastOccurrenceSoTheWriterTargetsTheDisplayedBrand()
    {
        var snapshot = new EntityDataSnapshot("A", "Line", new[]
        {
            V(1001, "BobrovXDATA"), V(1000, "Material"), V(1002, "{"),
            V(1000, "Name=A"), V(1000, "Марка=B"), V(1000, "Name=C"), V(1000, "Count=1"), V(1002, "}")
        }, Array.Empty<DataRecord>());
        Assert.Equal("C", new XDataParser().Parse(snapshot).Materials.Single().Name);
        var changed = new XDataPatch().Apply(snapshot, SelectionEdit.MaterialField("C", "Count", "9"));
        Assert.Equal("9", new XDataParser().Parse(changed).Materials.Single().Count);
        var xml = new EntityDataSnapshot("B", "Line", Array.Empty<DataValue>(), new[]
        {
            new DataRecord("XML", new[] { V(1, "<VisualTreeString><Materials><Material Name='A' Марка='B' Count='1'><Name>C</Name></Material></Materials></VisualTreeString>") })
        });
        Assert.Equal("C", new XDataParser().Parse(xml).Materials.Single().Name);
        changed = new XDataPatch().Apply(xml, SelectionEdit.MaterialField("C", "Count", "9"));
        Assert.Equal("9", new XDataParser().Parse(changed).Materials.Single().Count);
    }

    [Fact]
    public void XmlChunksPreserveUnicodePairsAndStayWithinByteLimits()
    {
        var snapshot = new EntityDataSnapshot("A", "Line", Array.Empty<DataValue>(), new[]
        {
            new DataRecord("XML", new[] { V(1000, "<VisualTreeString><Properties><Name>old</Name></Properties></VisualTreeString>") })
        });
        var name = string.Concat(Enumerable.Repeat("Ж🙂", 80));
        var changed = new XDataPatch().Apply(snapshot, SelectionEdit.Header("Name", name));
        Assert.Equal(name, new XDataParser().Parse(changed).Properties["Name"]);
        Assert.All(changed.Records[0].Values, value =>
        {
            var text = (string)value.Value;
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 240);
            Assert.False(char.IsLowSurrogate(text[0]));
            Assert.False(char.IsHighSurrogate(text[text.Length - 1]));
            Assert.Equal(1000, value.TypeCode);
        });
    }

    private static DataValue V(int code, object value) => new DataValue(code, value);
    private static EntityDataSnapshot Sample(string handle, string name, string? count = null, bool flag = true)
    {
        var data = new List<DataValue> { V(1001, "BobrovXDATA"), V(1000, "Type=Кабель"), V(1000, "Name=" + name) };
        if (count != null) data.AddRange(new[] { V(1000, "Material"), V(1002, "{"), V(1000, "Category=Арматура"), V(1000, "Name=CD35"), V(1000, "Count=" + count), V(1000, "IsInSpec=" + flag), V(1000, "Comment=shared"), V(1002, "}") });
        return new EntityDataSnapshot(handle, "Line", data, Array.Empty<DataRecord>());
    }

    private sealed class MemorySelection : ISelectionSource, ISelectionWriter
    {
        public EntityDataSnapshot[] Snapshots { get; set; } = Array.Empty<EntityDataSnapshot>();
        public int Writes { get; private set; }
        public bool FailWrite { get; set; }
        public event EventHandler? SelectionChanged { add { } remove { } }
        public IReadOnlyList<EntityDataSnapshot> ReadSelection() => Snapshots;
        public void WriteSelection(IReadOnlyList<string> handles, SelectionEdit edit)
        {
            if (FailWrite) throw new InvalidOperationException("write failed");
            Assert.Equal(Snapshots.Select(s => s.Handle), handles);
            Snapshots = Snapshots.Select(s => new XDataPatch().Apply(s, edit)).ToArray();
            Writes++;
        }
    }
}
