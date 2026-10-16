using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace AcadPaletteSetXData.Core;

/// <summary>Kind of a VST row (TZXDATA 8.2).</summary>
public enum PropertyRowKind
{
    /// <summary>A group of parameters, e.g. «Общие» or «Не описаны в схеме».</summary>
    Group,
    /// <summary>A scalar parameter.</summary>
    Parameter,
    /// <summary>A <c>Table</c> parameter: its rows are children.</summary>
    Table,
    /// <summary>A category of <c>Table</c> rows (<c>GroupBy</c>).</summary>
    Category,
    /// <summary>A row of a <c>Table</c>.</summary>
    TableRow
}

/// <summary>State column of a value (TZXDATA 8.2): <c>●</c> set, <c>○</c> default, <c>ƒ</c> calculated, <c>!</c> error.</summary>
public enum PropertyState
{
    None,
    Set,
    Default,
    Calculated,
    Error
}

/// <summary>A row of the read-only VST: the texts of columns 1–4 and its place in the tree.</summary>
public sealed class PropertyRow : ObservableObject
{
    private bool isExpanded;

    internal PropertyRow(PropertyRowKind kind, string key, string caption, int level)
    {
        Kind = kind; Key = key; Caption = caption; Level = level;
    }

    public PropertyRowKind Kind { get; }
    /// <summary>Stable path of the row, e.g. <c>g:Общие</c> or <c>t:Materials/c:LineFittings</c>; expansion is saved by it.</summary>
    public string Key { get; }
    /// <summary>Parameter <c>Id</c> of a parameter or table; null for groups, categories and table rows.</summary>
    public string? Id { get; internal set; }
    /// <summary>Column 1: <c>Caption</c> with the unit, a group name or the key column of a table row.</summary>
    public string Caption { get; }
    /// <summary>Column 2: the formatted value, «Разное», a row counter or the second table column.</summary>
    public string Value { get; internal set; } = "";
    public PropertyState State { get; internal set; }
    /// <summary>Column 3 of a non-table row: <c>●</c>, <c>○</c>, <c>ƒ</c>, <c>!</c> or the text of a non-<c>Boolean</c> table column.</summary>
    public string StateText { get; internal set; } = "";
    /// <summary>Column 3 of a table row with a <c>Boolean</c> column; null when it differs in the selection.</summary>
    public bool? Flag { get; internal set; }
    public bool HasFlag { get; internal set; }
    /// <summary>Column 4: <c>Description</c>, «по умолчанию», a check message or the comment column of a table row.</summary>
    public string Note { get; internal set; } = "";
    public string ToolTip { get; internal set; } = "";
    /// <summary>Depth in the tree: the indent of column 1.</summary>
    public int Level { get; }
    public IReadOnlyList<PropertyRow> Children => children;
    internal readonly List<PropertyRow> children = new List<PropertyRow>();
    internal bool DefaultExpanded { get; set; } = true;
    public bool IsExpandable => children.Count != 0;
    public bool IsExpanded { get => isExpanded; internal set => SetProperty(ref isExpanded, value); }
    /// <summary>Column 2 differs in the selection: the «Разное» placeholder (grey italic).</summary>
    public bool IsMixed { get; internal set; }
    public bool IsCaptionMixed { get; internal set; }
    public bool IsNoteMixed { get; internal set; }
    /// <summary>The value is a default that is not stored (TZXDATA 6.2): grey.</summary>
    public bool IsDefault { get; internal set; }
    public bool IsError => State == PropertyState.Error;
    /// <summary>Groups and categories are shown in italics.</summary>
    public bool IsHeader => Kind == PropertyRowKind.Group || Kind == PropertyRowKind.Category;
    /// <summary>Column 0: <c>×</c> and <c>✎</c> of a table row (editing is stage 6).</summary>
    public bool ShowActions => Kind == PropertyRowKind.TableRow;
    /// <summary>Column 4 of a table node: «+ Добавить» (editing is stage 6).</summary>
    public bool ShowAdd { get; internal set; }

    public override string ToString() => new string(' ', Level * 2) + Caption + " | " + Value + " | " + (HasFlag ? Flag switch { true => "☑", false => "☐", _ => "?" } : StateText) + " | " + Note;

    internal static string Symbol(PropertyState state) => state switch
    {
        PropertyState.Set => "●",
        PropertyState.Default => "○",
        PropertyState.Calculated => "ƒ",
        PropertyState.Error => "!",
        _ => ""
    };
}

