using System;
using System.Globalization;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class JsonTests
{
    [Fact]
    public void ParsesAllValueKindsAndKeepsPropertyOrder()
    {
        var value = (JsonObject)JsonValue.Parse("{ \"z\": \"текст\", \"a\": [1, -2.50, 1e3, true, false, null], \"m\": {} }");
        Assert.Equal(new[] { "z", "a", "m" }, value.Select(property => property.Key));
        Assert.Equal("текст", ((JsonString)value["z"]!).Value);
        var array = (JsonArray)value["a"]!;
        Assert.Equal(new[] { "1", "-2.50", "1e3" }, array.Take(3).Select(item => ((JsonNumber)item).Text));
        Assert.Same(JsonBoolean.True, array[3]);
        Assert.Same(JsonBoolean.False, array[4]);
        Assert.Same(JsonNull.Instance, array[5]);
        Assert.Empty((JsonObject)value["m"]!);
        Assert.Null(value["missing"]);
    }

    [Fact]
    public void NumberKeepsItsTokenAndComparesByValue()
    {
        var number = (JsonNumber)JsonValue.Parse("2.50");
        Assert.Equal("2.50", number.ToJson());
        Assert.Equal(2.5, number.ToDouble());
        Assert.True(JsonValue.DeepEquals(number, new JsonNumber(2.5)));
        Assert.True(new JsonNumber("3.0").IsInteger);
        Assert.True(new JsonNumber("3.0").TryGetInt32(out var three));
        Assert.Equal(3, three);
        Assert.False(new JsonNumber("3.5").TryGetInt32(out _));
        Assert.False(new JsonNumber("2147483648").TryGetInt32(out _));
        Assert.Throws<FormatException>(() => new JsonNumber("01"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JsonNumber(double.NaN));
    }

    [Fact]
    public void NumbersUseInvariantCultureUnderRussianLocale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal("0.5", new JsonNumber(0.5).Text);
            Assert.Equal(1234.5, ((JsonNumber)JsonValue.Parse("1234.5")).ToDouble());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void StringsAreEscapedAndCyrillicIsWrittenAsIs()
    {
        var text = "Кавычка \" слэш \\ строка\nтаб\t\u0001 😀 " + '\uD800';
        var json = new JsonString(text).ToJson();
        Assert.Equal("\"Кавычка \\\" слэш \\\\ строка\\nтаб\\t\\u0001 😀 \\ud800\"", json);
        Assert.Equal(text, ((JsonString)JsonValue.Parse(json)).Value);
        Assert.Equal("\u00e9/", ((JsonString)JsonValue.Parse("\"\\u00E9\\/\"")).Value);
    }

    [Fact]
    public void WriterPutsShortScalarContainersOnOneLine()
    {
        var value = JsonValue.Parse("{\"a\":[1,2],\"b\":{\"c\":\"d\"},\"e\":[{\"f\":1}],\"g\":[],\"h\":{}}");
        Assert.Equal(
            "{\n  \"a\": [ 1, 2 ],\n  \"b\": { \"c\": \"d\" },\n  \"e\": [\n    { \"f\": 1 }\n  ],\n  \"g\": [],\n  \"h\": {}\n}",
            value.ToJson());
        var longArray = new JsonArray(Enumerable.Range(0, 40).Select(i => (JsonValue)new JsonString("item" + i)));
        Assert.StartsWith("[\n  \"item0\",\n", longArray.ToJson());
    }

    [Fact]
    public void LeadingByteOrderMarkIsIgnored()
    {
        Assert.Equal("ok", ((JsonString)JsonValue.Parse("\uFEFF\"ok\"")).Value);
    }

    [Theory]
    [InlineData("{\n  \"a\": 1,\n}", 3, 1)]
    [InlineData("{\n  \"a\": 1\n  \"b\": 2\n}", 3, 3)]
    [InlineData("[1, 2", 1, 6)]
    public void SyntaxErrorReportsLineAndColumn(string json, int line, int column)
    {
        var error = Assert.Throws<JsonException>(() => JsonValue.Parse(json));
        Assert.Equal(line, error.Line);
        Assert.Equal(column, error.Column);
        Assert.Contains("строка " + line, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{} {}")]
    [InlineData("{'a': 1}")]
    [InlineData("{\"a\": 01}")]
    [InlineData("{\"a\": NaN}")]
    [InlineData("{\"a\": .5}")]
    [InlineData("[1,]")]
    [InlineData("// comment\n{}")]
    [InlineData("\"tab\there\"")]
    [InlineData("\"\\x\"")]
    [InlineData("\"\\u12\"")]
    [InlineData("tru")]
    public void RejectsInvalidJson(string json)
    {
        Assert.Throws<JsonException>(() => JsonValue.Parse(json));
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        var error = Assert.Throws<JsonException>(() => JsonValue.Parse("{\"a\": 1, \"a\": 2}"));
        Assert.Contains("Повторяющееся поле", error.Message);
    }

    [Fact]
    public void LimitsNestingDepth()
    {
        Assert.NotNull(JsonValue.Parse(new string('[', 32) + new string(']', 32)));
        Assert.Throws<JsonException>(() => JsonValue.Parse(new string('[', 1000) + new string(']', 1000)));
    }

    [Fact]
    public void ObjectRejectsDuplicateNamesAndDeepEqualsIgnoresPropertyOrder()
    {
        var obj = new JsonObject();
        obj.Add("a", new JsonNumber(1));
        Assert.Throws<ArgumentException>(() => obj.Add("a", new JsonNumber(2)));
        Assert.True(JsonValue.DeepEquals(JsonValue.Parse("{\"a\":1,\"b\":[true]}"), JsonValue.Parse("{\"b\":[true],\"a\":1.0}")));
        Assert.False(JsonValue.DeepEquals(JsonValue.Parse("[1,2]"), JsonValue.Parse("[2,1]")));
        Assert.False(JsonValue.DeepEquals(JsonValue.Parse("{\"a\":1}"), JsonValue.Parse("{\"a\":\"1\"}")));
    }
}
