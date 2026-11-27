using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// The value of a navigator level or column on one object (issue #66): the text that is shown, or a mark that the
/// value is not set (<see cref="IsEmpty"/>) or could not be computed (<see cref="IsError"/>, with <see cref="Error"/>).
/// </summary>
public sealed class NavigatorValue
{
    public static NavigatorValue Empty { get; } = new NavigatorValue("", true, false, null, false);

    private NavigatorValue(string text, bool isEmpty, bool isError, string? error, bool isUnknown)
    {
        Text = text; IsEmpty = isEmpty; IsError = isError; Error = error; IsUnknown = isUnknown;
    }

    public string Text { get; }
    /// <summary>The parameter is missing on the object, or its value is an empty string.</summary>
    public bool IsEmpty { get; }
    /// <summary>The value could not be computed: an error of a <c>CalculatedString</c> or of the format.</summary>
    public bool IsError { get; }
    public string? Error { get; }
    /// <summary>A referenced parameter is neither in the schema, nor on the object, nor an entity property.</summary>
    public bool IsUnknown { get; }

    public static NavigatorValue Of(string? text) => string.IsNullOrEmpty(text) ? Empty : new NavigatorValue(text!, false, false, null, false);
    public static NavigatorValue Failed(string error, string? text = null) =>
        new NavigatorValue(string.IsNullOrEmpty(text) ? FormulaExpression.ErrorText : text!, false, true, error, false);
    public static NavigatorValue Unknown(string error) => new NavigatorValue("", true, false, error, true);

    public override string ToString() => IsError ? Text + " (" + Error + ")" : Text;
}

/// <summary>
/// The source of a navigator level or column (issue #66): a parameter id (<c>NMO_BaseName</c>) or a
/// <c>CalculatedString</c> format (<c>@@[NMO_Name]</c>, <c>@@[A]-@@[B]</c>, <c>=@@[Power]*2</c>) evaluated with the
/// existing mechanism of TZXDATA 7 (<see cref="CalculatedFormat"/>, <see cref="FormulaExpression"/>, <see cref="ObjectCalculation"/>).
/// New kinds of sources are added here without changing the profile format.
/// </summary>
public sealed class NavigatorValueSource
{
    private readonly FormulaExpression? expression;

    private NavigatorValueSource(string text, NavigatorSourceKind kind, IReadOnlyList<FormatFragment> fragments, FormulaExpression? expression, string? error)
    {
        Text = text; Kind = kind; Fragments = fragments; this.expression = expression; Error = error;
    }