/// <summary>
/// Model of the «Свойства» tab (TZXDATA 8, stage 5): a read-only VST built from the value cache of the selection.
/// Rows of a multiple selection are the union of parameters; differing or missing values are «Разное».
/// </summary>
public sealed class PropertyGridViewModel : ObservableObject
{
    public const string GroupNotInSchema = "Не описаны в схеме";
    public const string GroupExtra = "Дополнительные";
    public const string GroupLegacy = "Прежний формат (только чтение)";
    public const string NoCategory = "Без категории";
    public const string DefaultNote = "по умолчанию";

    private readonly SelectionViewModel selection;
    private readonly CultureInfo culture;
    private readonly ParameterTypeRegistry types;
    private readonly Dictionary<string, Dictionary<string, bool>> expansion = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
    private readonly Dictionary<string, double> offsets = new Dictionary<string, double>(StringComparer.Ordinal);
    private IReadOnlyList<PropertyRow> roots = Array.Empty<PropertyRow>();
    private ObservableCollection<PropertyRow> rows = new ObservableCollection<PropertyRow>();
    private string templateKey = "";
    private string templateText = "";
    private string message = "Выберите объекты в чертеже";
    private string filter = "";
    private bool isEnabled;
    private bool needsTemplate;
    private bool showActions = true, showState = true, showNote = true;

    public PropertyGridViewModel(SelectionViewModel selection, CultureInfo? culture = null, ParameterTypeRegistry? types = null)
    {
        this.selection = selection ?? throw new ArgumentNullException(nameof(selection));
        this.culture = culture ?? CultureInfo.CurrentCulture;
        this.types = types ?? ParameterTypeRegistry.Default;
        ExpandAllCommand = new DelegateCommand(_ => SetAllExpanded(true));
        CollapseAllCommand = new DelegateCommand(_ => SetAllExpanded(false));
        ToggleCommand = new DelegateCommand(row => { if (row is PropertyRow item) Toggle(item); });
        RefreshCommand = new DelegateCommand(_ => Refresh());
        AssignTemplateCommand = new DelegateCommand(_ => { }, _ => false);
        selection.Applied += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>Visible rows of the tree in display order: the items of the virtualized list.</summary>
    public ObservableCollection<PropertyRow> Rows { get => rows; private set => SetProperty(ref rows, value); }
    /// <summary>Top-level rows; every row of the tree is reachable through <see cref="PropertyRow.Children"/>.</summary>
    public IReadOnlyList<PropertyRow> Roots => roots;
    /// <summary>Number of rows of the tree, expanded or not.</summary>
    public int RowCount { get; private set; }
    /// <summary>No selection: the VST is disabled and empty (TZXDATA 8.5).</summary>
    public bool IsEnabled { get => isEnabled; private set => SetProperty(ref isEnabled, value); }
    /// <summary>The template of the selection: its caption, «Разное» when templates differ, «нет» without one (TZXDATA 8.4).</summary>
    public string TemplateText { get => templateText; private set => SetProperty(ref templateText, value); }
    /// <summary>Message above the tree: no selection, objects without <c>BobrovXDATA</c>, no schema.</summary>
    public string Message { get => message; private set => SetProperty(ref message, value); }
    /// <summary>The selection has objects without <c>BobrovXDATA</c>: «Назначить шаблон» is offered (TZXDATA 8.5).</summary>
    public bool NeedsTemplate { get => needsTemplate; private set => SetProperty(ref needsTemplate, value); }
    /// <summary>Filter by <c>Caption</c>, <c>Id</c> or value; rows that do not match are hidden (TZXDATA 8.4).</summary>
    public string Filter
    {
        get => filter;
        set
        {
            value ??= "";
            if (filter == value) return;
            SetProperty(ref filter, value);
            Flatten();
        }
    }
    /// <summary>Visibility of columns 0, 3 and 4 (the header context menu, TZXDATA 8.2).</summary>
    public bool ShowActionsColumn { get => showActions; set => SetProperty(ref showActions, value); }
    public bool ShowStateColumn { get => showState; set => SetProperty(ref showState, value); }
    public bool ShowNoteColumn { get => showNote; set => SetProperty(ref showNote, value); }
    /// <summary>
    /// Vertical scroll position; saved per template so that a new selection of the same template keeps it (TZXDATA 8.5).
    /// </summary>
    public double ScrollOffset
    {
        get => offsets.TryGetValue(templateKey, out var offset) ? offset : 0;
        set { if (ScrollOffset == value) return; offsets[templateKey] = value; Notify(nameof(ScrollOffset)); }
    }
    /// <summary>Templates of the selection: the key of the saved expansion and scroll position.</summary>
    public string TemplateKey => templateKey;
    /// <summary>Duration of the last build of the tree (TZXDATA 10.6).</summary>
    public TimeSpan BuildTime { get; private set; }
    /// <summary>One line for <c>XDATAPALETTE_TRACE=1</c> about the last build.</summary>
    public string BuildSummary => "VST: объектов " + selection.Entities.Count + ", строк " + RowCount + ", видимых " + Rows.Count + "; " +
        BuildTime.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " мс.";

    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand ToggleCommand { get; }
    /// <summary>⟳: re-reads the selection and recalculates <c>CalculatedString</c>; rebuilds the tree without a source.</summary>
    public ICommand RefreshCommand { get; }
    /// <summary>«Назначить шаблон» runs <c>VELEC_TEMPLATE_APPLY</c> from stage 7; reserved until then.</summary>
    public ICommand AssignTemplateCommand { get; }

    /// <summary>Raised after every build of the tree, e.g. for the trace of the AutoCAD host.</summary>
    public event EventHandler? Built;

    public void Refresh()
    {
        if (!selection.RequestRefresh()) Rebuild();
    }

    /// <summary>Expands or collapses a group, category or table; the state is saved for the template of the selection.</summary>
    public void Toggle(PropertyRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (!row.IsExpandable) return;
        row.IsExpanded = !row.IsExpanded;
        Saved()[row.Key] = row.IsExpanded;
        var index = Rows.IndexOf(row);
        if (filter.Length != 0 || index < 0) { Flatten(); return; }
        if (row.IsExpanded)
        {
            var shown = new List<PropertyRow>();
            foreach (var child in row.children) Visible(child, shown);
            for (var i = 0; i < shown.Count; i++) Rows.Insert(index + 1 + i, shown[i]);
        }
        else
            while (index + 1 < Rows.Count && Rows[index + 1].Level > row.Level) Rows.RemoveAt(index + 1);
    }

    /// <summary>Finds a row of the tree by its <see cref="PropertyRow.Key"/>.</summary>
    public PropertyRow? Find(string key) => All(roots).FirstOrDefault(row => row.Key == key);

    private void SetAllExpanded(bool expanded)
    {
        var saved = Saved();
        foreach (var row in All(roots).Where(row => row.IsExpandable))
        {
            row.IsExpanded = expanded;
            saved[row.Key] = expanded;
        }
        Flatten();
    }

    private Dictionary<string, bool> Saved()
    {
        if (!expansion.TryGetValue(templateKey, out var saved)) expansion[templateKey] = saved = new Dictionary<string, bool>(StringComparer.Ordinal);
        return saved;
    }

    private static IEnumerable<PropertyRow> All(IEnumerable<PropertyRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            foreach (var child in All(row.children)) yield return child;
        }
    }

