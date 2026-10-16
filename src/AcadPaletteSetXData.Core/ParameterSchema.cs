using System;
using System.Collections.Generic;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Schema of BobrovXDATA parameters (TZXDATA section 4): parameters, enums and templates.
/// Every field is nullable so that absent and invalid JSON input round-trips exactly;
/// fields this version does not understand are kept in <see cref="SchemaElement.Extra"/>.
/// </summary>
public sealed class ParameterSchema : SchemaElement
{
    public const string FormatName = "BobrovXDATA.schema";
    public const int CurrentVersion = 1;
    public const string DefaultGroup = "Общие";
    /// <summary>System keys of XDATA v2 (TZXDATA 6.1) that a parameter cannot use.</summary>
    public static readonly IReadOnlyList<string> ReservedIds = new[] { "DEVICE_ID", "TEMPLATE", "FMT" };

    public string? Format { get; set; } = FormatName;
    public int? Version { get; set; } = CurrentVersion;
    public List<SchemaParameter> Parameters { get; } = new List<SchemaParameter>();
    public List<SchemaEnum> Enums { get; } = new List<SchemaEnum>();
    public List<SchemaTemplate> Templates { get; } = new List<SchemaTemplate>();

    /// <summary>Ids are case-insensitive (TZXDATA 4.1); the first match wins if the schema has duplicates.</summary>
    public SchemaParameter? FindParameter(string? id) => Find(Parameters, id, parameter => parameter.Id);
    public SchemaEnum? FindEnum(string? id) => Find(Enums, id, item => item.Id);
    public SchemaTemplate? FindTemplate(string? id) => Find(Templates, id, template => template.Id);

    /// <summary>
    /// Templates that may be assigned to every one of <paramref name="entityTypes"/> (TZXDATA 4.4, <c>VELEC_TEMPLATE_APPLY</c>),
    /// in the order of the schema; repeated or empty ids are skipped.
    /// </summary>
    public IReadOnlyList<SchemaTemplate> TemplatesFor(IEnumerable<string> entityTypes)
    {
        var types = entityTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Templates.Where(template => !string.IsNullOrEmpty(template.Id) && ReferenceEquals(FindTemplate(template.Id), template) &&
            types.All(template.Accepts)).ToArray();
    }

    public static bool SameId(string? left, string? right) =>
        left != null && right != null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static T? Find<T>(IEnumerable<T> items, string? id, Func<T, string?> key) where T : class =>
        id is null ? null : items.FirstOrDefault(item => SameId(key(item), id));
}

/// <summary>Base for schema objects: unknown JSON fields are preserved in source order.</summary>
public abstract class SchemaElement
{
    public JsonObject Extra { get; } = new JsonObject();
}

public sealed class SchemaParameter : SchemaElement
{
    public string? Id { get; set; }
    /// <summary>Type name as written in the schema; resolved by <see cref="ParameterTypeRegistry"/>.</summary>
    public string? Type { get; set; }
    public string? Caption { get; set; }
    public string? Description { get; set; }
    public string? Group { get; set; }
    /// <summary>Raw JSON default so that number formatting and invalid values survive round-trip.</summary>
    public JsonValue? Default { get; set; }
    /// <summary>Id of an <see cref="SchemaEnum"/> for <c>Enum</c> parameters.</summary>
    public string? Enum { get; set; }
    /// <summary><c>@@[Id]</c> format of a <c>CalculatedString</c>.</summary>
    public string? Format { get; set; }
    public bool? Persist { get; set; }
    /// <summary>Parameter ids of <c>Table</c> columns.</summary>
    public List<string>? Columns { get; set; }
    /// <summary><c>Table</c>: enum column grouping rows in VST (TZXDATA 4.2).</summary>
    public string? GroupBy { get; set; }
    /// <summary><c>Table</c>: column matching rows in group editing; defaults to the first <c>String</c> column.</summary>
    public string? Key { get; set; }
    public ParameterEditor? Editor { get; set; }

    public string EffectiveGroup => string.IsNullOrEmpty(Group) ? ParameterSchema.DefaultGroup : Group!;
}

/// <summary>Editor settings (TZXDATA 4.1, 8.2): presentation only, never stored in XDATA.</summary>
public sealed class ParameterEditor : SchemaElement
{
    public string? Unit { get; set; }
    public int? Precision { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public int? MaxLength { get; set; }
    public bool? ReadOnly { get; set; }
    public bool? Multiline { get; set; }
    public bool? AllowCustom { get; set; }
    /// <summary>VST column (1–4) that shows this parameter when it is a <c>Table</c> column.</summary>
    public int? VstColumn { get; set; }
}

public sealed class SchemaEnum : SchemaElement
{
    public string? Id { get; set; }
    public string? Caption { get; set; }
    public List<SchemaEnumItem> Items { get; } = new List<SchemaEnumItem>();

    /// <summary>The key is what XDATA stores; lookup is ordinal so that stored keys stay exact.</summary>
    public SchemaEnumItem? FindItem(string? key) => key is null ? null : Items.FirstOrDefault(item => item.Key == key);
}

public sealed class SchemaEnumItem : SchemaElement
{
    public SchemaEnumItem() { }
    public SchemaEnumItem(string key, string caption) { Key = key; Caption = caption; }
    public string? Key { get; set; }
    public string? Caption { get; set; }
}

public sealed class SchemaTemplate : SchemaElement
{
    public string? Id { get; set; }
    public string? Caption { get; set; }
    /// <summary>Optional entity type filter (<c>BlockReference</c>, <c>Polyline</c>, …).</summary>
    public List<string>? EntityTypes { get; set; }
    /// <summary>Ordered references to parameters, not copies of their descriptions.</summary>
    public List<TemplateParameter> Parameters { get; } = new List<TemplateParameter>();

    /// <summary>«Устройство (DEVICE)»: the caption with the id for menus and prompts; the id without a caption.</summary>
    public string DisplayName => string.IsNullOrEmpty(Caption) || Caption == Id ? Id ?? "" : Caption + " (" + Id + ")";

    /// <summary>The template may be assigned to an entity of <paramref name="entityType"/>: no filter, or the type is listed.</summary>
    public bool Accepts(string? entityType) =>
        EntityTypes is null || EntityTypes.Count == 0 || EntityTypes.Any(type => string.Equals(type?.Trim(), entityType, StringComparison.OrdinalIgnoreCase));
}

public sealed class TemplateParameter : SchemaElement
{
    public TemplateParameter() { }
    public TemplateParameter(string id, JsonValue? @default = null) { Id = id; Default = @default; }
    public string? Id { get; set; }
    /// <summary>Template default overriding the parameter default.</summary>
    public JsonValue? Default { get; set; }
}
