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
    TableRow,
    /// <summary>A cell of a table row opened by <c>✎</c>: one row per table column (stage 6).</summary>
    Cell
}

/// <summary>Editor of column 2 (TZXDATA 8.3); the view creates it only for the cell in focus.</summary>
public enum PropertyEditorKind
{
    /// <summary>Read only: groups, <c>CalculatedString</c>, <c>Editor.ReadOnly</c>, values outside the schema, the former format.</summary>
    None,
    /// <summary><c>String</c>: a text field.</summary>
    Text,
    /// <summary><c>Double</c>/<c>Integer</c>: a text field with the number, <c>Min/Max</c> check and the unit.</summary>
    Number,
    /// <summary><c>Enum</c>: a drop-down list of captions, with typing when <c>AllowCustom</c>.</summary>
    Choice,
    /// <summary><c>Boolean</c>: a check box, three-state for «Разное».</summary>
    Flag
}

/// <summary>An item of a drop-down list: an <c>Enum</c> item or a parameter of «+ Параметр».</summary>
public sealed class PropertyOption
{
    public PropertyOption(string key, string caption) { Key = key; Caption = caption; }
    public string Key { get; }
    public string Caption { get; }
    public override string ToString() => Caption;
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

/// <summary>A row of the VST: the texts of columns 1–4, its place in the tree and the editor of its value.</summary>
public sealed class PropertyRow : ObservableObject
{
    private bool isExpanded;
    private bool isEditing;
    private bool isDirty;
    private string editText = "";

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
    /// <summary>Column 0: <c>×</c> and <c>✎</c> of a table row, <c>×</c> of a value outside the template or the schema.</summary>
    public bool ShowActions => Kind == PropertyRowKind.TableRow || CanDelete;
    /// <summary>Column 0: <c>✎</c> of a table row.</summary>
    public bool ShowEdit => Kind == PropertyRowKind.TableRow;
    /// <summary>Column 4 of a table node or category: «+ Добавить».</summary>
    public bool ShowAdd { get; internal set; }

    /// <summary>Editor of column 2 (TZXDATA 8.3); <see cref="PropertyEditorKind.None"/> when the value is read only.</summary>
    public PropertyEditorKind Editor { get; internal set; }
    public bool IsEditable => Editor != PropertyEditorKind.None;
    public bool IsTextEditor => Editor == PropertyEditorKind.Text || Editor == PropertyEditorKind.Number;
    public bool IsChoiceEditor => Editor == PropertyEditorKind.Choice;
    /// <summary>Column 2 is a check box: a <c>Boolean</c> parameter or cell; <see cref="Flag"/> is its value.</summary>
    public bool IsFlagEditor => Editor == PropertyEditorKind.Flag;
    /// <summary>Items of an <c>Enum</c>: the list shows captions, the key is written.</summary>
    public IReadOnlyList<PropertyOption> Options { get; internal set; } = Array.Empty<PropertyOption>();
    /// <summary><c>Editor.AllowCustom</c>: a value outside the list can be typed.</summary>
    public bool AllowCustom { get; internal set; }
    /// <summary><c>Editor.Unit</c>, shown next to a number editor.</summary>
    public string Unit { get; internal set; } = "";
    /// <summary>
    /// Text of the editor. It starts with the value, or empty for «Разное»; a different text marks the row changed, so a
    /// visit without typing writes nothing (also when a control sets the same text back), while an empty text or the word
    /// «Разное» typed by the user are data (stage 4).
    /// </summary>
    public string EditText
    {
        get => editText;
        set
        {
            if (string.Equals(editText, value ?? "", StringComparison.Ordinal)) return;
            IsDirty = true;
            SetProperty(ref editText, value ?? "");
        }
    }
    /// <summary>
    /// The editor text was changed since the value was shown: Enter or leaving the cell writes it. A changed empty text
    /// of «Разное» is data, so the editor shows no «Разное» placeholder then.
    /// </summary>
    public bool IsDirty { get => isDirty; private set => SetProperty(ref isDirty, value); }
    /// <summary>The check box of column 2 (<see cref="IsFlagEditor"/>) or 3 (<see cref="HasFlag"/>) is applied by a click.</summary>
    public bool CanSetFlag { get; internal set; }
    /// <summary><c>×</c>: removes a table row in every object, or a value outside the template or the schema.</summary>
    public bool CanDelete { get; internal set; }
    /// <summary><c>✎</c>: shows the cells of a table row as child rows with editors.</summary>
    public bool CanEditRow { get; internal set; }
    /// <summary>«+ Добавить» adds a row to the table in every object.</summary>
    public bool CanAdd { get; internal set; }
    /// <summary>The cells of the table row are shown (<c>✎</c>).</summary>
    public bool IsEditing { get => isEditing; internal set => SetProperty(ref isEditing, value); }
    /// <summary><c>Id</c> of the table of a table node, category, row or cell.</summary>
    public string? Table { get; internal set; }
    /// <summary>The key column text of a table row (TZXDATA 4.2); null for a row without a key.</summary>
    public string? RowKey { get; internal set; }
    /// <summary>The table column edited in column 2 of a table row or cell.</summary>
    public string? Column { get; internal set; }

    internal SchemaParameter? Parameter;
    internal string InitialText = "";
    internal string? FlagColumn;
    internal object? CategoryValue;
    /// <summary>Key of the table node; with <see cref="RowKey"/> it names a row when its category changes.</summary>
    internal string? TableKey;
    internal HashSet<string>? RowKeys;
    internal string? KeyColumn;
    /// <summary>The <c>GroupBy</c> column of a category: a row added to it gets <see cref="CategoryValue"/>.</summary>
    internal string? CategoryColumn;
    /// <summary>Builds the cells shown by <c>✎</c>; called again after every rebuild while the row stays open.</summary>
    internal Func<IEnumerable<PropertyRow>>? Cells;
    /// <summary>The build of the tree the row belongs to: a row of an earlier build is not written (a late commit of its editor).</summary>
    internal int Build;
    internal string Identity => Kind switch
    {
        PropertyRowKind.TableRow when RowKey != null => TableKey + "/r:" + RowKey,
        PropertyRowKind.Cell when RowKey != null => TableKey + "/r:" + RowKey + "/f:" + Column,
        _ => Key
    };