    private void Rebuild()
    {
        var timer = Stopwatch.StartNew();
        var build = new Builder(this, selection);
        var built = build.Run();
        templateKey = build.TemplateKey;
        TemplateText = build.TemplateText;
        NeedsTemplate = build.NeedsTemplate;
        Message = build.Message;
        IsEnabled = selection.Entities.Count != 0;
        var saved = Saved();
        var count = 0;
        foreach (var row in All(built))
        {
            count++;
            if (row.IsExpandable) row.IsExpanded = saved.TryGetValue(row.Key, out var expanded) ? expanded : row.DefaultExpanded;
        }
        roots = built;
        RowCount = count;
        Flatten();
        BuildTime = timer.Elapsed;
        Notify(nameof(Roots));
        Notify(nameof(RowCount));
        Notify(nameof(TemplateKey));
        Notify(nameof(ScrollOffset));
        Built?.Invoke(this, EventArgs.Empty);
    }

    private void Flatten()
    {
        var shown = new List<PropertyRow>();
        if (filter.Length == 0) foreach (var row in roots) Visible(row, shown);
        else foreach (var row in roots) Filtered(row, false, shown);
        Rows = new ObservableCollection<PropertyRow>(shown);
    }

    private static void Visible(PropertyRow row, List<PropertyRow> shown)
    {
        shown.Add(row);
        if (!row.IsExpanded) return;
        foreach (var child in row.children) Visible(child, shown);
    }

    /// <summary>A matching row shows its subtree as expanded; ancestors of matches are shown expanded.</summary>
    private bool Filtered(PropertyRow row, bool parentMatches, List<PropertyRow> shown)
    {
        if (parentMatches) { Visible(row, shown); return true; }
        if (Matches(row)) { Visible(row, shown); return true; }
        var index = shown.Count;
        shown.Add(row);
        var any = false;
        foreach (var child in row.children) any |= Filtered(child, false, shown);
        if (!any) shown.RemoveAt(index);
        return any;
    }

    private bool Matches(PropertyRow row) =>
        Contains(row.Caption) || Contains(row.Id) || !row.IsMixed && Contains(row.Value);