    public string Text { get; }
    public NavigatorSourceKind Kind { get; }
    public IReadOnlyList<FormatFragment> Fragments { get; }
    /// <summary>A syntax error of the source; such a source gives an error value on every object.</summary>
    public string? Error { get; }
    public bool IsValid => Error is null;
    /// <summary>The ids the source refers to (parameters, attributes, geometry), in the order of the text.</summary>
    public IReadOnlyList<string> References => Fragments.Where(fragment => fragment.Reference != null).Select(fragment => fragment.Reference!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// A plain id is a parameter; a text with <c>@@</c>, or one that starts with <c>=</c>, is a format.
    /// Anything else (an empty text, spaces or brackets in an id) is an error of the source.
    /// </summary>
    public static NavigatorValueSource Parse(string? text)
    {
        var source = (text ?? "").Trim();
        if (source.Length == 0) return new NavigatorValueSource(source, NavigatorSourceKind.Parameter, Array.Empty<FormatFragment>(), null, "пустой источник значения");
        if (IsParameterId(source))
            return new NavigatorValueSource(source, NavigatorSourceKind.Parameter, new[] { FormatFragment.Parameter(source, false) }, null, null);
        if (CalculatedFormat.IsFormula(source))
        {
            var compiled = FormulaExpression.Compile(source);
            return new NavigatorValueSource(source, NavigatorSourceKind.Formula, compiled.References, compiled, compiled.Error);
        }
        if (source.IndexOf("@@", StringComparison.Ordinal) < 0)
            return new NavigatorValueSource(source, NavigatorSourceKind.Parameter, Array.Empty<FormatFragment>(), null,
                "«" + source + "» не является идентификатором параметра или форматом @@[Id]");
        var fragments = CalculatedFormat.Parse(source, out var error);
        if (error is null && !fragments.Any(fragment => fragment.Reference != null))
            error = "формат «" + source + "» не ссылается ни на один параметр";
        return new NavigatorValueSource(source, NavigatorSourceKind.Format, fragments, null, error);
    }

    /// <summary>Letters, digits, <c>_</c>, <c>-</c> and <c>.</c>; the first character is not a digit.</summary>
    public static bool IsParameterId(string text)
    {
        if (string.IsNullOrEmpty(text) || char.IsDigit(text[0])) return false;
        foreach (var symbol in text)
            if (!char.IsLetterOrDigit(symbol) && symbol != '_' && symbol != '-' && symbol != '.') return false;
        return true;
    }

    /// <summary>The value on <paramref name="target"/>; it never throws, an error of one object stays in its value.</summary>
    public NavigatorValue Evaluate(NavigatorObject target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (Error != null) return NavigatorValue.Failed(Error);
        try
        {
            return Kind switch
            {
                NavigatorSourceKind.Parameter => target.Resolve(Fragments[0]).Value,
                NavigatorSourceKind.Formula => EvaluateFormula(target),
                _ => EvaluateFormat(target)
            };
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return NavigatorValue.Failed(failure.Message);
        }
    }

    private NavigatorValue EvaluateFormat(NavigatorObject target)
    {
        var text = new StringBuilder();
        var unknown = new List<string>();
        var references = 0;
        var empty = 0;
        foreach (var fragment in Fragments)
        {
            if (fragment.Reference is null) { text.Append(fragment.Text); continue; }
            references++;
            var value = target.Resolve(fragment).Value;
            if (value.IsError) return value;
            if (value.IsUnknown) unknown.Add(value.Error!);
            if (value.IsEmpty) empty++;
            text.Append(value.Text);
        }
        // A format of references only, all of them missing, is not set: «@@[A]@@[B]» of an object without A and B.
        if (references == empty && text.ToString().Trim().Length == 0)
            return unknown.Count != 0 ? NavigatorValue.Unknown(string.Join("; ", unknown)) : NavigatorValue.Empty;
        return NavigatorValue.Of(text.ToString());
    }

    private NavigatorValue EvaluateFormula(NavigatorObject target)
    {
        string? failed = null;
        var text = expression!.Evaluate(fragment =>
        {
            var resolved = target.Resolve(fragment);
            if (resolved.Value.IsError) failed ??= resolved.Value.Error;
            return resolved.Typed;
        }, fragment =>
        {
            var resolved = target.Resolve(fragment);
            if (resolved.Value.IsError) failed ??= resolved.Value.Error;
            return resolved.Value.Text;
        }, target.Culture);
        if (failed != null) return NavigatorValue.Failed("зависит от ошибочного значения: " + failed);
        return NavigatorValue.Of(text);
    }
}

public enum NavigatorSourceKind
{
    /// <summary>A parameter id: the value of the parameter, as in the property grid.</summary>
    Parameter,
    /// <summary>A <c>CalculatedString</c> format with <c>@@[Id]</c> references.</summary>
    Format,
    /// <summary>An NCalc expression <c>=…</c> (issue #43).</summary>
    Formula
}

/// <summary>A resolved reference: the shown text and the typed value used by formula mode.</summary>
public readonly struct NavigatorResolved
{
    public NavigatorResolved(NavigatorValue value, object? typed) { Value = value; Typed = typed; }
    public NavigatorValue Value { get; }
    public object? Typed { get; }
}

/// <summary>
/// One object of the navigator (issue #66): an adapter over the cached <see cref="ObjectValues"/> of the existing
/// parser — the navigator does not parse XDATA itself. It has no AutoCAD types: the object is found again by
/// <see cref="Handle"/>. The <see cref="ObjectCalculation"/> is built on first use, and values are memoized per source
/// text, so switching profiles, expanding nodes and sorting do not evaluate them again.
/// </summary>
public sealed class NavigatorObject
{
    private readonly Dictionary<string, NavigatorValue> values = new Dictionary<string, NavigatorValue>(StringComparer.Ordinal);
    private ObjectCalculation? calculation;
    private string? calculationError;

