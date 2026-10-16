using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// The prompt of <c>VELEC_TEMPLATE_APPLY</c> (TZXDATA 6.4, 6.6): the templates of the schema that suit every selected entity type,
/// chosen by Id or number; <see cref="None"/> removes the template. The write itself is <see cref="SelectionEdit.SetTemplate"/>
/// for the whole selection in one transaction, as the «Шаблон ▾» menu of the panel does.
/// </summary>
public sealed class TemplateApply
{
    /// <summary>The answer that removes the template; values and <c>DEVICE_ID</c> stay.</summary>
    public const string None = "-";

    public TemplateApply(SchemaSnapshot schema, IReadOnlyList<ObjectValues> objects)
    {
        if (schema is null) throw new ArgumentNullException(nameof(schema));
        Objects = objects ?? throw new ArgumentNullException(nameof(objects));
        Templates = schema.Schema?.TemplatesFor(objects.Select(item => item.EntityType)) ?? Array.Empty<SchemaTemplate>();
        var ids = objects.Select(item => item.Template ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Current = ids.Length == 1 && ids[0].Length != 0 ? ids[0] : null;
        HasTemplate = objects.Any(item => item.Template != null);
    }

    public IReadOnlyList<ObjectValues> Objects { get; }
    /// <summary>Templates whose <c>EntityTypes</c> accept every selected object, in the order of the schema.</summary>
    public IReadOnlyList<SchemaTemplate> Templates { get; }
    /// <summary>The template of every object, or null when they differ or have none.</summary>
    public string? Current { get; }
    public bool HasTemplate { get; }

    /// <summary>Numbered list for the command line; <c>*</c> marks the current template.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = Templates.Select((template, index) => (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + template.DisplayName +
            (ParameterSchema.SameId(template.Id, Current) ? " *" : "")).ToList();
        if (HasTemplate) lines.Add(None + ". Без шаблона");
        return lines;
    }

    /// <summary>Template Id for an answer (Id or number, any case), <c>""</c> for <see cref="None"/>, null when not recognized.</summary>
    public string? Parse(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (text == None) return HasTemplate ? "" : null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number >= 1 && number <= Templates.Count ? Templates[number - 1].Id : null;
        return Templates.FirstOrDefault(template => ParameterSchema.SameId(template.Id, text))?.Id;
    }

    /// <summary>Objects the template is written to: those without this template, v2 format or <c>DEVICE_ID</c>.</summary>
    public int Changes(string id) => Objects.Count(item => id.Length == 0
        ? item.Template != null
        : !ParameterSchema.SameId(item.Template, id) || item.Format != XDataFormat.V2 || item.DeviceId is null);
}
