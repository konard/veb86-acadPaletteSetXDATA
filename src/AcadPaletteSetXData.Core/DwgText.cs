using System;
using System.Collections.Generic;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>String limits shared by XDATA, XRecord and NOD writers.</summary>
public static class DwgText
{
    /// <summary>Fragment size that stays below AutoCAD's 255-byte string chunks with a margin.</summary>
    public const int MaxFragmentBytes = 240;

    /// <summary>
    /// Splits text into fragments of at most <paramref name="maxBytes"/> UTF-8 bytes without breaking
    /// surrogate pairs. An empty string gives one empty fragment. With <paramref name="keepWhitespaceInside"/>
    /// a cut is moved back (up to 32 characters) so that no fragment starts or ends with whitespace,
    /// which some DXF tools trim.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxBytes = MaxFragmentBytes, bool keepWhitespaceInside = false)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (maxBytes < 4) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var fragments = new List<string>();
        var offset = 0;
        do
        {
            var end = offset; var bytes = 0;
            while (end < text.Length)
            {
                var width = Width(text, end);
                var size = Encoding.UTF8.GetByteCount(text.ToCharArray(end, width));
                if (bytes + size > maxBytes) break;
                bytes += size; end += width;
            }
            if (keepWhitespaceInside && end < text.Length)
            {
                for (var cut = end; cut > offset && end - cut <= 32; cut--)
                {
                    if (char.IsLowSurrogate(text[cut]) || char.IsWhiteSpace(text[cut]) || char.IsWhiteSpace(text[cut - 1])) continue;
                    end = cut;
                    break;
                }
            }
            fragments.Add(text.Substring(offset, end - offset));
            offset = end;
        } while (offset < text.Length);
        return fragments;
    }

    private static int Width(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
}