    /// <summary>Shows the value again in the editor; the row is not changed.</summary>
    internal void ResetEdit()
    {
        IsDirty = false;
        SetProperty(ref editText, InitialText, nameof(EditText));
    }

    internal void Changed(string propertyName) => Notify(propertyName);

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
/// Model of the «Свойства» tab (TZXDATA 8): a VST built from the value cache of the selection. Rows of a multiple
/// selection are the union of parameters; differing or missing values are «Разное». With a writable source the values
/// are edited (stage 6, TZXDATA 8.3–8.5): a confirmed change of one value is written to every object in one transaction.
/// </summary>
public sealed class PropertyGridViewModel : ObservableObject
{
    public const string GroupNotInSchema = "Не описаны в схеме";
    public const string GroupExtra = "Дополнительные";
    public const string GroupLegacy = "Прежний формат (только чтение)";
    public const string NoCategory = "Без категории";
    public const string DefaultNote = "по умолчанию";
    public const string NewRowKey = "Новая строка";

    private readonly SelectionViewModel selection;
    private readonly CultureInfo culture;
    private readonly ParameterTypeRegistry types;
    private readonly Dictionary<string, Dictionary<string, bool>> expansion = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
    private readonly Dictionary<string, double> offsets = new Dictionary<string, double>(StringComparer.Ordinal);
    /// <summary>Table rows opened by <c>✎</c>, by <see cref="PropertyRow.Identity"/>: they stay open after a write.</summary>
    private readonly HashSet<string> editing = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlyList<PropertyRow> roots = Array.Empty<PropertyRow>();
    /// <summary>Number of the tree in <see cref="roots"/>, see <see cref="PropertyRow.Build"/>.</summary>
    private int generation;
    private ObservableCollection<PropertyRow> rows = new ObservableCollection<PropertyRow>();
    private IReadOnlyList<PropertyOption> available = Array.Empty<PropertyOption>();
    private IReadOnlyList<PropertyOption> templates = Array.Empty<PropertyOption>();
    private SchemaSnapshot? schema;
    private PropertyRow? selectedRow;
    private string? selectedIdentity;
    /// <summary>The ancestors of this row are expanded by the next build: a row or parameter just added.</summary>
    private string? reveal;
    private string templateKey = "";
    private string templateText = "";
    private string message = "Выберите объекты в чертеже";
    private string filter = "";
    private bool isEnabled;
    private bool canEdit;
    private bool hasError;
    private bool needsTemplate;
    private bool hasTemplate;
    private bool hasDefaults;
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
        AssignTemplateCommand = new DelegateCommand(
            option => AssignTemplate(option is PropertyOption item ? item.Key : option as string ?? ""),
            option => CanEdit && (option is PropertyOption || option is string));
        CommitDefaultsCommand = new DelegateCommand(_ => CommitDefaults(), _ => CanEdit && HasDefaults);
        DeleteRowCommand = new DelegateCommand(row => { if (row is PropertyRow item) DeleteRow(item); }, row => row is PropertyRow item && CanEdit && item.CanDelete);
        EditRowCommand = new DelegateCommand(row => { if (row is PropertyRow item) EditRow(item); }, row => row is PropertyRow item && item.CanEditRow);
        AddRowCommand = new DelegateCommand(row => { if (row is PropertyRow item) AddRow(item); }, row => row is PropertyRow item && CanEdit && item.CanAdd);
        AddParameterCommand = new DelegateCommand(
            option => AddParameter(option is PropertyOption item ? item.Key : option as string ?? ""),
            option => CanEdit && (option is null || option is PropertyOption || option is string));
        SchemaCommand = new DelegateCommand(command => RunSchemaCommand(command as string ?? SchemaEdit), _ => CanRunSchemaCommands);
        selection.Applied += (_, _) => Rebuild();
        selection.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectionViewModel.CanWrite)) Rebuild(); };
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
    /// <summary>
    /// Values can be changed: the source writes, the schema is loaded and every object is v2 or without data (stage 6).
    /// </summary>
    public bool CanEdit { get => canEdit; private set => SetProperty(ref canEdit, value); }
    /// <summary>The template of the selection: its caption, «Разное» when templates differ, «нет» without one (TZXDATA 8.4).</summary>
    public string TemplateText { get => templateText; private set => SetProperty(ref templateText, value); }
    /// <summary>Message above the tree: no selection, objects without <c>BobrovXDATA</c>, no schema, a write or input error.</summary>
    public string Message { get => message; private set => SetProperty(ref message, value); }
    /// <summary><see cref="Message"/> is the error of the last write or input (TZXDATA 8.5): shown in red.</summary>
    public bool HasError { get => hasError; private set => SetProperty(ref hasError, value); }
    /// <summary>The selection has objects without <c>BobrovXDATA</c>: «Назначить шаблон» is offered (TZXDATA 8.5).</summary>
    public bool NeedsTemplate { get => needsTemplate; private set => SetProperty(ref needsTemplate, value); }
    /// <summary>«+ Параметр» (TZXDATA 8.4): scalar parameters of the schema that are not shown yet.</summary>
    public IReadOnlyList<PropertyOption> AvailableParameters { get => available; private set => SetProperty(ref available, value); }
    /// <summary>
    /// «Шаблон ▾» (TZXDATA 8.4, stage 7): templates of the schema that accept every entity type of the selection
    /// (<c>EntityTypes</c>); the key is the template id, the caption «Устройство (DEVICE)».
    /// </summary>
    public IReadOnlyList<PropertyOption> AvailableTemplates { get => templates; private set => SetProperty(ref templates, value); }
    /// <summary>An object of the selection has a template: «Без шаблона» is offered.</summary>
    public bool HasTemplate { get => hasTemplate; private set => SetProperty(ref hasTemplate, value); }
    /// <summary>An object shows a template default (<c>○</c>) that is not stored: «Зафиксировать значения по умолчанию» (TZXDATA 6.2).</summary>
    public bool HasDefaults { get => hasDefaults; private set => SetProperty(ref hasDefaults, value); }
    /// <summary>The row in focus: only it gets an editor (TZXDATA 8.3); it is selected again after a write.</summary>
    public PropertyRow? SelectedRow
    {
        get => selectedRow;
        set
        {
            SetProperty(ref selectedRow, value);
            if (value != null) selectedIdentity = value.Identity;
        }
    }
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
    /// <summary>Widths of columns 0–4; the view writes back the widths dragged by the user for the session.</summary>
    public double[] ColumnWidths { get; } = { 44, 150, 120, 28, 130 };
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
    /// <summary>«Шаблон ▾» / «Назначить шаблон»: <see cref="AssignTemplate"/> with an option of <see cref="AvailableTemplates"/>.</summary>
    public ICommand AssignTemplateCommand { get; }
    /// <summary>«Зафиксировать значения по умолчанию»: <see cref="CommitDefaults"/>.</summary>
    public ICommand CommitDefaultsCommand { get; }
    /// <summary><c>×</c> of column 0: <see cref="DeleteRow"/>.</summary>
    public ICommand DeleteRowCommand { get; }
    /// <summary><c>✎</c> of column 0: <see cref="EditRow"/>.</summary>
    public ICommand EditRowCommand { get; }
    /// <summary>«+ Добавить» of column 4: <see cref="AddRow"/>.</summary>
    public ICommand AddRowCommand { get; }
    /// <summary>«+ Параметр»: <see cref="AddParameter"/> with a <see cref="PropertyOption"/> of <see cref="AvailableParameters"/>.</summary>
    public ICommand AddParameterCommand { get; }

    /// <summary>«⚙» (TZXDATA 8.4, 9): <see cref="RunSchemaCommand"/> with one of the schema commands.</summary>
    public ICommand SchemaCommand { get; }

    /// <summary>The modal schema editor (TZXDATA 9).</summary>
    public const string SchemaEdit = "VELEC_SCHEMA_EDIT";
    /// <summary>Import of a JSON schema into the drawing (TZXDATA 5.1).</summary>
    public const string SchemaImport = "VELEC_SCHEMA_IMPORT";
    /// <summary>Export of the schema of the drawing to JSON (TZXDATA 5.2).</summary>
    public const string SchemaExport = "VELEC_SCHEMA_EXPORT";
    private static readonly string[] SchemaCommands = { SchemaEdit, SchemaImport, SchemaExport };
    private Action<string>? schemaCommandRunner;

    /// <summary>
    /// Starts a schema command in the host: AutoCAD runs it as a command of the document after the click, so that the
    /// palette never writes the NOD itself and the command is one Undo step. Without a host «⚙» is disabled.
    /// </summary>
    public Action<string>? SchemaCommandRunner
    {
        get => schemaCommandRunner;
        set
        {
            if (schemaCommandRunner == value) return;
            schemaCommandRunner = value;
            Notify(nameof(CanRunSchemaCommands));
            ((DelegateCommand)SchemaCommand).RaiseCanExecuteChanged();
        }
    }

    public bool CanRunSchemaCommands => schemaCommandRunner != null;

    /// <summary>Runs <see cref="SchemaEdit"/>, <see cref="SchemaImport"/> or <see cref="SchemaExport"/>; other names are refused.</summary>
    public bool RunSchemaCommand(string command)
    {
        var name = SchemaCommands.FirstOrDefault(item => string.Equals(item, command, StringComparison.OrdinalIgnoreCase));
        if (name is null || schemaCommandRunner is null) return false;
        schemaCommandRunner(name);
        return true;
    }

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
        if (row.Kind == PropertyRowKind.TableRow) { if (row.IsEditing) EditRow(row); return; }
        if (!row.IsExpandable) return;
        row.IsExpanded = !row.IsExpanded;
        Saved()[row.Key] = row.IsExpanded;
        ShowChildren(row);
    }

    /// <summary>Finds a row of the tree by its <see cref="PropertyRow.Key"/>.</summary>
    public PropertyRow? Find(string key) => All(roots).FirstOrDefault(row => row.Key == key);

    /// <summary>
    /// Enter or leaving the cell (TZXDATA 8.5): writes the text of the editor when it was changed. A visit without typing
    /// writes nothing, so «Разное» stays; an empty text restores the default of a non-string parameter. A wrong input is
    /// reported above the VST and nothing is written. Returns true when a write was requested.
    /// </summary>
    public bool Commit(PropertyRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (!Current(row))
        {
            // A late commit (the editor lost the focus after × or another write rebuilt the tree): the text goes to the
            // same row of the new tree, and is dropped when the row is gone, so a deleted table row is not written back.
            var current = row.IsDirty ? All(roots).FirstOrDefault(item => item.Kind == row.Kind && item.Identity == row.Identity) : null;
            if (current is null) { row.ResetEdit(); return false; }
            current.EditText = row.EditText;
            row.ResetEdit();
            row = current;
        }
        if (!CanEdit || !row.IsDirty || !row.IsEditable || row.IsFlagEditor || row.Parameter is null) return false;
        var text = row.EditText;
        if (!row.IsMixed && string.Equals(text, row.InitialText, StringComparison.Ordinal)) { row.ResetEdit(); return false; }
        var parameter = row.Parameter;
        var type = types.Find(parameter.Type);
        if (type is null || schema?.Schema is null) { row.ResetEdit(); return false; }
        if (text.Trim().Length == 0 && type.Kind != ParameterKind.String)
        {
            // The default of a scalar is the absence of its value (TZXDATA 6.2); a cell has no default.
            if (row.Kind != PropertyRowKind.Parameter) return Reject(row, "значение не может быть пустым");
            return Request(row, SelectionEdit.RemoveParameter(row.Id!));
        }
        object value = text;
        if (row.IsChoiceEditor)
        {
            var option = row.Options.FirstOrDefault(item => string.Equals(item.Caption, text, StringComparison.Ordinal))
                ?? row.Options.FirstOrDefault(item => string.Equals(item.Caption, text.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? row.Options.FirstOrDefault(item => string.Equals(item.Key, text.Trim(), StringComparison.Ordinal));
            if (option != null) value = option.Key;
            else if (!row.AllowCustom) return Reject(row, "«" + text + "» нет в списке");
        }
        var error = XDataPatch.Check(parameter, type, value, schema.Schema, out var converted);
        if (error != null) return Reject(row, error);
        if (row.Kind == PropertyRowKind.Parameter) return Request(row, SelectionEdit.SetParameter(row.Id!, converted));
        if (row.Table is null || row.RowKey is null || row.Column is null) { row.ResetEdit(); return false; }
        if (ParameterSchema.SameId(row.Column, row.KeyColumn))
        {
            // A new key renames the row in every object: it must stay unique (TZXDATA 4.2).
            var key = XDataV2.FormatValue(converted);
            if (key.Length == 0) return Reject(row, "ключ строки не может быть пустым");
            if (key != row.RowKey && row.RowKeys?.Contains(key) == true) return Reject(row, "строка с ключом «" + key + "» уже есть");
            if (key != row.RowKey) return Rename(row, key, SelectionEdit.SetTableCell(row.Table, row.RowKey, row.Column, converted));
        }
        return Request(row, SelectionEdit.SetTableCell(row.Table, row.RowKey, row.Column, converted));
    }

    /// <summary>Esc: the editor shows the value again; nothing is written.</summary>
    public void Cancel(PropertyRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        row.ResetEdit();
    }

    /// <summary>
    /// A click on a check box (TZXDATA 8.5): a <c>Boolean</c> parameter, cell or the flag column of a table row is written
    /// at once. A three-state box of «Разное» gets the clicked value in every object.
    /// </summary>
    public bool SetFlag(PropertyRow row, bool value)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (!CanEdit || !Current(row) || !row.CanSetFlag || row.Flag == value) return false;
        if (row.Kind == PropertyRowKind.Parameter && row.Id != null) return Request(row, SelectionEdit.SetParameter(row.Id, value));
        var column = row.Kind == PropertyRowKind.TableRow ? row.FlagColumn : row.Column;
        if (row.Table is null || row.RowKey is null || column is null) return false;
        return Request(row, SelectionEdit.SetTableCell(row.Table, row.RowKey, column, value));
    }

    /// <summary>
    /// <c>×</c>: removes a table row from every object of the selection, or a value outside the template or the schema
    /// (TZXDATA 6.5, 8.5).
    /// </summary>
    public bool DeleteRow(PropertyRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (!CanEdit || !Current(row) || !row.CanDelete) return false;
        if (row.Kind == PropertyRowKind.TableRow)
            return row.Table != null && row.RowKey != null && Request(row, SelectionEdit.DeleteTableRow(row.Table, row.RowKey));
        return row.Id != null && Request(row, SelectionEdit.RemoveParameter(row.Id));
    }

    /// <summary><c>✎</c>: shows or hides the cells of a table row, one child row per column with its editor.</summary>
    public void EditRow(PropertyRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (!row.CanEditRow || row.Cells is null) return;
        var open = !row.IsEditing;
        if (open) editing.Add(row.Identity);
        else editing.Remove(row.Identity);
        var before = row.children.Count;
        Open(row, open);
        RowCount += row.children.Count - before;
        Notify(nameof(RowCount));
        ShowChildren(row);
    }

    /// <summary>
    /// «+ Добавить» of a table or category: adds a row with a new unique key to every object of the selection; a row of a
    /// category gets its <c>GroupBy</c> value. The new row is opened by <c>✎</c> with its key cell selected.
    /// </summary>
    public bool AddRow(PropertyRow node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (!CanEdit || !Current(node) || !node.CanAdd || node.Table is null || node.KeyColumn is null || schema is null) return false;
        if (!schema.Parameters.TryGetValue(node.KeyColumn, out var keyParameter)) return false;
        var keys = node.RowKeys ?? new HashSet<string>(StringComparer.Ordinal);
        object? key = null;
        switch (types.Find(keyParameter.Type)?.Kind)
        {
            case ParameterKind.String:
                var text = NewRowKey;
                for (var i = 2; keys.Contains(text); i++) text = NewRowKey + " " + i.ToString(CultureInfo.InvariantCulture);
                key = text;
                break;
            case ParameterKind.Integer:
                key = keys.Select(item => int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0).DefaultIfEmpty(0).Max() + 1;
                break;
            case ParameterKind.Double:
                key = Math.Floor(keys.Select(item => double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0).DefaultIfEmpty(0).Max()) + 1;
                break;
            case ParameterKind.Enum:
                key = schema.Schema?.FindEnum(keyParameter.Enum)?.Items.Select(item => item.Key).FirstOrDefault(item => !string.IsNullOrEmpty(item) && !keys.Contains(item!));
                break;
        }
        if (key is null) return Reject(node, "нет свободного ключа для новой строки");
        var cells = new List<KeyValuePair<string, object>> { new KeyValuePair<string, object>(keyParameter.Id!, key) };
        if (node.Kind == PropertyRowKind.Category && node.CategoryColumn != null && node.CategoryValue != null && !ParameterSchema.SameId(node.CategoryColumn, node.KeyColumn))
            cells.Add(new KeyValuePair<string, object>(node.CategoryColumn, node.CategoryValue));
        var identity = node.TableKey + "/r:" + XDataV2.FormatValue(key);
        editing.Add(identity);
        selectedIdentity = reveal = identity + "/f:" + keyParameter.Id;
        return Request(node, SelectionEdit.AddTableRow(node.Table, cells));
    }

    /// <summary>
    /// «+ Параметр» (TZXDATA 8.4): writes a parameter of the schema that is not shown yet to every object, with the value
    /// of its default (or an empty value of its type); it is shown in «Дополнительные» and selected for editing.
    /// </summary>
    public bool AddParameter(string id)
    {
        if (!CanEdit || schema?.Schema is null || string.IsNullOrEmpty(id)) return false;
        if (!AvailableParameters.Any(option => ParameterSchema.SameId(option.Key, id)) || !schema.Parameters.TryGetValue(id, out var parameter)) return false;
        var type = types.Find(parameter.Type)!;
        var value = selection.Calculations.FirstOrDefault()?.Value(parameter.Id!) ?? type.Kind switch
        {
            ParameterKind.Integer => (object)(int)Math.Ceiling(parameter.Editor?.Min ?? 0),
            ParameterKind.Double => parameter.Editor?.Min ?? 0.0,
            ParameterKind.Boolean => false,
            ParameterKind.Enum => schema.Schema.FindEnum(parameter.Enum)?.Items.FirstOrDefault()?.Key ?? "",
            _ => ""
        };
        var error = XDataPatch.Check(parameter, type, value, schema.Schema, out var converted);
        if (error != null) { ShowError("Ошибка ввода: " + parameter.Id + ": " + error + "."); return false; }
        selectedIdentity = reveal = "p:" + parameter.Id!.ToUpperInvariant();
        return selection.RequestEdit(SelectionEdit.SetParameter(parameter.Id!, converted));
    }

    /// <summary>
    /// <c>VELEC_TEMPLATE_APPLY</c> of the panel (TZXDATA 6.4, 6.6): sets the template of every object in one transaction;
    /// an object without <c>DEVICE_ID</c> gets a new one. Values are kept: those outside the new template go to
    /// «Дополнительные». An empty id removes the template; the values stay.
    /// </summary>
    public bool AssignTemplate(string id)
    {
        if (!CanEdit || id is null) return false;
        if (id.Length == 0 ? !HasTemplate : !AvailableTemplates.Any(option => ParameterSchema.SameId(option.Key, id))) return false;
        // Every object has this template already: nothing to write.
        if (id.Length != 0 && selection.Values.All(item => ParameterSchema.SameId(item.Template, id) && item.Format == XDataFormat.V2 &&
            item.DeviceId != null)) return false;
        return selection.RequestEdit(SelectionEdit.SetTemplate(id));
    }

    /// <summary>
    /// «Зафиксировать значения по умолчанию» (TZXDATA 6.2): writes the shown defaults of the templates in one transaction,
    /// for programs that read only XDATA; stored values are kept.
    /// </summary>
    public bool CommitDefaults() => CanEdit && HasDefaults && selection.RequestEdit(SelectionEdit.CommitDefaults());

    private bool Request(PropertyRow row, SelectionEdit edit)
    {
        // The controller writes and reads the selection again synchronously: the tree is rebuilt by Applied. The written
        // row is reset too, so leaving its editor afterwards does not write the same text again.
        var written = selection.RequestEdit(edit);
        row.ResetEdit();
        return written;
    }

    private bool Reject(PropertyRow row, string error)
    {
        row.ResetEdit();
        ShowError("Ошибка ввода: " + (row.Parameter?.Id ?? row.Id ?? row.Caption) + ": " + error + ".");
        return false;
    }

    private void ShowError(string text)
    {
        Message = text;
        HasError = true;
    }

    /// <summary>
    /// The key of a row opened by <c>✎</c> changes: it stays open and selected under the new key. The tree is rebuilt
    /// during the write, so the new key is set first; a failed write selects the row under its old key again.
    /// </summary>
    private bool Rename(PropertyRow row, string key, SelectionEdit edit)
    {
        var old = row.TableKey + "/r:" + row.RowKey;
        var renamed = row.TableKey + "/r:" + key;
        var selected = selectedIdentity;
        var open = editing.Contains(old);
        if (open) editing.Add(renamed);
        if (selected != null && (selected == old || selected.StartsWith(old + "/f:", StringComparison.Ordinal)))
            selectedIdentity = renamed + selected.Substring(old.Length);
        if (Request(row, edit))
        {
            if (open) editing.Remove(old);
            return true;
        }
        if (open) editing.Remove(renamed);
        selectedIdentity = selected;
        SelectedRow = selected is null ? null : Rows.FirstOrDefault(item => item.Identity == selected);
        selectedIdentity = selected;
        return false;
    }

    private bool Current(PropertyRow row) => row.Build == generation;

    private void Open(PropertyRow row, bool open)
    {
        row.children.Clear();
        if (open && row.Cells != null) row.children.AddRange(row.Cells().Select(cell => { cell.Build = row.Build; return cell; }));
        row.IsEditing = open;
        row.IsExpanded = open;
        row.Changed(nameof(PropertyRow.IsExpandable));
    }

    /// <summary>Inserts or removes the visible children of a row that was expanded or collapsed.</summary>
    private void ShowChildren(PropertyRow row)
    {
        var index = Rows.IndexOf(row);
        if (filter.Length != 0 || index < 0) { Flatten(); return; }
        while (index + 1 < Rows.Count && Rows[index + 1].Level > row.Level) Rows.RemoveAt(index + 1);
        if (!row.IsExpanded) return;
        var shown = new List<PropertyRow>();
        foreach (var child in row.children) Visible(child, shown);
        for (var i = 0; i < shown.Count; i++) Rows.Insert(index + 1 + i, shown[i]);
    }

    private void SetAllExpanded(bool expanded)
    {
        var saved = Saved();
        foreach (var row in All(roots).Where(row => row.IsExpandable && row.Kind != PropertyRowKind.TableRow))
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
        generation++;
        templateKey = build.TemplateKey;
        schema = build.Snapshot;
        TemplateText = build.TemplateText;
        NeedsTemplate = build.NeedsTemplate;
        Message = selection.Error ?? build.Message;
        HasError = selection.Error != null;
        IsEnabled = selection.Entities.Count != 0;
        CanEdit = build.CanEdit;
        foreach (var row in All(built).Where(row => row.Kind == PropertyRowKind.TableRow && row.CanEditRow && editing.Contains(row.Identity)).ToArray())
            Open(row, true);
        var saved = Saved();
        var count = 0;
        foreach (var row in All(built))
        {
            count++;
            row.Build = generation;
            if (row.IsExpandable && row.Kind != PropertyRowKind.TableRow)
                row.IsExpanded = saved.TryGetValue(row.Key, out var expanded) ? expanded : row.DefaultExpanded;
        }
        if (reveal != null)
        {
            foreach (var ancestor in Path(built, reveal) ?? new List<PropertyRow>())
                if (ancestor.Kind != PropertyRowKind.TableRow) saved[ancestor.Key] = ancestor.IsExpanded = true;
            reveal = null;
        }
        roots = built;
        RowCount = count;
        AvailableParameters = build.Available(built);
        AvailableTemplates = build.TemplateOptions();
        HasTemplate = build.HasTemplate;
        HasDefaults = CanEdit && build.HasDefaults;
        ((DelegateCommand)AssignTemplateCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)CommitDefaultsCommand).RaiseCanExecuteChanged();
        Flatten();
        BuildTime = timer.Elapsed;
        Notify(nameof(Roots));
        Notify(nameof(RowCount));
        Notify(nameof(TemplateKey));
        Notify(nameof(ScrollOffset));
        Built?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The ancestors of the row with <paramref name="identity"/>, from the root; null when it is not in the tree.</summary>
    private static List<PropertyRow>? Path(IEnumerable<PropertyRow> rows, string identity)
    {
        foreach (var row in rows)
        {
            if (row.Identity == identity) return new List<PropertyRow>();
            if (Path(row.children, identity) is List<PropertyRow> path) { path.Insert(0, row); return path; }
        }
        return null;
    }

    private void Flatten()
    {
        // A new list clears the selection of the view: the row with the same identity is selected again.
        var identity = selectedIdentity;
        var shown = new List<PropertyRow>();
        if (filter.Length == 0) foreach (var row in roots) Visible(row, shown);
        else foreach (var row in roots) Filtered(row, false, shown);
        Rows = new ObservableCollection<PropertyRow>(shown);
        SelectedRow = identity is null ? null : shown.FirstOrDefault(row => row.Identity == identity);
        selectedIdentity = identity;
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
            // v1 and newer formats are written only after a migration (TZXDATA 6.1); objects without data get a v2 section.
            CanEdit = selection.CanWrite && values.Count != 0 && schema?.Schema != null &&
                values.All(item => item.Format == XDataFormat.None || item.Format == XDataFormat.V2);
        }

        public SchemaSnapshot? Snapshot => schema;
        public bool CanEdit { get; }

        public string TemplateKey { get; private set; } = "";
        public string TemplateText { get; private set; } = "";
        public bool NeedsTemplate { get; private set; }
        public bool HasTemplate => values.Any(item => item.Template != null);
        public bool HasDefaults => calculations.Any(calculation => calculation.MissingDefaults().Count != 0);
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
            if (selection.CanWrite && values.Any(item => item.Format == XDataFormat.V1 || item.Format == XDataFormat.Newer))
                Message = (Message.Length == 0 ? "" : Message + " ") + "Есть объекты в формате v1 или более новой версии: только чтение.";
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
            foreach (var group in groups) result.Add(Group("g:" + group.Name, group.Name, group.Ids, false));
            if (extra.Count != 0) result.Add(Group("g:" + GroupExtra, GroupExtra, extra, true));
            if (outside.Count != 0)
            {
                var node = Group("g:" + GroupNotInSchema, GroupNotInSchema + " (" + outside.Count + ")", outside, true);
                node.DefaultExpanded = false;
                node.State = PropertyState.Error;
                node.StateText = PropertyRow.Symbol(PropertyState.Error);
                node.Note = CanEdit ? "× — удалить значение" : "только чтение";
                node.ToolTip = "Id есть в XDATA объекта, но не описан в схеме NOD (TZXDATA 6.5).";
                result.Add(node);
            }
        }

        /// <summary>
        /// A group of one table with the same caption is shown as the table node itself (TZXDATA 8.1). Values of a
        /// <paramref name="removable"/> group (outside the template or the schema) get <c>×</c>.
        /// </summary>
        private PropertyRow Group(string key, string name, List<string> ids, bool removable)
        {
            if (ids.Count == 1 && schema!.Parameters.TryGetValue(ids[0], out var only) && Kind(only) == ParameterKind.Table &&
                string.Equals(Caption(only), name, StringComparison.OrdinalIgnoreCase))
                return Table(ids[0], only, 0, "");
            var group = new PropertyRow(PropertyRowKind.Group, key, name, 0);
            foreach (var id in ids)
            {
                var parameter = schema!.Parameters.TryGetValue(id, out var found) ? found : null;
                var child = parameter != null && Kind(parameter) == ParameterKind.Table || parameter is null && IsList(id)
                    ? Table(id, parameter, 1, key + "/")
                    : Scalar(id, parameter, 1);
                child.CanDelete = removable && CanEdit && !ParameterSchema.ReservedIds.Contains(id, StringComparer.OrdinalIgnoreCase);
                group.children.Add(child);
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
            // Objects of a selection often store the same value: it is formatted once (TZXDATA 10.4).
            object? formattedValue = null;
            string? formattedText = null;
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
                        if (formattedText is null || !SameValue(formattedValue, stored.Value))
                        {
                            formattedValue = stored.Value;
                            formattedText = ObjectCalculation.FormatValue(schema!, parameter, stored.Value, false, Culture, Types);
                        }
                        text = formattedText;
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
            if (parameter != null && Kind(parameter) == ParameterKind.Boolean)
            {
                var flags = calculations.Select(calculation => calculation.Value(id) as bool?).Distinct().ToArray();
                row.Flag = flags.Length == 1 && !mixed ? flags[0] : null;
            }
            SetEditor(row, parameter);
            return row;
        }

        /// <summary>Equal stored values have equal text; doubles by bits, so that <c>-0</c> is not taken for <c>0</c>.</summary>
        private static bool SameValue(object? left, object right) =>
            left is double x && right is double y ? BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y) : Equals(left, right);

        /// <summary>The editor of column 2 by the type of the parameter (TZXDATA 8.3).</summary>
        private void SetEditor(PropertyRow row, SchemaParameter? parameter)
        {
            row.Parameter = parameter;
            if (!CanEdit || parameter?.Id is null || parameter.Editor?.ReadOnly == true ||
                ParameterSchema.ReservedIds.Contains(parameter.Id, StringComparer.OrdinalIgnoreCase)) return;
            var kind = Kind(parameter);
            row.Editor = kind switch
            {
                ParameterKind.String => PropertyEditorKind.Text,
                ParameterKind.Integer => PropertyEditorKind.Number,
                ParameterKind.Double => PropertyEditorKind.Number,
                ParameterKind.Boolean => PropertyEditorKind.Flag,
                ParameterKind.Enum => PropertyEditorKind.Choice,
                _ => PropertyEditorKind.None
            };
            if (row.Editor == PropertyEditorKind.None) return;
            if (kind == ParameterKind.Enum)
            {
                row.Options = schema!.Schema!.FindEnum(parameter.Enum)?.Items.Where(item => !string.IsNullOrEmpty(item.Key))
                    .Select(item => new PropertyOption(item.Key!, string.IsNullOrEmpty(item.Caption) ? item.Key! : item.Caption!)).ToArray() ?? Array.Empty<PropertyOption>();
                row.AllowCustom = parameter.Editor?.AllowCustom == true;
            }
            row.Unit = parameter.Editor?.Unit ?? "";
            row.CanSetFlag = row.Editor == PropertyEditorKind.Flag;
            row.InitialText = row.IsMixed ? "" : row.Value;
            row.ResetEdit();
        }

        /// <summary>
        /// «+ Параметр»: scalar parameters of the schema that the tree does not show, except system ids, formulas and
        /// table columns.
        /// </summary>
        public IReadOnlyList<PropertyOption> Available(IReadOnlyList<PropertyRow> built)
        {
            if (!CanEdit) return Array.Empty<PropertyOption>();
            var shown = new HashSet<string>(All(built).Where(row => row.Id != null).Select(row => row.Id!), StringComparer.OrdinalIgnoreCase);
            var columns = new HashSet<string>(schema!.Parameters.Values.Where(parameter => parameter.Columns != null).SelectMany(parameter => parameter.Columns!), StringComparer.OrdinalIgnoreCase);
            return schema.Schema!.Parameters
                .Where(parameter => !string.IsNullOrEmpty(parameter.Id) && !shown.Contains(parameter.Id!) && !columns.Contains(parameter.Id!) &&
                    !ParameterSchema.ReservedIds.Contains(parameter.Id!, StringComparer.OrdinalIgnoreCase) && parameter.Editor?.ReadOnly != true &&
                    Kind(parameter) is ParameterKind kind && kind != ParameterKind.Table && kind != ParameterKind.CalculatedString)
                .Select(parameter => new PropertyOption(parameter.Id!, Caption(parameter) == parameter.Id ? parameter.Id! : Caption(parameter) + " (" + parameter.Id + ")"))
                .ToArray();
        }

        /// <summary>«Шаблон ▾»: the templates that accept every entity type of the selection.</summary>
        public IReadOnlyList<PropertyOption> TemplateOptions()
        {
            if (!CanEdit) return Array.Empty<PropertyOption>();
            return schema!.Schema!.TemplatesFor(values.Select(item => item.EntityType))
                .Select(template => new PropertyOption(template.Id!, template.DisplayName)).ToArray();
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
            node.Table = node.Id;
            node.TableKey = key;
            if (parameter != null)
            {
                node.KeyColumn = XDataV2.KeyColumn(parameter, schema!, Types);
                node.CanAdd = CanEdit && node.KeyColumn != null && parameter.Editor?.ReadOnly != true;
            }
            node.RowKeys = new HashSet<string>(perObject.Where(rows => rows != null).SelectMany(rows => rows!).Where(row => row.Key != null).Select(row => row.Key!), StringComparer.Ordinal);
            Merge(node, perObject, level + 1, key, parameter is null ? null : columns);
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
            /// <summary>Columns declared by the table in the schema: only they are written (cells outside are read-only).</summary>
            public HashSet<string> Declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private TableColumns Columns(string id, SchemaParameter? table)
        {
            var columns = new TableColumns();
            var ids = new List<string>();
            if (table?.Columns != null) ids.AddRange(table.Columns.Where(column => !string.IsNullOrEmpty(column)));
            columns.Declared.UnionWith(ids);
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
            /// <summary>The stored <c>GroupBy</c> value: a row added to the category gets it.</summary>
            public object? CategoryValue;
            public ObjectRow? Source;
        }

        private RowData RowOf(ObjectRow row, TableColumns columns)
        {
            var data = new RowData { Key = row.Key, Source = row };
            for (var i = 1; i <= 4; i++)
                if (columns.Ids[i] is string column) data.Texts[i] = Text(row.Find(column), columns.Parameters[i]);
            if (columns.FlagColumn && columns.Ids[3] is string flag && row.Find(flag) is ObjectValue cell && cell.Value is bool value) data.Flag = value;
            if (columns.GroupBy != null)
            {
                var stored = row.Find(columns.GroupBy);
                var category = Text(stored, columns.GroupByParameter);
                data.Category = category.Length == 0 ? NoCategory : category;
                if (category.Length != 0 && stored?.State == ObjectValueState.Typed && !stored.IsTable) data.CategoryValue = stored.Value;
            }
            data.Tip = string.Join("\n", columns.All.Select((column, index) => columns.Names[index] + ": " + Text(row.Find(column), schema!.Parameters.TryGetValue(column, out var found) ? found : null)));
            return data;
        }

        private string Text(ObjectValue? cell, SchemaParameter? parameter) =>
            cell is null ? "" : cell.State == ObjectValueState.Typed && parameter != null && !cell.IsTable
                ? ObjectCalculation.FormatValue(schema!, parameter, cell.Value, false, Culture, Types) : cell.Text;

        /// <summary>
        /// Rows of all objects matched by the key column (TZXDATA 4.2), or by position without a key; a row missing on an
        /// object, or a differing cell, gives «Разное». Rows of a schema table (<paramref name="columns"/>) get their editors.
        /// </summary>
        private void Merge(PropertyRow node, IReadOnlyList<IReadOnlyList<RowData>?> perObject, int level, string key, TableColumns? columns = null)
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
                        if (columns != null)
                        {
                            parent.Table = node.Table;
                            parent.TableKey = node.TableKey;
                            parent.KeyColumn = node.KeyColumn;
                            parent.RowKeys = node.RowKeys;
                            parent.CanAdd = node.CanAdd;
                            parent.ShowAdd = true;
                            parent.CategoryColumn = columns.GroupBy;
                            parent.CategoryValue = category == ObjectCalculation.Various ? null : item.Rows.First(row => row != null)!.CategoryValue;
                        }
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
                if (columns != null) Editors(node, row, item.Key, item.Rows, all, columns);
                parent.children.Add(row);
            }
            foreach (var category in categories) category.Value = RowsText(category.children.Count);
        }

        /// <summary>
        /// A row of a schema table: <c>×</c>, <c>✎</c>, the editor of column 2 and the check box of column 3 (TZXDATA 8.2).
        /// A row is changed by its key in every object, so a row without a key is read only.
        /// </summary>
        private void Editors(PropertyRow node, PropertyRow row, string name, RowData?[] rows, bool all, TableColumns columns)
        {
            var first = rows.First(data => data != null)!;
            row.Table = node.Table;
            row.TableKey = node.TableKey;
            row.KeyColumn = node.KeyColumn;
            row.RowKeys = node.RowKeys;
            row.RowKey = first.Key != null && name == "k:" + first.Key ? first.Key : null;
            var editable = node.CanAdd && row.RowKey != null;
            row.CanDelete = row.CanEditRow = editable;
            row.Cells = () => Cells(row, rows, all, columns, editable);
            if (!editable) return;
            row.Column = columns.Ids[2];
            if (columns.Parameters[2] is SchemaParameter second && columns.Declared.Contains(columns.Ids[2]!))
            {
                SetEditor(row, second);
                if (row.IsFlagEditor)
                {
                    // Column 3 is the check box of the row: a Boolean in column 2 is a «Да»/«Нет» list.
                    row.Editor = PropertyEditorKind.Choice;
                    row.Options = new[] { new PropertyOption("1", "Да"), new PropertyOption("0", "Нет") };
                }
            }
            if (columns.FlagColumn && columns.Ids[3] is string flag && columns.Declared.Contains(flag) && columns.Parameters[3]?.Editor?.ReadOnly != true)
            {
                row.FlagColumn = flag;
                row.CanSetFlag = true;
            }
            else row.CanSetFlag = false;
        }

        /// <summary>The cells of a table row shown by <c>✎</c>: every column with its editor, «Разное» when they differ.</summary>
        private IEnumerable<PropertyRow> Cells(PropertyRow row, RowData?[] rows, bool all, TableColumns columns, bool editable)
        {
            foreach (var column in columns.All)
            {
                var parameter = schema!.Parameters.TryGetValue(column, out var found) ? found : null;
                var cells = rows.Where(data => data != null).Select(data => data!.Source?.Find(column)).ToArray();
                var texts = cells.Select(cell => Text(cell, parameter)).Distinct(StringComparer.Ordinal).ToArray();
                var cell = new PropertyRow(PropertyRowKind.Cell, row.Key + "/f:" + column.ToUpperInvariant(), parameter is null ? column : CaptionWithUnit(parameter), row.Level + 1)
                {
                    Id = parameter?.Id ?? column,
                    Table = row.Table,
                    TableKey = row.TableKey,
                    KeyColumn = row.KeyColumn,
                    RowKeys = row.RowKeys,
                    RowKey = row.RowKey,
                    Column = parameter?.Id ?? column,
                    IsMixed = !all || texts.Length != 1
                };
                cell.Value = cell.IsMixed ? ObjectCalculation.Various : texts[0];
                var state = PropertyState.None;
                string? error = null;
                foreach (var stored in cells.Where(stored => stored != null))
                {
                    state = Worse(state, stored!.State == ObjectValueState.Typed && stored.Warning is null ? PropertyState.Set : PropertyState.Error);
                    error ??= stored.Warning;
                }
                cell.State = state;
                cell.StateText = PropertyRow.Symbol(state);
                cell.Note = error ?? parameter?.Description ?? (parameter is null ? "не описан в схеме" : "");
                cell.ToolTip = Tip(column, parameter, error);
                if (parameter != null && Kind(parameter) == ParameterKind.Boolean)
                {
                    var flags = cells.Select(stored => stored?.Value as bool?).Distinct().ToArray();
                    cell.Flag = !cell.IsMixed && flags.Length == 1 ? flags[0] : null;
                }
                if (editable && columns.Declared.Contains(column)) SetEditor(cell, parameter);
                yield return cell;
            }
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