    private bool Contains(string? text) => text != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Russian plural of a row counter: «1 строка», «3 строки», «10 строк».</summary>
    public static string RowsText(int count)
    {
        var tens = count % 100;
        var ones = count % 10;
        var word = tens >= 11 && tens <= 14 ? "строк" : ones == 1 ? "строка" : ones >= 2 && ones <= 4 ? "строки" : "строк";
        return count.ToString(CultureInfo.InvariantCulture) + " " + word;
    }

    /// <summary>One build of the tree from <see cref="SelectionViewModel.Values"/> and <see cref="SelectionViewModel.Calculations"/>.</summary>
    private sealed class Builder
    {
        private readonly PropertyGridViewModel owner;
        private readonly SelectionViewModel selection;
        private readonly IReadOnlyList<ObjectValues> values;
        private readonly IReadOnlyList<ObjectCalculation> calculations;
        private readonly SchemaSnapshot? schema;

        public Builder(PropertyGridViewModel owner, SelectionViewModel selection)
        {
            this.owner = owner;
            this.selection = selection;
            // The cache gives the values in the order of the entities; anything else is a selection without a schema source.
            values = selection.Values.Count == selection.Entities.Count ? selection.Values : Array.Empty<ObjectValues>();
            calculations = selection.Calculations.Count == values.Count ? selection.Calculations : values.Select(item => new ObjectCalculation(item, owner.culture, owner.types)).ToArray();
            schema = values.Count != 0 ? values[0].Schema : null;
        }

        public string TemplateKey { get; private set; } = "";
        public string TemplateText { get; private set; } = "";
        public bool NeedsTemplate { get; private set; }
        public string Message { get; private set; } = "";

        private CultureInfo Culture => owner.culture;
        private ParameterTypeRegistry Types => owner.types;

        public IReadOnlyList<PropertyRow> Run()
        {
            var result = new List<PropertyRow>();
            var count = selection.Entities.Count;
            if (count == 0) { Message = "Выберите объекты в чертеже"; return result; }
            Templates();
            if (values.Count != 0) Schema(result);
            Legacy(result);
            var none = values.Count(item => item.Format == XDataFormat.None && !HasLegacy(item.Handle));
            NeedsTemplate = none != 0;
            if (values.Count == 0) Message = result.Count == 0 ? "Значения BobrovXDATA v2 недоступны: нет схемы параметров чертежа." : "";
            else if (schema?.Schema is null) Message = "Схема параметров не загружена (VELEC_SCHEMA_IMPORT): значения показаны как строки.";
            else if (none == count) Message = "Нет данных " + DrawingNames.RegApp + ": назначьте шаблон.";
            else if (none != 0) Message = "Объектов без " + DrawingNames.RegApp + ": " + none + " — их значения показаны как «Разное».";
            return result;
        }

        private void Templates()
        {
            if (values.Count == 0) { TemplateKey = "-"; TemplateText = "нет"; return; }
            var ids = values.Select(item => item.Template ?? "").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            TemplateKey = string.Join("|", ids).ToUpperInvariant();
            if (ids.Length != 1) { TemplateText = ObjectCalculation.Various; return; }
            var id = ids[0];
            if (id.Length == 0) { TemplateText = "нет"; return; }
            TemplateText = schema != null && schema.Templates.TryGetValue(id, out var template) && !string.IsNullOrEmpty(template.Caption) ? template.Caption! : id;
        }

        private void Schema(List<PropertyRow> result)
        {
            // Parameter order: templates first, then stored values, in the order of the objects.
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var storedWithTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in values)
            {
                if (item.Template != null && schema!.Templates.TryGetValue(item.Template, out var template))
                    foreach (var parameter in template.Parameters)
                        if (!string.IsNullOrEmpty(parameter.Id))
                        {
                            inTemplate.Add(parameter.Id!);
                            if (seen.Add(parameter.Id!)) order.Add(parameter.Id!);
                        }
                foreach (var value in item.Values)
                {
                    if (item.Template != null) storedWithTemplate.Add(value.Id);
                    if (seen.Add(value.Id)) order.Add(value.Id);
                }
            }
            var groups = new List<(string Name, List<string> Ids)>();
            var extra = new List<string>();
            var outside = new List<string>();
            foreach (var id in order)
            {
                var parameter = schema!.Parameters.TryGetValue(id, out var found) ? found : null;
                if (parameter is null) { outside.Add(id); continue; }
                if (!inTemplate.Contains(id) && storedWithTemplate.Contains(id)) { extra.Add(id); continue; }
                var name = parameter.EffectiveGroup;
                var group = groups.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (group.Ids is null) groups.Add(group = (name, new List<string>()));
                group.Ids.Add(id);
            }
            foreach (var group in groups) result.Add(Group("g:" + group.Name, group.Name, group.Ids));
            if (extra.Count != 0) result.Add(Group("g:" + GroupExtra, GroupExtra, extra));
            if (outside.Count != 0)
            {
                var node = Group("g:" + GroupNotInSchema, GroupNotInSchema + " (" + outside.Count + ")", outside);
                node.DefaultExpanded = false;
                node.State = PropertyState.Error;
                node.StateText = PropertyRow.Symbol(PropertyState.Error);
                node.Note = "удаление — этап 6";
                node.ToolTip = "Id есть в XDATA объекта, но не описан в схеме NOD (TZXDATA 6.5).";
                result.Add(node);
            }
        }