    public NavigatorObject(ObjectValues source, string? typeName = null, CultureInfo? culture = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        TypeName = string.IsNullOrEmpty(typeName) ? source.EntityType : typeName!;
        Culture = culture ?? CultureInfo.CurrentCulture;
    }

    public ObjectValues Source { get; }
    public string Handle => Source.Handle;
    /// <summary>The localized type name of the entity («Блок», «Отрезок»), shown for a leaf without a name.</summary>
    public string TypeName { get; }
    public CultureInfo Culture { get; }
    /// <summary>How many values were computed; tests check that a profile switch reuses them.</summary>
    public int EvaluationCount { get; private set; }
    public bool IsCalculated => calculation != null;

    public NavigatorValue Value(NavigatorValueSource source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (values.TryGetValue(source.Text, out var value)) return value;
        EvaluationCount++;
        value = source.Evaluate(this);
        values[source.Text] = value;
        return value;
    }

    private ObjectCalculation? Calculation
    {
        get
        {
            if (calculation != null || calculationError != null) return calculation;
            try { calculation = new ObjectCalculation(Source, Culture); }
            catch (Exception failure) when (failure is not OutOfMemoryException) { calculationError = failure.Message; }
            return calculation;
        }
    }

    /// <summary>
    /// The value of a reference, by the rules of <c>CalculatedString</c> (TZXDATA 7.2): a schema formula gives its
    /// computed text, a schema parameter its stored value or default formatted by its type (Enum caption, Double with
    /// <c>Editor.Precision</c>, Boolean «Да»/«Нет»); otherwise a value stored on the object, a block attribute or the
    /// geometry. A reference that is none of them is unknown.
    /// </summary>
    public NavigatorResolved Resolve(FormatFragment fragment)
    {
        var id = fragment.Reference ?? throw new ArgumentException("A literal is not a reference.", nameof(fragment));
        var schema = Source.Schema;
        var entity = Source.EntityProperties;
        if (fragment.IsGeometry) return EntityValue(entity.Geometry(id), id);
        if (fragment.IsAttribute)
            return EntityValue(entity.Attributes.TryGetValue(id, out var attribute) ? attribute : null, id);
        if (schema.Parameters.TryGetValue(id, out var parameter))
        {
            var calc = Calculation;
            if (calc is null) return new NavigatorResolved(NavigatorValue.Failed("ошибка вычисления: " + calculationError), null);
            var kind = ParameterTypeRegistry.Default.Find(parameter.Type)?.Kind;
            if (calc.Formulas.Find(parameter.Id) != null)
            {
                var result = calc.Find(parameter.Id!);
                if (result is null) return new NavigatorResolved(NavigatorValue.Empty, null);
                if (result.IsError) return new NavigatorResolved(NavigatorValue.Failed(parameter.Id + ": " + result.Error, result.Text), result.Text);
                return new NavigatorResolved(NavigatorValue.Of(result.Text), result.Text);
            }
            if (kind is ParameterKind.Table or ParameterKind.Repeat)
                return new NavigatorResolved(NavigatorValue.Failed("табличный параметр " + parameter.Id + " не выводится в навигаторе"), null);
            var value = calc.Value(parameter.Id!);
            var text = ObjectCalculation.FormatValue(schema, parameter, value, fragment.UseKey, Culture);
            return new NavigatorResolved(NavigatorValue.Of(text), kind == ParameterKind.Enum && value is string ? text : value);
        }
        var stored = Source.Find(id);
        if (stored != null && !stored.IsTable)
            return new NavigatorResolved(NavigatorValue.Of(stored.Value as string ?? XDataV2.FormatValue(stored.Value)), stored.Value);
        if (!fragment.UseKey && EntityProperties.IsGeometryId(id)) return EntityValue(entity.Geometry(id), id);
        if (!fragment.UseKey && entity.Attributes.TryGetValue(id, out var tag)) return EntityValue(tag, id);
        return schema.Schema is null
            ? new NavigatorResolved(NavigatorValue.Empty, null)
            : new NavigatorResolved(NavigatorValue.Unknown("неизвестный параметр " + id), null);
    }

    private NavigatorResolved EntityValue(object? value, string id) =>
        new NavigatorResolved(value is null ? NavigatorValue.Empty : NavigatorValue.Of(EntityProperties.Format(value, Culture)), value);

