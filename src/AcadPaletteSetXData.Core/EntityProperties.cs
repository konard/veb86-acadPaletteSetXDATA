using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>Read-only values from the drawing entity, detached from the AutoCAD transaction.</summary>
public sealed class EntityProperties
{
    public const string LengthId = "geomLength";
    public const string AreaId = "geomSquare";
    public static EntityProperties Empty { get; } = new EntityProperties();

    public EntityProperties(IEnumerable<KeyValuePair<string, string>>? attributes = null, double? length = null, double? area = null)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attributes != null)
            foreach (var pair in attributes)
                if (!string.IsNullOrEmpty(pair.Key) && !tags.ContainsKey(pair.Key)) tags.Add(pair.Key, pair.Value);
        Attributes = new ReadOnlyDictionary<string, string>(tags);
        Length = ValidMeasurement(length);
        Area = ValidMeasurement(area);
    }

    public IReadOnlyDictionary<string, string> Attributes { get; }
    public double? Length { get; }
    public double? Area { get; }

    internal static bool IsGeometryId(string id) => ParameterSchema.SameId(id, LengthId) || ParameterSchema.SameId(id, AreaId);

    internal double? Geometry(string id) =>
        ParameterSchema.SameId(id, LengthId) ? Length : ParameterSchema.SameId(id, AreaId) ? Area : null;

    internal bool SameAs(EntityProperties other) => ReferenceEquals(this, other) ||
        Length == other.Length && Area == other.Area && Attributes.SequenceEqual(other.Attributes);

    internal static string Format(object? value, CultureInfo culture) =>
        value is double number ? number.ToString("R", culture) : value as string ?? "";

    private static double? ValidMeasurement(double? value) =>
        value is double number && number >= 0 && !double.IsInfinity(number) ? number : (double?)null;
}