        /// <summary>A group of one table with the same caption is shown as the table node itself (TZXDATA 8.1).</summary>
        private PropertyRow Group(string key, string name, List<string> ids)
        {
            if (ids.Count == 1 && schema!.Parameters.TryGetValue(ids[0], out var only) && Kind(only) == ParameterKind.Table &&
                string.Equals(Caption(only), name, StringComparison.OrdinalIgnoreCase))
                return Table(ids[0], only, 0, "");
            var group = new PropertyRow(PropertyRowKind.Group, key, name, 0);
            foreach (var id in ids)
            {
                var parameter = schema!.Parameters.TryGetValue(id, out var found) ? found : null;
                group.children.Add(parameter != null && Kind(parameter) == ParameterKind.Table || parameter is null && IsList(id)
                    ? Table(id, parameter, 1, key + "/")
                    : Scalar(id, parameter, 1));
            }
            return group;
        }

        private bool IsList(string id) => values.Any(item => item.Find(id)?.IsTable == true);

        private ParameterKind? Kind(SchemaParameter parameter) => Types.Find(parameter.Type)?.Kind;

        private static string Caption(SchemaParameter parameter) => string.IsNullOrEmpty(parameter.Caption) ? parameter.Id! : parameter.Caption!;

        private static string CaptionWithUnit(SchemaParameter parameter)
        {
            var unit = parameter.Editor?.Unit;
            return Caption(parameter) + (string.IsNullOrEmpty(unit) ? "" : ", " + unit);
        }

        private PropertyRow Scalar(string id, SchemaParameter? parameter, int level)
        {
            var row = new PropertyRow(PropertyRowKind.Parameter, "p:" + id.ToUpperInvariant(), parameter is null ? id : CaptionWithUnit(parameter), level) { Id = parameter?.Id ?? id };
            var formula = parameter is null ? null : schema!.Formulas.Find(id);
            string? common = null;
            var mixed = false;
            var state = PropertyState.None;
            string? error = null;
            var allDefault = true;
            for (var i = 0; i < values.Count; i++)
            {
                var item = values[i];
                var calculation = calculations[i];
                string? text = null;
                var itemState = PropertyState.None;
                var stored = item.Find(id);
                if (formula != null && calculation.IsUsed(id))
                {
                    var result = calculation.Find(id);
                    text = result?.Text ?? "";
                    itemState = result?.IsError == true ? PropertyState.Error : PropertyState.Calculated;
                    if (result?.IsError == true) error ??= result.Error;
                    else if (result?.IsStale == true) error ??= "записано «" + result.Stored + "»; обновится при изменении исходных параметров";
                }
                else if (stored != null)
                {
                    if (stored.State == ObjectValueState.Typed && parameter != null)
                    {
                        text = ObjectCalculation.FormatValue(schema!, parameter, stored.Value, false, Culture, Types);
                        itemState = PropertyState.Set;
                    }
                    else
                    {
                        text = stored.Text;
                        itemState = PropertyState.Error;
                    }
                    if (stored.Warning != null) { itemState = PropertyState.Error; error ??= stored.Warning; }
                }
                else if (parameter != null && calculation.IsUsed(id))
                {
                    text = ObjectCalculation.FormatValue(schema!, parameter, calculation.Value(id), false, Culture, Types);
                    itemState = PropertyState.Default;
                }
                if (itemState != PropertyState.Default) allDefault = false;
                state = Worse(state, itemState);
                if (text is null) mixed = true;
                else if (i == 0) common = text;
                else if (!string.Equals(common, text, StringComparison.Ordinal)) mixed = true;
            }
            row.Value = mixed ? ObjectCalculation.Various : common ?? "";
            row.IsMixed = mixed;
            row.State = state;
            row.StateText = PropertyRow.Symbol(state);
            row.IsDefault = !mixed && allDefault && state == PropertyState.Default;
            row.Note = error ?? (allDefault && state == PropertyState.Default ? DefaultNote : parameter?.Description ?? (parameter is null ? "не описан в схеме" : ""));
            row.ToolTip = Tip(row.Id!, parameter, error);
            return row;
        }

