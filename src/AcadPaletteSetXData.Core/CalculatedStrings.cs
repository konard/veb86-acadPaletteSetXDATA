using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>A <c>CalculatedString</c> of the schema, parsed once when the schema is loaded (TZXDATA 7.3).</summary>
public sealed class Formula
{
    private readonly List<string> errors = new List<string>();
    private readonly List<string> references = new List<string>();

    internal Formula(SchemaParameter parameter, IReadOnlyList<FormatFragment> fragments)
    {
        Parameter = parameter; Fragments = fragments;
    }

    public string Id => Parameter.Id!;
    public SchemaParameter Parameter { get; }
    public IReadOnlyList<FormatFragment> Fragments { get; }
    /// <summary>The result is also written to XDATA (TZXDATA 7.4).</summary>
    public bool Persist => Parameter.Persist == true;
    /// <summary>Referenced parameters of the schema, in the schema spelling, without repetitions.</summary>
    public IReadOnlyList<string> References => references;
    /// <summary>Format errors, unknown references, cycles and references to erroneous formulas; empty when correct.</summary>
    public IReadOnlyList<string> Errors => errors;
    public bool IsError => errors.Count != 0;
    public string Error => string.Join("; ", errors);
    /// <summary>The formula refers to itself through other formulas.</summary>
    public bool InCycle { get; internal set; }
    /// <summary>Position in the evaluation order: referenced formulas come first.</summary>
    public int Order { get; internal set; }
    /// <summary>Formulas of one cycle share the component; references inside it stay as <c>@@[Id]</c>.</summary>
    internal int Component { get; set; }

    internal void AddError(string error) { if (!errors.Contains(error)) errors.Add(error); }
    internal void AddReference(string id) { if (!references.Any(item => ParameterSchema.SameId(item, id))) references.Add(id); }
}

/// <summary>
/// Formulas of one schema snapshot (TZXDATA 7.3): fragments of every <c>CalculatedString</c>, the dependency graph
/// <c>Id</c> → dependent formulas, cycles and unknown references. Built once per <see cref="SchemaSnapshot"/>.
/// </summary>
public sealed class FormulaSet
{
    private readonly Dictionary<string, Formula> byId = new Dictionary<string, Formula>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Formula>> direct = new Dictionary<string, List<Formula>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<Formula>> dependents = new Dictionary<string, IReadOnlyList<Formula>>(StringComparer.OrdinalIgnoreCase);
    private readonly List<Formula> order = new List<Formula>();

    private FormulaSet() { }

    /// <summary>All formulas in the evaluation order: a formula follows the formulas it refers to.</summary>
    public IReadOnlyList<Formula> Formulas => order;

    public Formula? Find(string? id) => id != null && byId.TryGetValue(id, out var formula) ? formula : null;

    /// <summary>Errors of all formulas as <c>Id: error</c>.</summary>
    public IReadOnlyList<string> Diagnostics => order.Where(formula => formula.IsError).Select(formula => formula.Id + ": " + formula.Error).ToArray();

    /// <summary>
    /// Formulas to recalculate after <paramref name="id"/> changes: direct and indirect dependents, in the evaluation order.
    /// </summary>
    public IReadOnlyList<Formula> Dependents(string id)
    {
        if (dependents.TryGetValue(id, out var cached)) return cached;
        var found = new HashSet<Formula>();
        var queue = new Queue<string>();
        queue.Enqueue(id);
        while (queue.Count != 0)
            if (direct.TryGetValue(queue.Dequeue(), out var next))
                foreach (var formula in next)
                    if (found.Add(formula)) queue.Enqueue(formula.Id);
        var result = found.OrderBy(formula => formula.Order).ToArray();
        dependents[id] = result;
        return result;
    }

