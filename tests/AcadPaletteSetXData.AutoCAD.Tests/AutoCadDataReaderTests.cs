using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Xunit;

namespace AcadPaletteSetXData.AutoCAD.Tests;

public sealed class AutoCadDataReaderTests
{
    [Fact]
    public void ReadsInstanceAttributesAndConstantDefinitionsAlongsideXData()
    {
        var transaction = new Transaction();
        var definition = new ObjectId(10);
        var tag = new ObjectId(11);
        var multiline = new ObjectId(12);
        var constant = new ObjectId(13, "ATTDEF");
        var variable = new ObjectId(14, "ATTDEF");
        var erased = new ObjectId(15, IsErased: true);
        var missing = new ObjectId(16);
        var text = new MText { Text = "первая\nвторая" };
        transaction.Objects[definition] = new BlockTableRecord { constant, variable };
        transaction.Objects[tag] = new AttributeReference { Tag = "MARK", TextString = "ЩУ1" };
        transaction.Objects[multiline] = new AttributeReference { Tag = "MULTI", MTextAttribute = text };
        transaction.Objects[constant] = new AttributeDefinition { Tag = "SERIES", TextString = "ЩУ", Constant = true };
        transaction.Objects[variable] = new AttributeDefinition { Tag = "IGNORED", TextString = "значение из определения" };
        var block = new BlockReference
        {
            Handle = 1,
            BlockTableRecord = definition,
            XData = new ResultBuffer { new TypedValue(1001, "BobrovXDATA") }
        };
        block.AttributeCollection.AddRange(new[] { tag, erased, missing, multiline });

        var result = new AutoCadDataReader(transaction).ReadEntity(block);

        Assert.Equal(new Dictionary<string, string> { ["MARK"] = "ЩУ1", ["MULTI"] = text.Text, ["SERIES"] = "ЩУ" },
            result.EntityProperties.Attributes);
        Assert.Equal("BobrovXDATA", Assert.Single(result.XData).Value);
        Assert.Contains("Атрибут [16]", result.Warning);
        Assert.True(text.Disposed);
    }

    [Fact]
    public void ReadsEachSupportedMeasurementIndependently()
    {
        var reader = new AutoCadDataReader(new Transaction());
        var line = reader.ReadEntity(new Line { Length = 5, MeasuredArea = 0 }).EntityProperties;
        Assert.Equal(5, line.Length);
        Assert.Null(line.Area);
        var areaOnly = reader.ReadEntity(new Hatch { Area = 2 }).EntityProperties;
        Assert.Null(areaOnly.Length);
        Assert.Equal(2, areaOnly.Area);
        Assert.Equal(8, reader.ReadEntity(new Region { Area = 4, Perimeter = 8 }).EntityProperties.Length);
        var curve = reader.ReadEntity(new Curve { MeasuredLength = 7.5, MeasuredArea = 2.5 }).EntityProperties;
        Assert.Equal(7.5, curve.Length);
        Assert.Equal(2.5, curve.Area);
        Assert.Equal(1, reader.ReadEntity(new Curve { MeasuredLength = 1 }).EntityProperties.Length);
        Assert.Null(reader.ReadEntity(new Ray { MeasuredLength = 5 }).EntityProperties.Length);
    }
}
