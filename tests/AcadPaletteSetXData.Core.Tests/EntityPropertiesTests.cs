using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class EntityPropertiesTests
{
    private const string SchemaJson = """
        {
          "format":"BobrovXDATA.schema", "version":1,
          "parameters":[
            {"id":"Label","type":"String","caption":"Марка"},
            {"id":"Name","type":"CalculatedString","caption":"Имя","format":"@@[tag]/@@[Label]/@@[\"КОД:1]\"]","persist":true},
            {"id":"Measure","type":"CalculatedString","caption":"Размер","format":"@@[geomLength] м / @@[geomSquare] м²"},
            {"id":"Total","type":"CalculatedString","caption":"Итого","format":"=@@[geomLength] * 2 + @@[geomSquare]"},
            {"id":"Both","type":"CalculatedString","caption":"Оба","format":"=@@[\"Label\"] & @@[Label]"},
            {"id":"Optional","type":"CalculatedString","caption":"Дополнительно","format":"=@@[\"Optional\"] & 'ok'"}
          ]
        }
        """;

    [Fact]
    public void GridGroupsNativePropertiesEvenWithoutSchemaOrXData()
    {
        var model = new PaletteViewModel();
        var grid = new PropertyGridViewModel(model.Selection, culture: CultureInfo.InvariantCulture);
        var block = Snapshot("1", "BlockReference", new EntityProperties(new Dictionary<string, string>
        {
            ["TAG"] = "ЩУ1",
            ["EMPTY"] = ""
        }));
        var polyline = Snapshot("2", "Polyline", new EntityProperties(length: 7.5, area: 2.25));
        var line = Snapshot("3", "Line", new EntityProperties(length: 7.5));

        model.Selection.Apply(new[] { block });
        var attributes = Assert.Single(grid.Roots);
        Assert.Equal("Атрибуты", attributes.Caption);
        Assert.Equal(new[] { ("TAG", "TAG", "ЩУ1"), ("EMPTY", "EMPTY", "") },
            attributes.Children.Select(row => (row.Id!, row.Caption, row.Value)));

        model.Selection.Apply(new[] { polyline });
        var geometry = Assert.Single(grid.Roots);
        Assert.Equal("Геометрия", geometry.Caption);
        Assert.Equal(new[] { ("geomLength", "Длина", "7.5"), ("geomSquare", "Площадь", "2.25") },
            geometry.Children.Select(row => (row.Id!, row.Caption, row.Value)));
        Assert.All(attributes.Children.Concat(geometry.Children), row =>
        {
            Assert.False(row.IsEditable);
            Assert.False(row.CanDelete);
        });

        model.Selection.Apply(new[] { polyline, line });
        Assert.False(grid.Rows.Single(row => row.Id == EntityProperties.LengthId).IsMixed);
        Assert.True(grid.Rows.Single(row => row.Id == EntityProperties.AreaId).IsMixed);
        model.Selection.Apply(new[] { block, line });
        Assert.All(grid.Rows.Where(row => row.Id != null), row => Assert.Equal(ObjectCalculation.Various, row.Value));

        model.Selection.Apply(new[] { Snapshot("4", "Point", new EntityProperties(length: double.PositiveInfinity, area: double.NaN)) });
        Assert.Empty(grid.Roots);
    }

    [Fact]
    public void CalculatedStringCombinesAttributesGeometryAndXData()
    {
        var schema = LoadSchema();
        Assert.Equal(SchemaIssueSeverity.Warning, Assert.Single(schema.Issues).Severity);
        var cache = new ObjectValuesCache(new SchemaCache(() => schema));
        var block = Snapshot("1", "BlockReference", new EntityProperties(new Dictionary<string, string>
        {
            ["TAG"] = "ЩУ1",
            ["КОД:1]"] = "код",
            ["LABEL"] = "атрибут:"
        }), "XDATA");
        var polyline = Snapshot("2", "Polyline", new EntityProperties(length: 7.5, area: 2.25));
        var blockCalculation = new ObjectCalculation(cache.Get(block), CultureInfo.InvariantCulture);
        var geometryCalculation = new ObjectCalculation(cache.Get(polyline), CultureInfo.InvariantCulture);

        Assert.Equal("ЩУ1/XDATA/код", blockCalculation.Text("Name"));
        Assert.Equal("атрибут:XDATA", blockCalculation.Text("Both"));
        Assert.Equal("ok", blockCalculation.Text("Optional"));
        Assert.Equal("7.5 м / 2.25 м²", geometryCalculation.Text("Measure"));
        Assert.Equal("17.25", geometryCalculation.Text("Total"));
        Assert.False(geometryCalculation.Find("Total")!.IsError);
        Assert.Equal("неизвестная ссылка @@[tag]", geometryCalculation.Find("Name")!.Error);

        Assert.Equal("=@@[\"Label\"] & @@[Other]", FormulaExpression.RenameReference("=@@[\"Label\"] & @@[Label]", "Label", "Other"));
    }

    [Fact]
    public void RefreshAndPersistUseTheCurrentNativeValuesWithUnchangedXData()
    {
        var cache = new ObjectValuesCache(new SchemaCache(LoadSchema));
        var model = new PaletteViewModel();
        var before = Snapshot("1", "BlockReference", new EntityProperties(new Dictionary<string, string> { ["TAG"] = "до" }, length: 2, area: 1), "X");
        var after = new EntityDataSnapshot(before.Handle, before.EntityType, before.XData, before.Records,
            entityProperties: new EntityProperties(new Dictionary<string, string> { ["TAG"] = "после" }, length: 4, area: 1));

        model.Selection.Apply(new[] { before }, cache: cache);
        var oldValues = cache.Get(before);
        Assert.Equal("до/X/", model.Selection.Calculations[0].Text("Name"));
        model.Selection.Apply(new[] { after }, cache: cache);
        Assert.Equal("после/X/", model.Selection.Calculations[0].Text("Name"));
        Assert.Equal("9", model.Selection.Calculations[0].Text("Total"));
        Assert.Equal("после", model.Grid.Rows.Single(row => row.Id == "TAG").Value);
        Assert.Equal(2, cache.ParseCount);

        // The writer's snapshot wins even if its XDATA is the very same buffer as the old cache entry.
        var patch = new XDataPatch(cache.Schema);
        var stored = patch.Apply(after, SelectionEdit.AddCalculated("Name"), oldValues);
        Assert.Same(after.EntityProperties, stored.EntityProperties);
        Assert.Equal("после/X/", XDataV2.Read(stored, cache.Schema).Find("Name")!.Value);
        Assert.DoesNotContain(XDataV2.Read(stored, cache.Schema).Values, value => value.Id == "TAG" || value.Id == "geomLength");
    }

    [Fact]
    public void ExplicitGeometryAndAttributeReferencesKeepSchemaIdsAvailable()
    {
        var read = ParameterSchemaJson.Load("""
            {
              "format":"BobrovXDATA.schema", "version":1,
              "parameters":[
                {"id":"geomLength","type":"Double","caption":"Длина в XDATA","default":3},
                {"id":"Result","type":"CalculatedString","caption":"Результат",
                 "format":"=@@[geomLength] & '/' & @@[geomLength:geom] & '/' & @@[\"geomLength\"]"}
              ]
            }
            """);
        Assert.Empty(read.Issues);
        var schema = new SchemaSnapshot(new SchemaNodReadResult(read.Schema, null, read.Issues));
        var entity = Snapshot("1", "Polyline", new EntityProperties(new Dictionary<string, string> { ["GEOMLENGTH"] = "тег" }, length: 5));
        var calculation = new ObjectCalculation(XDataV2.Read(entity, schema), CultureInfo.InvariantCulture);
        Assert.Equal("3/5/тег", calculation.Text("Result"));
        Assert.Equal("=@@[StoredLength] & @@[geomLength:geom]", FormulaExpression.RenameReference(
            "=@@[geomLength] & @@[geomLength:geom]", "geomLength", "StoredLength"));
    }

    private static SchemaNodReadResult LoadSchema()
    {
        var read = ParameterSchemaJson.Load(SchemaJson);
        Assert.False(read.HasErrors, string.Join("\n", read.Issues));
        return new SchemaNodReadResult(read.Schema, null, read.Issues);
    }

    private static EntityDataSnapshot Snapshot(string handle, string type, EntityProperties properties, string? label = null)
    {
        var xdata = label is null ? Array.Empty<DataValue>() : new[]
        {
            new DataValue(1001, DrawingNames.RegApp), new DataValue(1070, (short)2),
            new DataValue(1000, "Label"), new DataValue(1000, label)
        };
        return new EntityDataSnapshot(handle, type, xdata, Array.Empty<DataRecord>(), entityProperties: properties);
    }
}