    public override string ToString() => Handle + " " + TypeName;
}

/// <summary>A level of the tree expression: <c>+NMO_BaseName</c> is enabled, <c>-DB_link</c> is skipped.</summary>
public sealed class NavigatorLevel
{
    internal NavigatorLevel(int index, string part, bool enabled, string text, bool isLeaf)
    {
        Index = index; Part = part; Enabled = enabled; Text = text; IsLeaf = isLeaf;
        Source = NavigatorValueSource.Parse(text);
    }

    public int Index { get; }
    /// <summary>The part as written, with the prefix.</summary>
    public string Part { get; }
    public bool Enabled { get; }
    /// <summary>The part without the prefix: a parameter id or a format.</summary>
    public string Text { get; }
    /// <summary>The last part: the name of a leaf rather than a group.</summary>
    public bool IsLeaf { get; }
    public NavigatorValueSource Source { get; }

    public override string ToString() => Part;
}

/// <summary>
/// The tree expression of a profile (issue #66, ZCAD <c>TreeBuildMap</c>):
/// <c>-DB_link|-DESC_MountingParts|+NMO_BaseName|+NMO_Suffix|-@@[NMO_Name]</c>.
/// Parts are separated by <c>|</c> (a <c>|</c> inside <c>@@[…]</c> is part of the reference). A part starting with <c>+</c>
/// is enabled and one starting with <c>-</c> is disabled and skipped. Enabled parts but the last are group levels; the last
/// part names the leaf — the object type name when it is disabled or its value is empty.
/// Unlike ZCAD, which drops the first character of any part, a part without the prefix is an error.
/// </summary>
public sealed class NavigatorTreeExpression
{
    private NavigatorTreeExpression(string text, IReadOnlyList<NavigatorLevel> levels, IReadOnlyList<string> errors)
    {
        Text = text; Levels = levels; Errors = errors;
    }

    public string Text { get; }
    public IReadOnlyList<NavigatorLevel> Levels { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;
    /// <summary>The enabled levels that make groups, from the root down.</summary>
    public IReadOnlyList<NavigatorLevel> Groups => Levels.Where(level => level.Enabled && !level.IsLeaf).ToArray();
    /// <summary>The level that names leaves, or null when the last part is disabled.</summary>
    public NavigatorLevel? Leaf => Levels.Count != 0 && Levels[Levels.Count - 1].Enabled ? Levels[Levels.Count - 1] : null;
    /// <summary>The ids the enabled levels refer to.</summary>
    public IReadOnlyList<string> References => Levels.Where(level => level.Enabled).SelectMany(level => level.Source.References)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static NavigatorTreeExpression Parse(string? expression)
    {
        var text = expression ?? "";
        var errors = new List<string>();
        var levels = new List<NavigatorLevel>();
        if (text.Trim().Length == 0)
        {
            errors.Add("выражение дерева пустое");
            return new NavigatorTreeExpression(text, levels, errors);
        }
        var parts = Split(text);
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i].Trim();
            var number = (i + 1).ToString(CultureInfo.InvariantCulture);
            if (part.Length == 0) { errors.Add("уровень " + number + " пустой"); continue; }
            if (part[0] != '+' && part[0] != '-')
            {
                errors.Add("уровень " + number + " «" + part + "»: нужен префикс + (включён) или - (выключен)");
                continue;
            }
            var enabled = part[0] == '+';
            var level = new NavigatorLevel(i, part, enabled, part.Substring(1).Trim(), i == parts.Count - 1);
            if (enabled && level.Source.Error != null) errors.Add("уровень " + number + " «" + part + "»: " + level.Source.Error);
            levels.Add(level);
        }
        return new NavigatorTreeExpression(text, levels, errors);
    }

    /// <summary>The ids of enabled levels that are not in <paramref name="schema"/>; their objects go to the empty group.</summary>
    public IReadOnlyList<string> UnknownReferences(SchemaSnapshot? schema) => NavigatorProfile.Unknown(References, schema);

    private static List<string> Split(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var symbol = text[i];
            if (symbol == '[') depth++;
            else if (symbol == ']' && depth > 0) depth--;
            else if (symbol == '|' && depth == 0)
            {
                parts.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(text.Substring(start));
        return parts;
    }

    public override string ToString() => Text;
}