        /// <summary>Priority of the merged state: an error, then a stored, a calculated and a default value.</summary>
        private static PropertyState Worse(PropertyState left, PropertyState right)
        {
            static int Rank(PropertyState state) => state switch
            {
                PropertyState.Error => 4,
                PropertyState.Set => 3,
                PropertyState.Calculated => 2,
                PropertyState.Default => 1,
                _ => 0
            };
            return Rank(right) > Rank(left) ? right : left;
        }

        private static string Tip(string id, SchemaParameter? parameter, string? error)
        {
            var lines = new List<string> { id + (parameter?.Type is null ? "" : " (" + parameter.Type + ")") };
            if (!string.IsNullOrEmpty(parameter?.Description)) lines.Add(parameter!.Description!);
            if (!string.IsNullOrEmpty(parameter?.Format)) lines.Add(parameter!.Format!);
            if (error != null) lines.Add(error);
            return string.Join("\n", lines);
        }

        private PropertyRow Table(string id, SchemaParameter? parameter, int level, string prefix)
        {
            var key = prefix + "t:" + id.ToUpperInvariant();
            var node = new PropertyRow(PropertyRowKind.Table, key, parameter is null ? id : CaptionWithUnit(parameter), level) { Id = parameter?.Id ?? id };
            var columns = Columns(id, parameter);
            var perObject = new List<IReadOnlyList<RowData>?>();
            var state = PropertyState.None;
            string? error = null;
            for (var i = 0; i < values.Count; i++)
            {
                var stored = values[i].Find(id);
                if (stored?.Rows != null)
                {
                    perObject.Add(stored.Rows.Select(row => RowOf(row, columns)).ToArray());
                    state = Worse(state, stored.State == ObjectValueState.Typed && stored.Warning is null ? PropertyState.Set : PropertyState.Error);
                    error ??= stored.Warning;
                }
                else if (parameter != null && calculations[i].IsUsed(id))
                {
                    perObject.Add(Array.Empty<RowData>());
                    state = Worse(state, PropertyState.Default);
                }
                else perObject.Add(null);
            }
            Merge(node, perObject, level + 1, key);
            node.State = state;
            node.StateText = PropertyRow.Symbol(state);
            node.Note = error ?? "";
            node.ToolTip = Tip(node.Id!, parameter, error) + (columns.Names.Count == 0 ? "" : "\nКолонки: " + string.Join(", ", columns.Names));
            if (parameter is null) node.Note = error ?? "не описан в схеме";
            node.ShowAdd = parameter != null;
            return node;
        }

        /// <summary>Columns of a table mapped to the VST columns 1–4 by <c>Editor.VstColumn</c> (TZXDATA 8.2).</summary>
        private sealed class TableColumns
        {
            public string? GroupBy;
            public SchemaParameter? GroupByParameter;
            public string?[] Ids = new string?[5];
            public SchemaParameter?[] Parameters = new SchemaParameter?[5];
            public bool FlagColumn;
            public List<string> Names = new List<string>();
            public List<string> All = new List<string>();
        }

        private TableColumns Columns(string id, SchemaParameter? table)
        {
            var columns = new TableColumns();
            var ids = new List<string>();
            if (table?.Columns != null) ids.AddRange(table.Columns.Where(column => !string.IsNullOrEmpty(column)));
            // Cells outside the declared columns (or of a list outside the schema) are mapped after them.
            foreach (var item in values)
                if (item.Find(id)?.Rows is IReadOnlyList<ObjectRow> rows)
                    foreach (var cell in rows.SelectMany(row => row.Cells))
                        if (!ids.Contains(cell.Id, StringComparer.OrdinalIgnoreCase)) ids.Add(cell.Id);
            columns.All = ids;
            SchemaParameter? Parameter(string column) => schema!.Parameters.TryGetValue(column, out var found) ? found : null;
            columns.Names = ids.Select(column => Parameter(column) is SchemaParameter found ? Caption(found) : column).ToList();
            if (table != null && !string.IsNullOrEmpty(table.GroupBy) && ids.Contains(table.GroupBy!, StringComparer.OrdinalIgnoreCase))
            {
                columns.GroupBy = table.GroupBy;
                columns.GroupByParameter = Parameter(table.GroupBy!);
            }
            var free = ids.Where(column => !ParameterSchema.SameId(column, columns.GroupBy)).ToList();
            void Assign(int index, string column)
            {
                columns.Ids[index] = column;
                columns.Parameters[index] = Parameter(column);
                free.RemoveAll(item => ParameterSchema.SameId(item, column));
            }
            foreach (var column in free.ToArray())
                if (Parameter(column)?.Editor?.VstColumn is int vst && vst >= 1 && vst <= 4 && columns.Ids[vst] is null) Assign(vst, column);
            var keyColumn = table is null ? null : XDataV2.KeyColumn(table, schema!, Types);
            if (columns.Ids[1] is null)
            {
                var first = free.FirstOrDefault(column => ParameterSchema.SameId(column, keyColumn)) ?? free.FirstOrDefault();
                if (first != null) Assign(1, first);
            }
            if (columns.Ids[3] is null && free.FirstOrDefault(column => Parameter(column) is SchemaParameter found && Kind(found) == ParameterKind.Boolean) is string flag)
                Assign(3, flag);
            if (columns.Ids[2] is null && free.FirstOrDefault() is string second) Assign(2, second);
            if (columns.Ids[4] is null && free.FirstOrDefault() is string fourth) Assign(4, fourth);
            columns.FlagColumn = columns.Parameters[3] is SchemaParameter boolean && Kind(boolean) == ParameterKind.Boolean;
            return columns;
        }