    public static FormulaSet Build(SchemaSnapshot schema, ParameterTypeRegistry? types = null)
    {
        if (schema is null) throw new ArgumentNullException(nameof(schema));
        types ??= ParameterTypeRegistry.Default;
        var set = new FormulaSet();
        var parsed = new List<Formula>();
        foreach (var parameter in schema.Schema?.Parameters ?? Enumerable.Empty<SchemaParameter>())
        {
            if (parameter.Id is null || set.byId.ContainsKey(parameter.Id) || !ReferenceEquals(schema.Parameters[parameter.Id], parameter)) continue;
            if (types.Find(parameter.Type)?.Kind != ParameterKind.CalculatedString) continue;
            string? error = null;
            var fragments = string.IsNullOrEmpty(parameter.Format) ? Array.Empty<FormatFragment>() : CalculatedFormat.Parse(parameter.Format!, out error);
            var formula = new Formula(parameter, fragments);
            if (string.IsNullOrEmpty(parameter.Format)) formula.AddError("не задан format");
            if (error != null) formula.AddError(error);
            set.byId.Add(parameter.Id, formula);
            parsed.Add(formula);
        }

        foreach (var formula in parsed)
            foreach (var fragment in formula.Fragments.Where(fragment => fragment.Reference != null))
            {
                var name = Reference(fragment);
                if (!schema.Parameters.TryGetValue(fragment.Reference!, out var target)) { formula.AddError("неизвестная ссылка " + name); continue; }
                var kind = types.Find(target.Type)?.Kind;
                if (kind == ParameterKind.Table) { formula.AddError("таблица " + target.Id + " не подставляется в " + name); continue; }
                if (fragment.UseKey && kind != ParameterKind.Enum) formula.AddError(name + ": модификатор :key применим только к Enum");
                formula.AddReference(target.Id!);
                if (!set.direct.TryGetValue(target.Id!, out var list)) set.direct.Add(target.Id!, list = new List<Formula>());
                if (!list.Contains(formula)) list.Add(formula);
            }

        set.Sort(parsed);
        // A formula that uses an erroneous one shows its error too: the referenced text is not a valid result.
        foreach (var formula in set.order.Where(formula => !formula.InCycle))
            foreach (var reference in formula.References)
                if (set.Find(reference) is Formula used && used.IsError) formula.AddError("зависит от ошибочного параметра " + used.Id);
        return set;
    }

    internal static string Reference(FormatFragment fragment) => "@@[" + fragment.Reference + (fragment.UseKey ? ":key" : "") + "]";

    /// <summary>
    /// Tarjan's strongly connected components over formula → referenced formula: components come out with
    /// their references first, which is the evaluation order; a component of several formulas, or a formula
    /// referring to itself, is a cycle.
    /// </summary>
    private void Sort(List<Formula> formulas)
    {
        var index = new Dictionary<Formula, int>();
        var low = new Dictionary<Formula, int>();
        var stack = new Stack<Formula>();
        var onStack = new HashSet<Formula>();
        var component = 0;

        IEnumerable<Formula> Next(Formula formula) => formula.References.Select(Find).OfType<Formula>();

        void Visit(Formula formula)
        {
            index[formula] = low[formula] = index.Count;
            stack.Push(formula); onStack.Add(formula);
            foreach (var next in Next(formula))
            {
                if (!index.ContainsKey(next)) { Visit(next); low[formula] = Math.Min(low[formula], low[next]); }
                else if (onStack.Contains(next)) low[formula] = Math.Min(low[formula], index[next]);
            }
            if (low[formula] != index[formula]) return;
            var members = new List<Formula>();
            Formula member;
            do { member = stack.Pop(); onStack.Remove(member); members.Add(member); } while (member != formula);
            members.Reverse();
            var cycle = members.Count > 1 || Next(formula).Contains(formula);
            foreach (var item in members)
            {
                item.Component = component;
                item.Order = order.Count;
                order.Add(item);
                if (!cycle) continue;
                item.InCycle = true;
                item.AddError("циклическая ссылка: " + string.Join(" → ", CyclePath(item, members)));
            }
            component++;
        }

        foreach (var formula in formulas)
            if (!index.ContainsKey(formula)) Visit(formula);
    }

    /// <summary>The shortest path from <paramref name="start"/> back to itself inside its cycle.</summary>
    private IEnumerable<string> CyclePath(Formula start, List<Formula> members)
    {
        var previous = new Dictionary<Formula, Formula>();
        var queue = new Queue<Formula>();
        queue.Enqueue(start);
        while (queue.Count != 0)
        {
            var current = queue.Dequeue();
            foreach (var next in current.References.Select(Find).OfType<Formula>().Where(members.Contains))
            {
                if (next == start)
                {
                    var path = new List<string> { start.Id };
                    for (var item = current; item != start; item = previous[item]) path.Insert(1, item.Id);
                    path.Add(start.Id);
                    return path;
                }
                if (previous.ContainsKey(next)) continue;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }
        return new[] { start.Id, start.Id };
    }
}

/// <summary>The result of a <c>CalculatedString</c> on one object.</summary>
public sealed class CalculatedValue
{
    internal CalculatedValue(Formula formula, string text, string? stored) { Formula = formula; Text = text; Stored = stored; }
    public Formula Formula { get; }
    public string Id => Formula.Id;
    /// <summary>The computed text; unknown references stay as <c>@@[Id]</c> (TZXDATA 7.3).</summary>
    public string Text { get; }
    /// <summary>The text stored in XDATA when it was read; for <c>Persist</c> it is derived and not used (TZXDATA 7.4).</summary>
    public string? Stored { get; }
    public bool IsError => Formula.IsError;
    public string? Error => Formula.IsError ? Formula.Error : null;
    /// <summary>
    /// A stored <c>Persist</c> value whose text differs from the computed one; a missing value is not stale, it is written
    /// with the next change of a source or by <see cref="SelectionEdit.RecalculatePersisted"/>.
    /// </summary>
    public bool IsStale => Formula.Persist && !Formula.IsError && Stored != null && Stored != Text;
}

/// <summary>
/// The <c>CalculatedString</c> values of one object computed from its cached values (TZXDATA 7.3), without reading
/// XDATA. A change of a parameter recalculates only the formulas that depend on it.
/// </summary>
public sealed class ObjectCalculation
{
    /// <summary>The value of a group of objects whose results differ (TZXDATA 7.3).</summary>
    public const string Various = "Разное";

