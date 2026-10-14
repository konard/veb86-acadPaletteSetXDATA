using System.Linq;
using System.Text;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class DwgTextTests
{
    [Fact]
    public void EmptyTextIsOneEmptyFragment() => Assert.Equal(new[] { "" }, DwgText.Split(""));

    [Fact]
    public void FragmentsRespectTheByteLimitAndSurrogatePairs()
    {
        var text = "a" + string.Concat(Enumerable.Repeat("😀Ж", 200));
        var fragments = DwgText.Split(text);
        Assert.Equal(text, string.Concat(fragments));
        Assert.All(fragments, fragment =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(fragment), 1, DwgText.MaxFragmentBytes);
            Assert.False(char.IsLowSurrogate(fragment[0]));
            Assert.False(char.IsHighSurrogate(fragment[fragment.Length - 1]));
        });
    }

    [Fact]
    public void CutsAvoidWhitespaceAtFragmentEndsWhenAsked()
    {
        var text = string.Concat(Enumerable.Repeat("слово ", 100)).Trim();
        var plain = DwgText.Split(text, 50);
        Assert.Contains(plain, fragment => char.IsWhiteSpace(fragment[0]) || char.IsWhiteSpace(fragment[fragment.Length - 1]));
        var kept = DwgText.Split(text, 50, keepWhitespaceInside: true);
        Assert.Equal(text, string.Concat(kept));
        Assert.All(kept, fragment => Assert.False(char.IsWhiteSpace(fragment[0]) || char.IsWhiteSpace(fragment[fragment.Length - 1])));

        // Without a suitable cut nearby the fragment stays full.
        var spaces = new string(' ', 100);
        Assert.Equal(spaces, string.Concat(DwgText.Split(spaces, 10, keepWhitespaceInside: true)));
    }

    [Fact]
    public void CompactJsonIsOneLineAndHidesDxfSequences()
    {
        var value = JsonValue.Parse("{ \"a\": [1, 2.50, null, true], \"b\": \"C:\\\\U+0041^J\\n\\u0001\\ud800\", \"\\\\\": {} }");
        var compact = value.ToCompactJson();
        Assert.Equal("{\"a\":[1,2.50,null,true],\"b\":\"C:\\u005cU+0041\\u005eJ\\n\\u0001\\ud800\",\"\\u005c\":{}}", compact);
        Assert.True(JsonValue.DeepEquals(value, JsonValue.Parse(compact)));
    }
}