        /// <summary>Texts of one stored table row.</summary>
        private sealed class RowData
        {
            public string? Key;
            public string Category = NoCategory;
            public string[] Texts = { "", "", "", "", "" };
            public bool? Flag;
            public string Tip = "";
        }

        private RowData RowOf(ObjectRow row, TableColumns columns)
        {
            var data = new RowData { Key = row.Key };
            string Text(ObjectValue? cell, SchemaParameter? parameter) =>
                cell is null ? "" : cell.State == ObjectValueState.Typed && parameter != null && !cell.IsTable
                    ? ObjectCalculation.FormatValue(schema!, parameter, cell.Value, false, Culture, Types) : cell.Text;
            for (var i = 1; i <= 4; i++)
                if (columns.Ids[i] is string column) data.Texts[i] = Text(row.Find(column), columns.Parameters[i]);
            if (columns.FlagColumn && columns.Ids[3] is string flag && row.Find(flag) is ObjectValue cell && cell.Value is bool value) data.Flag = value;
            if (columns.GroupBy != null)
            {
                var category = Text(row.Find(columns.GroupBy), columns.GroupByParameter);
                data.Category = category.Length == 0 ? NoCategory : category;
            }
            data.Tip = string.Join("\n", columns.All.Select((column, index) => columns.Names[index] + ": " + Text(row.Find(column), schema!.Parameters.TryGetValue(column, out var found) ? found : null)));
            return data;
        }

