using System;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class XDataParserTests
{
    [Fact]
    public void ReadsRecognizedApplicationPropertiesAndNestedMaterialWithoutLosingForeignData()
    {
        var result = Parse(new[]
        {
            V(1001, "FOREIGN"), V(1000, "Name=Do not project"), V(1040, 1.25),
            V(1001, "ESMT_LEP_v1.0"), V(1000, "Тип=Опора 0,4 кВ"), V(1000, "Name=АО21"),
            V(1000, "Number=021"), V(1000, "Material"), V(1002, "{"),
            V(1000, "Category=Железобетонные элементы"), V(1000, "Name=СВ110-5"),
            V(1000, "Count=1,2"), V(1000, "IsInSpec=0"), V(1000, "Comment=a=b"), V(1002, "}"),
            V(1000, "Title=Опора"), V(1000, "ProjectReference=Проект 021")
        });
        Assert.Equal("АО21", result.Properties["Name"]);
        Assert.Equal("Опора 0,4 кВ", result.Properties["Type"]);
        Assert.Equal("021", result.Properties["Number"]);
        Assert.Equal("Опора", result.Properties["Title"]);
        Assert.Equal("Проект 021", result.Properties["ProjectReference"]);
        var material = Assert.Single(result.Materials);
        Assert.Equal("СВ110-5", material.Name);
        Assert.Equal("1,2", material.Count);
        Assert.False(material.IsInSpec);
        Assert.Equal("a=b", material.Comment);
        Assert.Equal("Железобетонные элементы", material.Category);
        var apps = result.Tree.Children.Single().Children;
        Assert.Equal(new[] { "FOREIGN", "ESMT_LEP_v1.0" }, apps.Select(node => node.Name));
        Assert.Contains(apps[0].Children, node => node.Value == "1.25" && node.TypeCode == 1040);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ProjectsFieldsFromBobrovXDataRegApp()
    {
        var result = Parse(new[]
        {
            V(1001, "UNKNOWN"), V(1000, "Name=Foreign"),
            V(1001, "BobrovXDATA"), V(1000, "Number=022")
        });
        Assert.Equal("022", result.Properties["Number"]);
        Assert.False(result.Properties.ContainsKey("Name"));
        Assert.Equal(new[] { "UNKNOWN", "BobrovXDATA" }, result.Tree.Children.Single().Children.Select(node => node.Name));
    }

    [Fact]
    public void ReadsChunkedXmlXRecordAndPreservesAllUnknownElementsAndTypedValues()
    {
        const string xml = "<VisualTreeString><Properties><Name>Кабель</Name><Custom>keep</Custom></Properties>" +
            "<Materials><Material Category='Линейная арматура' Name='CD35' Count='2' IsInSpec='true' Comment='Тест'/></Materials></VisualTreeString>";
        var result = new XDataParser().Parse(new EntityDataSnapshot("B2", "Polyline", Array.Empty<DataValue>(), new[]
        {
            new DataRecord("BobrovXDATA/Properties", new[] { V(1, xml.Substring(0, 80)), V(1, xml.Substring(80)) }),
            new DataRecord("Foreign", new[] { V(90, 42), V(310, new byte[] { 0, 255 }) })
        }));
        Assert.Equal("Кабель", result.Properties["Name"]);
        Assert.Equal("CD35", Assert.Single(result.Materials).Name);
        Assert.True(result.Materials[0].IsInSpec);
        Assert.Contains(Flatten(result.Tree), node => node.Name == "Custom" && node.Value == "keep");
        Assert.Contains(Flatten(result.Tree), node => node.TypeCode == 310 && node.Value == "00FF");
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MalformedListsRecoverAtNextApplicationAndDoNotLeakProperties()
    {
        var result = Parse(new[] { V(1001, "BobrovXDATA"), V(1002, "}"), V(1000, "Material"),
            V(1002, "{"), V(1000, "Name=unfinished"), V(1001, "ESMT_LEP_v1.0"), V(1000, "Name=valid") });
        Assert.Equal("valid", result.Properties["Name"]);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Equal(2, result.Tree.Children.Single().Children.Count);
    }

    [Theory]
    [InlineData("<VisualTreeString><Properties>")]
    [InlineData("<!DOCTYPE VisualTreeString [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><VisualTreeString>&x;</VisualTreeString>")]
    public void InvalidOrUnsafeXmlRemainsVisibleAndDoesNotStopOtherRecords(string xml)
    {
        var result = new XDataParser().Parse(new EntityDataSnapshot("A1", "Line", Array.Empty<DataValue>(), new[]
        {
            new DataRecord("broken", new[] { V(1, xml) }),
            new DataRecord("valid", new[] { V(1, "<VisualTreeString><Properties><Name>ok</Name></Properties></VisualTreeString>") })
        }));
        Assert.Equal("ok", result.Properties["Name"]);
        Assert.Single(result.Warnings);
        Assert.Contains(Flatten(result.Tree), node => node.Value == xml);
    }

    [Fact]
    public void ExcessiveNestingAndOversizedXmlAreBoundedAndReported()
    {
        var values = new[] { V(1001, "BobrovXDATA") }.Concat(Enumerable.Repeat(V(1002, "{"), 80));
        var result = new XDataParser().Parse(new EntityDataSnapshot("A1", "Line", values.ToArray(), new[]
        {
            new DataRecord("huge", new[] { V(1, "<VisualTreeString>" + new string('x', XDataParser.MaxXmlCharacters) + "</VisualTreeString>") })
        }));
        Assert.NotEmpty(result.Warnings);
        Assert.True(Flatten(result.Tree).Count() < 200);
    }

    [Fact]
    public void UnknownXmlDoesNotInventPropertyMappings()
    {
        var result = new XDataParser().Parse(new EntityDataSnapshot("A1", "Line", Array.Empty<DataValue>(), new[]
        {
            new DataRecord("foreign", new[] { V(1, "<Other><Properties><Name>foreign</Name></Properties></Other>") })
        }));
        Assert.Empty(result.Properties);
        Assert.Contains(Flatten(result.Tree), node => node.Name == "Name" && node.Value == "foreign");
    }

    private static ParsedEntityData Parse(DataValue[] values) =>
        new XDataParser().Parse(new EntityDataSnapshot("A1", "BlockReference", values, Array.Empty<DataRecord>()));
    private static DataValue V(int code, object value) => new DataValue(code, value);
    private static System.Collections.Generic.IEnumerable<XDataNode> Flatten(XDataNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child)) yield return descendant;
    }
}