    private readonly ParameterTypeRegistry types;
    private readonly CultureInfo culture;
    private readonly Dictionary<string, object?> changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CalculatedValue> results = new Dictionary<string, CalculatedValue>(StringComparer.OrdinalIgnoreCase);

    /// <param name="culture">Culture of the decimal separator of <c>Double</c> values; the current culture by default (TZXDATA 7.2).</param>
    public ObjectCalculation(ObjectValues values, CultureInfo? culture = null, ParameterTypeRegistry? types = null)
    {
        Source = values ?? throw new ArgumentNullException(nameof(values));
        this.types = types ?? ParameterTypeRegistry.Default;
        this.culture = culture ?? CultureInfo.CurrentCulture;
        Formulas = ReferenceEquals(this.types, ParameterTypeRegistry.Default) ? values.Schema.Formulas : FormulaSet.Build(values.Schema, this.types);
        Template = values.Template;
        Recalculate(Formulas.Formulas);
    }

    public ObjectValues Source { get; }
    public string Handle => Source.Handle;
    public SchemaSnapshot Schema => Source.Schema;
    public FormulaSet Formulas { get; }
    /// <summary>The template whose defaults fill missing values.</summary>
    public string? Template { get; private set; }
    /// <summary>How many formulas were evaluated; tests check that a change recalculates only dependents.</summary>
    public int EvaluationCount { get; private set; }
    /// <summary>All formulas of the schema in the evaluation order.</summary>
    public IReadOnlyList<CalculatedValue> Results => Formulas.Formulas.Select(formula => results[formula.Id]).ToArray();

    public CalculatedValue? Find(string id) => results.TryGetValue(id, out var value) ? value : null;
    public string? Text(string id) => Find(id)?.Text;

    /// <summary>The parameter is stored on the object or listed in its template.</summary>
    public bool IsUsed(string id) =>
        Source.Find(id) != null || Template != null && Schema.Templates.TryGetValue(Template, out var template) &&
        template.Parameters.Any(item => ParameterSchema.SameId(item.Id, id));

    /// <summary>
    /// One line for the trace: the formulas that are used by the object or reference its values,
    /// e.g. <c>A1: GC_Name = «ЩУ1.3»</c>; erroneous ones are marked with <c>!</c>, stale <c>Persist</c> with <c>*</c>.
    /// </summary>
    public string Describe()
    {
        // Results are in the evaluation order, so a formula over a shown formula follows it.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shown = new List<string>();
        foreach (var result in Results)
        {
            if (!IsUsed(result.Id) && !result.Formula.References.Any(id => ids.Contains(id) || IsUsed(id) || changes.ContainsKey(id))) continue;
            ids.Add(result.Id);
            shown.Add(result.Id + " = «" + result.Text + "»" + (result.IsError ? " !" : result.IsStale ? " *" : ""));
        }
        return Handle + ": " + (shown.Count == 0 ? "формул нет" : string.Join("; ", shown));
    }

    /// <summary>
    /// Changes a parameter of the object (null removes it) and recalculates its dependents; returns them in the
    /// evaluation order.
    /// </summary>
    public IReadOnlyList<CalculatedValue> SetValue(string id, object? value)
    {
        if (id is null) throw new ArgumentNullException(nameof(id));
        if (Formulas.Find(id) != null) throw new ArgumentException("Параметр " + id + " вычисляется (CalculatedString) и не задаётся вручную.", nameof(id));
        changes[id] = value;
        return Recalculate(Formulas.Dependents(id));
    }

    /// <summary>Changes the template: its defaults may change every formula.</summary>
    public IReadOnlyList<CalculatedValue> SetTemplate(string? template)
    {
        Template = string.IsNullOrEmpty(template) ? null : template;
        return Recalculate(Formulas.Formulas);
    }