        /// <summary>
        /// Rows of all objects matched by the key column (TZXDATA 4.2), or by position without a key; a row missing on an
        /// object, or a differing cell, gives «Разное».
        /// </summary>
        private void Merge(PropertyRow node, IReadOnlyList<IReadOnlyList<RowData>?> perObject, int level, string key)
        {
            var merged = new List<(string Key, RowData?[] Rows)>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < perObject.Count; i++)
            {
                if (perObject[i] is null) continue;
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var r = 0; r < perObject[i]!.Count; r++)
                {
                    var row = perObject[i]![r];
                    var name = row.Key is null ? "#" + r.ToString(CultureInfo.InvariantCulture) : "k:" + row.Key;
                    occurrences[name] = occurrences.TryGetValue(name, out var seen) ? seen + 1 : 0;
                    if (occurrences[name] != 0) name += "#" + occurrences[name].ToString(CultureInfo.InvariantCulture);
                    if (!index.TryGetValue(name, out var position))
                    {
                        index[name] = position = merged.Count;
                        merged.Add((name, new RowData?[perObject.Count]));
                    }
                    merged[position].Rows[i] = row;
                }
            }
            var present = perObject.All(rows => rows != null);
            var counts = perObject.Where(rows => rows != null).Select(rows => rows!.Count).Distinct().ToArray();
            node.IsMixed = !present || counts.Length != 1 || merged.Count != counts[0];
            node.Value = node.IsMixed ? ObjectCalculation.Various : RowsText(merged.Count);
            node.DefaultExpanded = true;
            var categories = new List<PropertyRow>();
            foreach (var item in merged)
            {
                var all = present && item.Rows.All(row => row != null);
                string Field(Func<RowData, string> get, out bool mixed)
                {
                    var texts = item.Rows.Where(row => row != null).Select(row => get(row!)).Distinct(StringComparer.Ordinal).ToArray();
                    mixed = !all || texts.Length != 1;
                    return mixed ? ObjectCalculation.Various : texts[0];
                }
                var category = Field(row => row.Category, out _);
                var parent = node;
                var rowLevel = level;
                if (item.Rows.First(row => row != null)!.Category != NoCategory || merged.Any(other => other.Rows.Any(row => row != null && row.Category != NoCategory)))
                {
                    parent = categories.FirstOrDefault(row => row.Caption == category)!;
                    if (parent is null)
                    {
                        parent = new PropertyRow(PropertyRowKind.Category, key + "/c:" + category, category, level);
                        categories.Add(parent);
                        node.children.Add(parent);
                    }
                    rowLevel = level + 1;
                }
                var first = Field(row => row.Texts[1], out var captionMixed);
                var row = new PropertyRow(PropertyRowKind.TableRow, parent.Key + "/r:" + item.Key, first, rowLevel)
                {
                    IsCaptionMixed = captionMixed
                };
                row.Value = Field(data => data.Texts[2], out var valueMixed);
                row.IsMixed = valueMixed;
                row.Note = Field(data => data.Texts[4], out var noteMixed);
                row.IsNoteMixed = noteMixed;
                var stateText = Field(data => data.Texts[3], out _);
                var flags = item.Rows.Where(data => data != null).Select(data => data!.Flag).Distinct().ToArray();
                row.HasFlag = item.Rows.Any(data => data != null && data.Flag != null);
                row.Flag = all && flags.Length == 1 ? flags[0] : null;
                if (!row.HasFlag) row.StateText = stateText == ObjectCalculation.Various ? "…" : stateText;
                row.ToolTip = string.Join("\n\n", item.Rows.Where(data => data != null).Select(data => data!.Tip).Distinct(StringComparer.Ordinal));
                parent.children.Add(row);
            }
            foreach (var category in categories) category.Value = RowsText(category.children.Count);
        }

        private bool HasLegacy(string handle) =>
            selection.Entities.Any(entity => string.Equals(entity.Handle, handle, StringComparison.OrdinalIgnoreCase) && (entity.Properties.Count != 0 || entity.Materials.Count != 0));

        /// <summary>
        /// Values of the earlier formats (<c>ESMT_LEP_v1.0</c>, v1 <c>key=value</c>, XML) are shown by their keys until
        /// <c>VELEC_MIGRATE</c> (stage 9) moves them to v2: nothing is lost from the view of the earlier stages.
        /// </summary>
        private void Legacy(List<PropertyRow> result)
        {
            var entities = selection.Entities;
            if (!entities.Any(entity => entity.Properties.Count != 0 || entity.Materials.Count != 0)) return;
            var group = new PropertyRow(PropertyRowKind.Group, "g:" + GroupLegacy, GroupLegacy, 0)
            {
                Note = "перенос в v2 — VELEC_MIGRATE",
                ToolTip = "Данные ESMT_LEP_v1.0, BobrovXDATA v1 (ключ=значение) и XML XRecord. Перенос в BobrovXDATA v2 — команда VELEC_MIGRATE (этап 9)."
            };
            var keys = entities.SelectMany(entity => entity.Properties.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var key in keys)
            {
                var texts = entities.Select(entity => entity.Properties.TryGetValue(key, out var value) ? value : null).ToArray();
                var mixed = texts.Any(text => text is null) || texts.Distinct(StringComparer.Ordinal).Count() != 1;
                group.children.Add(new PropertyRow(PropertyRowKind.Parameter, group.Key + "/p:" + key, key, 1)
                {
                    Id = key,
                    Value = mixed ? ObjectCalculation.Various : texts[0]!,
                    IsMixed = mixed,
                    State = PropertyState.Set,
                    StateText = PropertyRow.Symbol(PropertyState.Set),
                    ToolTip = key
                });
            }
            if (entities.Any(entity => entity.Materials.Count != 0))
            {
                var node = new PropertyRow(PropertyRowKind.Table, group.Key + "/t:Materials", "Materials", 1)
                {
                    ToolTip = "Материалы прежнего формата: категория, марка, количество, в спецификацию, примечание."
                };
                var perObject = entities.Select(entity => (IReadOnlyList<RowData>?)entity.Materials.Select(material => new RowData
                {
                    Key = material.MaterialKey.Length != 0 ? material.MaterialKey : material.Name,
                    Category = string.IsNullOrEmpty(material.Category) ? NoCategory : material.Category,
                    Texts = new[] { "", material.Name, material.Count, "", material.Comment },
                    Flag = material.IsInSpec,
                    Tip = "Category: " + material.Category + "\nName: " + material.Name + "\nCount: " + material.Count +
                          "\nIsInSpec: " + (material.IsInSpec switch { true => "1", false => "0", _ => "" }) + "\nComment: " + material.Comment
                }).ToArray()).ToArray();
                Merge(node, perObject, 2, node.Key);
                group.children.Add(node);
            }
            result.Add(group);
        }
    }
}