    /// <summary>The value used for <paramref name="id"/>: the changed or stored one, otherwise the default (TZXDATA 6.2).</summary>
    public object? Value(string id)
    {
        if (!Schema.Parameters.TryGetValue(id, out var parameter)) return changes.TryGetValue(id, out var changed) ? changed : Source.Find(id)?.Value;
        if (Formulas.Find(id) != null) return Text(id);
        if (changes.TryGetValue(id, out var value)) return value ?? Default(parameter);
        var stored = Source.Find(id);
        return stored is null || stored.IsTable ? Default(parameter) : stored.Value;
    }

    /// <summary>The common text of a group of objects, or null when it differs (shown as <see cref="Various"/>).</summary>
    public static string? Common(IEnumerable<ObjectCalculation> objects, string id)
    {
        string? common = null;
        var first = true;
        foreach (var item in objects)
        {
            var text = item.Text(id) ?? "";
            if (first) { common = text; first = false; }
            else if (!string.Equals(common, text, StringComparison.Ordinal)) return null;
        }
        return common;
    }

    /// <summary>The text of a group of objects: the common text or <see cref="Various"/>.</summary>
    public static string Merge(IEnumerable<ObjectCalculation> objects, string id) => Common(objects, id) ?? Various;

    /// <summary>
    /// Text substituted for a value (TZXDATA 7.2): <c>String</c> and <c>Integer</c> as is, <c>Enum</c> by the item
    /// caption (the key with <paramref name="useKey"/>), <c>Double</c> with <c>Editor.Precision</c> and the decimal
    /// separator of <paramref name="culture"/>, <c>Boolean</c> as «Да»/«Нет».
    /// </summary>
    public static string FormatValue(SchemaSnapshot schema, SchemaParameter parameter, object? value, bool useKey, CultureInfo culture, ParameterTypeRegistry? types = null)
    {
        if (value is null) return "";
        switch ((types ?? ParameterTypeRegistry.Default).Find(parameter.Type)?.Kind)
        {
            case ParameterKind.Boolean when value is bool flag: return flag ? "Да" : "Нет";
            case ParameterKind.Double when value is double number:
                var precision = parameter.Editor?.Precision;
                return precision is int digits && digits >= 0 && digits <= 15
                    ? number.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), culture)
                    : number.ToString("R", culture);
            case ParameterKind.Integer when value is int integer: return integer.ToString(CultureInfo.InvariantCulture);
            case ParameterKind.Enum when value is string key && !useKey:
                var caption = schema.Schema?.FindEnum(parameter.Enum)?.FindItem(key)?.Caption;
                return string.IsNullOrEmpty(caption) ? key : caption!;
        }
        return value as string ?? XDataV2.FormatValue(value);
    }

    private IReadOnlyList<CalculatedValue> Recalculate(IReadOnlyList<Formula> formulas)
    {
        var recalculated = new CalculatedValue[formulas.Count];
        for (var i = 0; i < formulas.Count; i++)
        {
            var formula = formulas[i];
            EvaluationCount++;
            var stored = Source.Find(formula.Id);
            var value = new CalculatedValue(formula, Evaluate(formula), stored != null && !stored.IsTable && stored.Value is string text ? text : null);
            results[formula.Id] = value;
            recalculated[i] = value;
        }
        return recalculated;
    }

    private string Evaluate(Formula formula)
    {
        var text = new StringBuilder();
        foreach (var fragment in formula.Fragments)
        {
            if (fragment.Reference is null) { text.Append(fragment.Text); continue; }
            if (!Schema.Parameters.TryGetValue(fragment.Reference, out var parameter) || types.Find(parameter.Type)?.Kind == ParameterKind.Table)
            {
                text.Append(FormulaSet.Reference(fragment));
                continue;
            }
            if (Formulas.Find(parameter.Id) is Formula used)
            {
                // Inside a cycle the reference has no value yet and stays in the text.
                if (used.Component == formula.Component) text.Append(FormulaSet.Reference(fragment));
                else text.Append(results.TryGetValue(used.Id, out var result) ? result.Text : "");
                continue;
            }
            text.Append(FormatValue(Schema, parameter, Value(parameter.Id!), fragment.UseKey, culture, types));
        }
        return text.ToString();
    }

    /// <summary>The template default overrides the parameter default; no default gives an empty value (TZXDATA 7.2).</summary>
    private object? Default(SchemaParameter parameter)
    {
        var json = Template != null && Schema.Templates.TryGetValue(Template, out var template)
            ? template.Parameters.FirstOrDefault(item => ParameterSchema.SameId(item.Id, parameter.Id))?.Default
            : null;
        json ??= parameter.Default;
        return json switch
        {
            JsonString text => text.Value,
            JsonBoolean flag => flag.Value,
            JsonNumber number when types.Find(parameter.Type)?.Kind == ParameterKind.Integer && number.TryGetInt32(out var integer) => integer,
            JsonNumber number => number.ToDouble(),
            _ => null
        };
    }
}
