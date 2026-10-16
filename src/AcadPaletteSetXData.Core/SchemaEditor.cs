using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Schema editor (TZXDATA 9): parameters, enums and templates of the drawing are edited in a draft
/// (a copy of the schema); «Применить» validates the draft (TZXDATA 4.5) and writes it to NOD in one
/// transaction — one Undo step; «Отмена» drops the draft. Objects that the drawing uses are not deleted:
/// a parameter or template stored in XDATA, an enum referenced by parameters (TZXDATA 4.3); an enum key
/// stored in objects is deleted only after <see cref="Confirm"/> shows how many objects keep it.
/// </summary>
/// <remarks>WPF binds to this model directly; it has no UI types so that every rule is tested without AutoCAD.</remarks>
public sealed class SchemaEditorViewModel : ObservableObject
{
    /// <summary><c>source</c> of the NOD <c>INFO</c> stamp written by «Применить».</summary>
    public const string StampSource = "Редактор схемы";
    /// <summary><c>mode</c> of the NOD <c>INFO</c> stamp written by «Применить».</summary>
    public const string StampMode = "Edit";

    private readonly string currentJson;
    private readonly Dictionary<(object Owner, string Field), SchemaIssue> inputErrors = new Dictionary<(object Owner, string Field), SchemaIssue>();
    private string parameterSearch = "";
    private ParameterDraft? selectedParameter;
    private EnumDraft? selectedEnum;
    private TemplateDraft? selectedTemplate;
    private ParameterDraft? selectedAvailable;
    private TemplateParameterDraft? selectedTemplateParameter;
    private EnumKeyDraft? selectedKey;
    private IReadOnlyList<SchemaIssue> issues = Array.Empty<SchemaIssue>();
    private IReadOnlyList<SchemaChange> changes = Array.Empty<SchemaChange>();
    private IReadOnlyList<string> enumIds = Array.Empty<string>();
    private IReadOnlyList<string> parameterIds = Array.Empty<string>();
    private bool isDirty;
    private string status = "";
    private int suspended;

    public SchemaEditorViewModel(ParameterSchema? current, SchemaUsage? usage = null, IEnumerable<EntityDataSnapshot>? selection = null,
        CultureInfo? culture = null, ParameterTypeRegistry? types = null)
    {
        Current = current;
        Usage = usage ?? new SchemaUsage();
        Selection = selection?.ToArray() ?? Array.Empty<EntityDataSnapshot>();
        Culture = culture ?? CultureInfo.CurrentCulture;
        Types = types ?? ParameterTypeRegistry.Default;
        TypeNames = Types.Types.Select(type => type.Name).ToArray();
        currentJson = ParameterSchemaJson.Write(current ?? new ParameterSchema());
        Draft = current is null ? new ParameterSchema() : ParameterSchemaJson.Clone(current);

        AddParameterCommand = new DelegateCommand(_ => AddParameter());
        DeleteParameterCommand = new DelegateCommand(_ => DeleteParameter(SelectedParameter), _ => SelectedParameter != null);
        MoveParameterUpCommand = new DelegateCommand(_ => MoveParameter(SelectedParameter, -1), _ => SelectedParameter != null);
        MoveParameterDownCommand = new DelegateCommand(_ => MoveParameter(SelectedParameter, 1), _ => SelectedParameter != null);
        InsertReferenceCommand = new DelegateCommand(id => InsertReference(id as string), id => SelectedParameter?.IsCalculated == true && id is string);
        AddEnumCommand = new DelegateCommand(_ => AddEnum());
        DeleteEnumCommand = new DelegateCommand(_ => DeleteEnum(SelectedEnum), _ => SelectedEnum != null);
        AddEnumKeyCommand = new DelegateCommand(_ => AddEnumKey(SelectedEnum), _ => SelectedEnum != null);
        DeleteEnumKeyCommand = new DelegateCommand(_ => DeleteEnumKey(SelectedKey), _ => SelectedKey != null);
        MoveEnumKeyUpCommand = new DelegateCommand(_ => MoveEnumKey(SelectedKey, -1), _ => SelectedKey != null);
        MoveEnumKeyDownCommand = new DelegateCommand(_ => MoveEnumKey(SelectedKey, 1), _ => SelectedKey != null);
        AddTemplateCommand = new DelegateCommand(_ => AddTemplate());
        DeleteTemplateCommand = new DelegateCommand(_ => DeleteTemplate(SelectedTemplate), _ => SelectedTemplate != null);
        AddToTemplateCommand = new DelegateCommand(_ => AddToTemplate(SelectedAvailable), _ => SelectedTemplate != null && SelectedAvailable != null);
        RemoveFromTemplateCommand = new DelegateCommand(_ => RemoveFromTemplate(SelectedTemplateParameter), _ => SelectedTemplateParameter != null);
        MoveTemplateParameterUpCommand = new DelegateCommand(_ => MoveTemplateParameter(SelectedTemplateParameter, -1), _ => SelectedTemplateParameter != null);
        MoveTemplateParameterDownCommand = new DelegateCommand(_ => MoveTemplateParameter(SelectedTemplateParameter, 1), _ => SelectedTemplateParameter != null);
        Reload();
    }

    /// <summary>Schema of the drawing when the editor was opened; null when the drawing has none.</summary>
    public ParameterSchema? Current { get; }
    /// <summary>The edited copy; the drawing is not changed until <see cref="Apply"/>.</summary>
    public ParameterSchema Draft { get; private set; }
    public SchemaUsage Usage { get; }
    /// <summary>Objects selected when the editor was opened: the first one previews formulas.</summary>
    public IReadOnlyList<EntityDataSnapshot> Selection { get; }
    public CultureInfo Culture { get; }
    public ParameterTypeRegistry Types { get; }
    public IReadOnlyList<string> TypeNames { get; }
    /// <summary>Asks the user to confirm a destructive change (deleting an enum key used by objects); null refuses it.</summary>
    public Func<string, bool>? Confirm { get; set; }

    public ObservableCollection<ParameterDraft> Parameters { get; } = new ObservableCollection<ParameterDraft>();
    /// <summary><see cref="Parameters"/> matching <see cref="ParameterSearch"/>.</summary>
    public ObservableCollection<ParameterDraft> FilteredParameters { get; } = new ObservableCollection<ParameterDraft>();
    public ObservableCollection<EnumDraft> Enums { get; } = new ObservableCollection<EnumDraft>();
    public ObservableCollection<TemplateDraft> Templates { get; } = new ObservableCollection<TemplateDraft>();
    /// <summary>«Все параметры» of the template tab: parameters not yet in <see cref="SelectedTemplate"/>.</summary>
    public ObservableCollection<ParameterDraft> AvailableParameters { get; } = new ObservableCollection<ParameterDraft>();

    public string ParameterSearch
    {
        get => parameterSearch;
        set { SetProperty(ref parameterSearch, value ?? ""); Filter(); }
    }

    public ParameterDraft? SelectedParameter
    {
        get => selectedParameter;
        set { SetProperty(ref selectedParameter, value); selectedParameter?.Refresh(); RaiseCommands(); }
    }

    public EnumDraft? SelectedEnum
    {
        get => selectedEnum;
        set { SetProperty(ref selectedEnum, value); SelectedKey = null; selectedEnum?.Refresh(); RaiseCommands(); }
    }

    public EnumKeyDraft? SelectedKey
    {
        get => selectedKey;
        set { SetProperty(ref selectedKey, value); RaiseCommands(); }
    }

    public TemplateDraft? SelectedTemplate
    {
        get => selectedTemplate;
        set { SetProperty(ref selectedTemplate, value); SelectedTemplateParameter = null; UpdateAvailable(); RaiseCommands(); }
    }

    public ParameterDraft? SelectedAvailable
    {
        get => selectedAvailable;
        set { SetProperty(ref selectedAvailable, value); RaiseCommands(); }
    }

    public TemplateParameterDraft? SelectedTemplateParameter
    {
        get => selectedTemplateParameter;
        set { SetProperty(ref selectedTemplateParameter, value); RaiseCommands(); }
    }

    /// <summary>Errors and warnings of the draft (TZXDATA 4.5) and of values typed in the editor.</summary>
    public IReadOnlyList<SchemaIssue> Issues => issues;
    public int ErrorCount => issues.Count(issue => issue.Severity == SchemaIssueSeverity.Error);
    public bool HasErrors => ErrorCount != 0;
    /// <summary>Differences between the draft and the drawing, by object.</summary>
    public IReadOnlyList<SchemaChange> Changes => changes;
    public IReadOnlyList<string> ChangeLines => changes.Select(Describe).ToArray();
    /// <summary>The draft differs from the schema of the drawing (also by order of objects).</summary>
    public bool IsDirty => isDirty;
    /// <summary>«Применить» is available: something changed and the draft has no errors.</summary>
    public bool CanApply => isDirty && !HasErrors;
    /// <summary>Result of the last action, e.g. why a deletion was refused.</summary>
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Summary =>
        "Параметров: " + Draft.Parameters.Count + ", справочников: " + Draft.Enums.Count + ", шаблонов: " + Draft.Templates.Count +
        (isDirty ? "; изменений: " + changes.Count : "; без изменений") + (HasErrors ? "; ошибок: " + ErrorCount : "");
    /// <summary>Ids of enums for the <c>enum</c> field.</summary>
    public IReadOnlyList<string> EnumIds => enumIds;
    /// <summary>Ids of parameters for <c>columns</c>, <c>groupBy</c>, <c>key</c> and <c>@@[...]</c> hints.</summary>
    public IReadOnlyList<string> ParameterIds => parameterIds;

    public DelegateCommand AddParameterCommand { get; }
    public DelegateCommand DeleteParameterCommand { get; }
    public DelegateCommand MoveParameterUpCommand { get; }
    public DelegateCommand MoveParameterDownCommand { get; }
    public DelegateCommand InsertReferenceCommand { get; }
    public DelegateCommand AddEnumCommand { get; }
    public DelegateCommand DeleteEnumCommand { get; }
    public DelegateCommand AddEnumKeyCommand { get; }
    public DelegateCommand DeleteEnumKeyCommand { get; }
    public DelegateCommand MoveEnumKeyUpCommand { get; }
    public DelegateCommand MoveEnumKeyDownCommand { get; }
    public DelegateCommand AddTemplateCommand { get; }
    public DelegateCommand DeleteTemplateCommand { get; }
    public DelegateCommand AddToTemplateCommand { get; }
    public DelegateCommand RemoveFromTemplateCommand { get; }
    public DelegateCommand MoveTemplateParameterUpCommand { get; }
    public DelegateCommand MoveTemplateParameterDownCommand { get; }

    public ParameterDraft? FindParameter(string? id) => Parameters.FirstOrDefault(item => ParameterSchema.SameId(item.Model.Id, id));
    public EnumDraft? FindEnum(string? id) => Enums.FirstOrDefault(item => ParameterSchema.SameId(item.Model.Id, id));
    public TemplateDraft? FindTemplate(string? id) => Templates.FirstOrDefault(item => ParameterSchema.SameId(item.Model.Id, id));

    // ---- Parameters -------------------------------------------------------------------------------------------------

    public ParameterDraft AddParameter()
    {
        var model = new SchemaParameter { Id = NewId("Param", id => Draft.FindParameter(id) != null), Type = "String", Caption = "Новый параметр" };
        if (SelectedParameter?.Model.Group != null) model.Group = SelectedParameter.Model.Group;
        var index = SelectedParameter is null ? Draft.Parameters.Count : Draft.Parameters.IndexOf(SelectedParameter.Model) + 1;
        Draft.Parameters.Insert(index, model);
        var item = new ParameterDraft(this, model);
        Parameters.Insert(index, item);
        if (!item.Matches(parameterSearch)) parameterSearch = "";
        Notify(nameof(ParameterSearch));
        Filter();
        SelectedParameter = item;
        Status = "Добавлен параметр " + model.Id + ".";
        Changed();
        return item;
    }

    /// <summary>Refused for a parameter stored in objects or referenced by templates, tables or formulas.</summary>
    public bool DeleteParameter(ParameterDraft? item)
    {
        if (item is null) return false;
        var id = item.Model.Id;
        var used = Usage.Count(SchemaSection.Parameter, id);
        if (used != 0 && Current?.FindParameter(id) != null)
            return Refuse("Параметр " + id + " хранится в объектах чертежа (" + used + ") — удаление запрещено.");
        var references = References(item.Model);
        if (references.Count != 0)
            return Refuse("Параметр " + id + " используется: " + string.Join(", ", references) + " — сначала уберите ссылки.");
        var index = Parameters.IndexOf(item);
        Draft.Parameters.Remove(item.Model);
        Parameters.Remove(item);
        ClearInputErrors(item);
        Filter();
        SelectedParameter = FilteredParameters.Count == 0 ? null : FilteredParameters[Math.Min(Math.Max(index, 0), FilteredParameters.Count - 1)];
        Status = "Параметр " + id + " удалён.";
        Changed();
        return true;
    }

    public void MoveParameter(ParameterDraft? item, int offset)
    {
        if (item is null || !Move(Draft.Parameters, item.Model, offset)) return;
        Parameters.Move(Parameters.IndexOf(item), Draft.Parameters.IndexOf(item.Model));
        Filter();
        SelectedParameter = item;
        Changed();
    }

    /// <summary>Appends <c>@@[id]</c> to the formula of the selected <c>CalculatedString</c>, or inserts it at <paramref name="position"/>.</summary>
    public void InsertReference(string? id, int? position = null)
    {
        var item = SelectedParameter;
        if (item is null || !item.IsCalculated || string.IsNullOrEmpty(id)) return;
        var format = item.Format;
        var at = position.HasValue ? Math.Max(0, Math.Min(position.Value, format.Length)) : format.Length;
        item.Format = format.Substring(0, at) + "@@[" + id + "]" + format.Substring(at);
    }

    /// <summary>Where the schema refers to the parameter: «шаблон DEVICE», «таблица Cables», «формула Name».</summary>
    public IReadOnlyList<string> References(SchemaParameter parameter)
    {
        var id = parameter.Id;
        var result = new List<string>();
        if (string.IsNullOrEmpty(id)) return result;
        foreach (var template in Draft.Templates)
            if (template.Parameters.Any(item => ParameterSchema.SameId(item.Id, id))) result.Add("шаблон " + template.Id);
        foreach (var other in Draft.Parameters)
        {
            if (ReferenceEquals(other, parameter)) continue;
            if ((other.Columns?.Any(column => ParameterSchema.SameId(column, id)) ?? false) ||
                ParameterSchema.SameId(other.GroupBy, id) || ParameterSchema.SameId(other.Key, id))
                result.Add("таблица " + other.Id);
            if (other.Format != null && CalculatedFormat.References(other.Format).Any(reference => ParameterSchema.SameId(reference, id)))
                result.Add("формула " + other.Id);
        }
        return result;
    }

    /// <summary>Templates whose list contains the parameter (TZXDATA 9: «в каких шаблонах используется»).</summary>
    public IReadOnlyList<SchemaTemplate> TemplatesUsing(string? id) =>
        Draft.Templates.Where(template => template.Parameters.Any(item => ParameterSchema.SameId(item.Id, id))).ToArray();

    /// <summary>The value of a <c>CalculatedString</c> on the first selected object, computed with the draft.</summary>
    public string FormulaPreview(SchemaParameter parameter)
    {
        if (Types.Find(parameter.Type)?.Kind != ParameterKind.CalculatedString) return "";
        if (Selection.Count == 0) return "Нет выбранного объекта: выберите объект до открытия редактора, чтобы увидеть результат.";
        try
        {
            var schema = new SchemaSnapshot(new SchemaNodReadResult(Draft, null, Array.Empty<SchemaIssue>()));
            var values = XDataV2.Read(Selection[0], schema, Types);
            var result = string.IsNullOrEmpty(parameter.Id) ? null : new ObjectCalculation(values, Culture, Types).Find(parameter.Id!);
            var source = " (" + Selection[0].EntityType + " [" + Selection[0].Handle + "])";
            if (result is null) return "Формула не вычислена" + source;
            return result.IsError ? "Ошибка: " + result.Error + source : "«" + result.Text + "»" + source;
        }
        catch (Exception error) when (!(error is OutOfMemoryException))
        {
            return "Ошибка: " + error.Message;
        }
    }

    internal bool RenameParameter(ParameterDraft item, string? value)
    {
        var old = item.Model.Id;
        value = value?.Trim() ?? "";
        if (old == value) return false;
        var used = Usage.Count(SchemaSection.Parameter, old);
        if (used != 0 && Current?.FindParameter(old) != null && !ParameterSchema.SameId(old, value))
            return Refuse("Параметр " + old + " хранится в объектах чертежа (" + used + ") — менять Id нельзя.");
        item.Model.Id = value;
        if (!string.IsNullOrEmpty(old) && SchemaValidator.IsValidId(value) && Draft.Parameters.Count(p => ParameterSchema.SameId(p.Id, old)) == 0)
        {
            foreach (var template in Draft.Templates)
                foreach (var reference in template.Parameters.Where(reference => ParameterSchema.SameId(reference.Id, old))) reference.Id = value;
            foreach (var other in Draft.Parameters)
            {
                if (other.Columns != null)
                    for (var i = 0; i < other.Columns.Count; i++)
                        if (ParameterSchema.SameId(other.Columns[i], old)) other.Columns[i] = value;
                if (ParameterSchema.SameId(other.GroupBy, old)) other.GroupBy = value;
                if (ParameterSchema.SameId(other.Key, old)) other.Key = value;
                if (other.Format != null) other.Format = RenameReference(other.Format, old!, value);
            }
            foreach (var template in Templates) template.Reload();
            foreach (var parameter in Parameters) parameter.Refresh();
        }
        Changed();
        return true;
    }

    private static string RenameReference(string format, string old, string value) =>
        Regex.Replace(format, @"(?<!@)((?:@@@@)*)@@\[" + Regex.Escape(old) + @"(:key)?\]",
            match => match.Groups[1].Value + "@@[" + value + match.Groups[2].Value + "]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---- Enums -------------------------------------------------------------------------------------------------------

    public EnumDraft AddEnum()
    {
        var model = new SchemaEnum { Id = NewId("Enum", id => Draft.FindEnum(id) != null), Caption = "Новый справочник" };
        model.Items.Add(new SchemaEnumItem("KEY1", "Значение 1"));
        Draft.Enums.Add(model);
        var item = new EnumDraft(this, model);
        Enums.Add(item);
        SelectedEnum = item;
        Status = "Добавлен справочник " + model.Id + ".";
        Changed();
        return item;
    }

    /// <summary>Refused while parameters reference the enum (TZXDATA 4.3); the message lists them.</summary>
    public bool DeleteEnum(EnumDraft? item)
    {
        if (item is null) return false;
        var users = ParametersOf(item.Model.Id);
        if (users.Count != 0)
            return Refuse("Справочник " + item.Model.Id + " используется параметрами: " + string.Join(", ", users.Select(p => p.Id)) + " — удаление запрещено.");
        var index = Enums.IndexOf(item);
        Draft.Enums.Remove(item.Model);
        Enums.Remove(item);
        ClearInputErrors(item);
        SelectedEnum = Enums.Count == 0 ? null : Enums[Math.Min(index, Enums.Count - 1)];
        Status = "Справочник " + item.Model.Id + " удалён.";
        Changed();
        return true;
    }

    public EnumKeyDraft? AddEnumKey(EnumDraft? owner)
    {
        if (owner is null) return null;
        var number = owner.Model.Items.Count + 1;
        string key;
        while (owner.Model.FindItem(key = "KEY" + number) != null) number++;
        var model = new SchemaEnumItem(key, "Значение " + number);
        var index = SelectedKey != null && ReferenceEquals(SelectedKey.Owner, owner) ? owner.Model.Items.IndexOf(SelectedKey.Model) + 1 : owner.Model.Items.Count;
        owner.Model.Items.Insert(index, model);
        var item = new EnumKeyDraft(this, owner, model);
        owner.Items.Insert(index, item);
        SelectedKey = item;
        Changed();
        return item;
    }

    /// <summary>A key stored in objects is deleted only after <see cref="Confirm"/> with the number of objects.</summary>
    public bool DeleteEnumKey(EnumKeyDraft? item)
    {
        if (item is null) return false;
        var used = item.UsageCount;
        if (used != 0 && !Ask("Ключ «" + item.Model.Key + "» справочника " + item.Owner.Model.Id + " хранится в объектах чертежа: " + used +
                ". Удалить ключ? Значения объектов не изменятся и будут показаны как недопустимые."))
            return Refuse("Ключ «" + item.Model.Key + "» используется объектами (" + used + ") — удаление отменено.");
        var index = item.Owner.Items.IndexOf(item);
        item.Owner.Model.Items.Remove(item.Model);
        item.Owner.Items.Remove(item);
        ClearInputErrors(item);
        SelectedKey = item.Owner.Items.Count == 0 ? null : item.Owner.Items[Math.Min(index, item.Owner.Items.Count - 1)];
        Status = "Ключ «" + item.Model.Key + "» удалён" + (used != 0 ? " (объектов с этим значением: " + used + ")." : ".");
        Changed();
        return true;
    }

    public void MoveEnumKey(EnumKeyDraft? item, int offset)
    {
        if (item is null || !Move(item.Owner.Model.Items, item.Model, offset)) return;
        item.Owner.Items.Move(item.Owner.Items.IndexOf(item), item.Owner.Model.Items.IndexOf(item.Model));
        SelectedKey = item;
        Changed();
    }

    /// <summary>Parameters whose <c>enum</c> is <paramref name="enumId"/>.</summary>
    public IReadOnlyList<SchemaParameter> ParametersOf(string? enumId) =>
        string.IsNullOrEmpty(enumId) ? Array.Empty<SchemaParameter>() : Draft.Parameters.Where(p => ParameterSchema.SameId(p.Enum, enumId)).ToArray();

    /// <summary>Objects storing <paramref name="key"/> in parameters of the enum (each parameter counts separately).</summary>
    public int KeyUsage(string? enumId, string? key)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in new[] { Current, Draft })
            if (schema != null)
                foreach (var parameter in schema.Parameters.Where(p => ParameterSchema.SameId(p.Enum, enumId) && !string.IsNullOrEmpty(p.Id)))
                    ids.Add(parameter.Id!);
        return ids.Sum(id => Usage.CountValue(id, key));
    }

    internal bool RenameEnum(EnumDraft item, string? value)
    {
        var old = item.Model.Id;
        value = value?.Trim() ?? "";
        if (old == value) return false;
        item.Model.Id = value;
        if (!string.IsNullOrEmpty(old) && SchemaValidator.IsValidId(value) && Draft.FindEnum(old) is null)
            foreach (var parameter in Draft.Parameters.Where(p => ParameterSchema.SameId(p.Enum, old))) parameter.Enum = value;
        foreach (var parameter in Parameters) parameter.Refresh();
        Changed();
        return true;
    }

    internal bool RenameKey(EnumKeyDraft item, string? value)
    {
        var old = item.Model.Key;
        value ??= "";
        if (old == value) return false;
        var used = item.UsageCount;
        if (used != 0 && !Ask("Ключ «" + old + "» справочника " + item.Owner.Model.Id + " хранится в объектах чертежа: " + used +
                ". Изменить ключ? Значения объектов не изменятся и будут показаны как недопустимые."))
            return Refuse("Ключ «" + old + "» используется объектами (" + used + ") — изменение отменено.");
        item.Model.Key = value;
        Changed();
        return true;
    }

    // ---- Templates ---------------------------------------------------------------------------------------------------

    public TemplateDraft AddTemplate()
    {
        var model = new SchemaTemplate { Id = NewId("Template", id => Draft.FindTemplate(id) != null), Caption = "Новый шаблон" };
        Draft.Templates.Add(model);
        var item = new TemplateDraft(this, model);
        Templates.Add(item);
        SelectedTemplate = item;
        Status = "Добавлен шаблон " + model.Id + ".";
        Changed();
        return item;
    }

    /// <summary>Refused for a template assigned to objects of the drawing.</summary>
    public bool DeleteTemplate(TemplateDraft? item)
    {
        if (item is null) return false;
        var used = Usage.Count(SchemaSection.Template, item.Model.Id);
        if (used != 0 && Current?.FindTemplate(item.Model.Id) != null)
            return Refuse("Шаблон " + item.Model.Id + " назначен объектам чертежа (" + used + ") — удаление запрещено.");
        var index = Templates.IndexOf(item);
        Draft.Templates.Remove(item.Model);
        Templates.Remove(item);
        ClearInputErrors(item);
        SelectedTemplate = Templates.Count == 0 ? null : Templates[Math.Min(index, Templates.Count - 1)];
        Status = "Шаблон " + item.Model.Id + " удалён.";
        Changed();
        return true;
    }

    public TemplateParameterDraft? AddToTemplate(ParameterDraft? parameter, TemplateDraft? template = null)
    {
        template ??= SelectedTemplate;
        if (template is null || parameter is null || string.IsNullOrEmpty(parameter.Model.Id)) return null;
        if (template.Model.Parameters.Any(item => ParameterSchema.SameId(item.Id, parameter.Model.Id))) return null;
        var model = new TemplateParameter(parameter.Model.Id!);
        var index = SelectedTemplateParameter != null && ReferenceEquals(SelectedTemplateParameter.Owner, template)
            ? template.Model.Parameters.IndexOf(SelectedTemplateParameter.Model) + 1 : template.Model.Parameters.Count;
        template.Model.Parameters.Insert(index, model);
        var item = new TemplateParameterDraft(this, template, model);
        template.Parameters.Insert(index, item);
        if (ReferenceEquals(template, SelectedTemplate)) UpdateAvailable();
        SelectedTemplateParameter = item;
        Changed();
        return item;
    }

    public bool RemoveFromTemplate(TemplateParameterDraft? item)
    {
        if (item is null) return false;
        var index = item.Owner.Parameters.IndexOf(item);
        item.Owner.Model.Parameters.Remove(item.Model);
        item.Owner.Parameters.Remove(item);
        ClearInputErrors(item);
        if (ReferenceEquals(item.Owner, SelectedTemplate)) UpdateAvailable();
        SelectedTemplateParameter = item.Owner.Parameters.Count == 0 ? null : item.Owner.Parameters[Math.Min(index, item.Owner.Parameters.Count - 1)];
        Changed();
        return true;
    }

    public void MoveTemplateParameter(TemplateParameterDraft? item, int offset)
    {
        if (item is null || !Move(item.Owner.Model.Parameters, item.Model, offset)) return;
        item.Owner.Parameters.Move(item.Owner.Parameters.IndexOf(item), item.Owner.Model.Parameters.IndexOf(item.Model));
        SelectedTemplateParameter = item;
        Changed();
    }

    internal bool RenameTemplate(TemplateDraft item, string? value)
    {
        var old = item.Model.Id;
        value = value?.Trim() ?? "";
        if (old == value) return false;
        var used = Usage.Count(SchemaSection.Template, old);
        if (used != 0 && Current?.FindTemplate(old) != null && !ParameterSchema.SameId(old, value))
            return Refuse("Шаблон " + old + " назначен объектам чертежа (" + used + ") — менять Id нельзя.");
        item.Model.Id = value;
        Changed();
        return true;
    }

    // ---- Import / export / apply -------------------------------------------------------------------------------------

    /// <summary>
    /// Imports <c>schema.json</c> into the draft like <c>VELEC_SCHEMA_IMPORT</c> (TZXDATA 5.3): Merge keeps the types
    /// of parameters that differ (they are listed in the report), Replace takes the file as is. Returns the report.
    /// </summary>
    public IReadOnlyList<string> ImportJson(string json, SchemaImportMode mode)
    {
        var loaded = ParameterSchemaJson.Load(json, Types);
        var lines = loaded.Issues.Select(issue => issue.ToString()).ToList();
        if (loaded.Schema is null || loaded.HasErrors)
        {
            lines.Add("Файл содержит ошибки; черновик не изменён.");
            Status = lines[lines.Count - 1];
            return lines;
        }
        var plan = SchemaImport.Plan(Draft, loaded.Schema, mode);
        var issues = plan.Validate(Types);
        lines.AddRange(SchemaImportReport.Format(plan, issues, Usage));
        var protectedObjects = RemovedButUsed(plan.BuildResult());
        if (protectedObjects.Count != 0)
        {
            lines.Add("Импорт удалил бы используемые объекты: " + string.Join(", ", protectedObjects) + "; черновик не изменён.");
            Status = lines[lines.Count - 1];
            return lines;
        }
        Draft = plan.BuildResult();
        inputErrors.Clear();
        Reload();
        Status = "Схема импортирована в черновик (" + (mode == SchemaImportMode.Merge ? "объединение" : "замена") + "); «Применить» запишет её в чертёж.";
        lines.Add(Status);
        return lines;
    }

    /// <summary>The draft as canonical <c>schema.json</c> (TZXDATA 5.3, <c>VELEC_SCHEMA_EXPORT</c>).</summary>
    public string ExportJson() => ParameterSchemaJson.Write(Draft);

    /// <summary>
    /// «Применить»: writes the draft to NOD through <paramref name="transaction"/> — one commit, one Undo step.
    /// Throws when the draft has errors (TZXDATA 4.5).
    /// </summary>
    public SchemaNodWriteResult Apply(ISchemaNodTransaction transaction)
    {
        if (HasErrors) throw new InvalidOperationException("Схема содержит ошибки: " + string.Join("; ", issues.Where(i => i.Severity == SchemaIssueSeverity.Error)));
        var info = new SchemaNodInfo(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), StampSource, StampMode);
        return SchemaNodWriter.Write(transaction, Draft, info, Types);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    /// <summary>Revalidates the draft after an edit.</summary>
    internal void Changed()
    {
        if (suspended != 0) return;
        changes = SchemaImport.Compare(Current, Draft);
        var list = new SchemaValidator(Types).Validate(Draft).ToList();
        list.AddRange(inputErrors.Values);
        list.AddRange(TypeWarnings());
        issues = list;
        isDirty = ParameterSchemaJson.Write(Draft) != currentJson;
        enumIds = Draft.Enums.Where(e => !string.IsNullOrEmpty(e.Id)).Select(e => e.Id!).ToArray();
        parameterIds = Draft.Parameters.Where(p => !string.IsNullOrEmpty(p.Id)).Select(p => p.Id!).ToArray();
        foreach (var name in new[] { nameof(Issues), nameof(ErrorCount), nameof(HasErrors), nameof(Changes), nameof(ChangeLines),
                     nameof(IsDirty), nameof(CanApply), nameof(Summary), nameof(EnumIds), nameof(ParameterIds) })
            Notify(name);
        SelectedParameter?.Refresh();
        SelectedEnum?.Refresh();
        SelectedTemplate?.Refresh();
        RaiseCommands();
    }

    internal void SetInputError(object owner, string objectPath, string field, string? message)
    {
        if (message is null) inputErrors.Remove((owner, field));
        else inputErrors[(owner, field)] = new SchemaIssue(SchemaIssueCode.InvalidFieldType, objectPath, field, message);
    }

    private void ClearInputErrors(object owner)
    {
        foreach (var key in inputErrors.Keys.Where(key => ReferenceEquals(key.Owner, owner) ||
                     (owner is TemplateDraft template && key.Owner is TemplateParameterDraft parameter && ReferenceEquals(parameter.Owner, template))).ToArray())
            inputErrors.Remove(key);
    }

    /// <summary>A stored parameter whose type changed: its values in objects may become invalid (TZXDATA 6.5).</summary>
    private IEnumerable<SchemaIssue> TypeWarnings()
    {
        if (Current is null) yield break;
        foreach (var change in changes.Where(change => change.Section == SchemaSection.Parameter && change.TypeChanged))
        {
            var used = Usage.Count(SchemaSection.Parameter, change.Id);
            if (used != 0)
                yield return new SchemaIssue(SchemaIssueCode.InvalidValue, "Параметр " + change.Id, "type",
                    "тип изменён с " + change.OldType + " на " + change.NewType + "; значения в объектах (" + used + ") будут показаны как недопустимые",
                    SchemaIssueSeverity.Warning);
        }
    }

    /// <summary>Objects of the drawing a new draft would delete although XDATA uses them.</summary>
    private IReadOnlyList<string> RemovedButUsed(ParameterSchema result) =>
        SchemaImport.Compare(Draft, result).Where(change => change.Kind == SchemaChangeKind.Removed && change.Section != SchemaSection.Enum &&
                Current != null && (change.Section == SchemaSection.Parameter ? Current.FindParameter(change.Id) : (object?)Current.FindTemplate(change.Id)) != null &&
                Usage.Count(change.Section, change.Id) != 0)
            .Select(change => (change.Section == SchemaSection.Parameter ? "параметр " : "шаблон ") + change.Id + " (" + Usage.Count(change.Section, change.Id) + ")")
            .ToArray();

    private void Reload()
    {
        suspended++;
        try
        {
            Parameters.Clear();
            foreach (var parameter in Draft.Parameters) Parameters.Add(new ParameterDraft(this, parameter));
            Enums.Clear();
            foreach (var item in Draft.Enums) Enums.Add(new EnumDraft(this, item));
            Templates.Clear();
            foreach (var template in Draft.Templates) Templates.Add(new TemplateDraft(this, template));
            Filter();
            SelectedParameter = FilteredParameters.FirstOrDefault();
            SelectedEnum = Enums.FirstOrDefault();
            SelectedTemplate = Templates.FirstOrDefault();
        }
        finally { suspended--; }
        Changed();
    }

    private void Filter()
    {
        var selected = selectedParameter;
        FilteredParameters.Clear();
        foreach (var item in Parameters.Where(item => item.Matches(parameterSearch))) FilteredParameters.Add(item);
        if (selected != null && !FilteredParameters.Contains(selected)) SelectedParameter = FilteredParameters.FirstOrDefault();
    }

    private void UpdateAvailable()
    {
        AvailableParameters.Clear();
        var template = selectedTemplate;
        if (template != null)
            foreach (var item in Parameters.Where(item => !string.IsNullOrEmpty(item.Model.Id) &&
                         !template.Model.Parameters.Any(reference => ParameterSchema.SameId(reference.Id, item.Model.Id))))
                AvailableParameters.Add(item);
        if (selectedAvailable != null && !AvailableParameters.Contains(selectedAvailable)) SelectedAvailable = null;
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { DeleteParameterCommand, MoveParameterUpCommand, MoveParameterDownCommand, InsertReferenceCommand,
                     DeleteEnumCommand, AddEnumKeyCommand, DeleteEnumKeyCommand, MoveEnumKeyUpCommand, MoveEnumKeyDownCommand,
                     DeleteTemplateCommand, AddToTemplateCommand, RemoveFromTemplateCommand, MoveTemplateParameterUpCommand, MoveTemplateParameterDownCommand })
            command?.RaiseCanExecuteChanged();
    }

    private bool Refuse(string message)
    {
        Status = message;
        return false;
    }

    private bool Ask(string message) => Confirm?.Invoke(message) == true;

    private static bool Move<T>(List<T> list, T item, int offset)
    {
        var index = list.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count) return false;
        list.RemoveAt(index);
        list.Insert(target, item);
        return true;
    }

    private static string NewId(string prefix, Func<string, bool> exists)
    {
        var number = 1;
        while (exists(prefix + number)) number++;
        return prefix + number;
    }

    private static string Describe(SchemaChange change)
    {
        var kind = change.Kind == SchemaChangeKind.Added ? "+ " : change.Kind == SchemaChangeKind.Removed ? "− " : "~ ";
        var section = change.Section == SchemaSection.Parameter ? "параметр " : change.Section == SchemaSection.Enum ? "справочник " : "шаблон ";
        return kind + section + change.Id + (change.TypeChanged ? " (тип " + change.OldType + " → " + change.NewType + ")" : "");
    }

    /// <summary>Text of a JSON default in a text box: strings as is, other values as compact JSON.</summary>
    internal static string DefaultText(JsonValue? value) => value switch
    {
        null => "",
        JsonString text => text.Value,
        _ => value.ToCompactJson()
    };

    /// <summary>
    /// Typed default for the type of <paramref name="parameter"/>: numbers in the invariant or current culture,
    /// <c>true</c>/<c>false</c>, JSON for tables; text that does not fit stays a string and the validator reports it.
    /// </summary>
    internal JsonValue? ParseDefault(string? text, SchemaParameter? parameter)
    {
        if (string.IsNullOrEmpty(text)) return null;
        switch (Types.Find(parameter?.Type)?.Kind)
        {
            case ParameterKind.Integer when long.TryParse(text!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                return new JsonNumber(integer);
            case ParameterKind.Double when TryParseDouble(text!, out var number):
                return new JsonNumber(number);
            case ParameterKind.Boolean when string.Equals(text!.Trim(), "true", StringComparison.OrdinalIgnoreCase):
                return JsonBoolean.True;
            case ParameterKind.Boolean when string.Equals(text!.Trim(), "false", StringComparison.OrdinalIgnoreCase):
                return JsonBoolean.False;
            case ParameterKind.Table:
                try { return JsonValue.Parse(text!); }
                catch (JsonException) { return new JsonString(text!); }
            default:
                return new JsonString(text!);
        }
    }

    internal static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>A parameter of the draft (TZXDATA 9, tab «Параметры»): the fields of TZXDATA 4.1.</summary>
public sealed class ParameterDraft : ObservableObject
{
    private readonly SchemaEditorViewModel owner;
    private readonly Dictionary<string, string> invalidText = new Dictionary<string, string>();

    internal ParameterDraft(SchemaEditorViewModel owner, SchemaParameter model) { this.owner = owner; Model = model; }

    public SchemaParameter Model { get; }
    public string Id { get => Model.Id ?? ""; set { if (owner.RenameParameter(this, value)) Notify(nameof(DisplayName)); Notify(nameof(Id)); } }
    public string Type
    {
        get => Model.Type ?? "";
        set
        {
            Model.Type = Empty(value);
            foreach (var name in new[] { nameof(Type), nameof(Kind), nameof(IsEnum), nameof(IsCalculated), nameof(IsTable), nameof(IsNumber), nameof(IsText) })
                Notify(name);
            owner.Changed();
        }
    }
    public string Caption { get => Model.Caption ?? ""; set { Model.Caption = Empty(value); Notify(nameof(Caption)); Notify(nameof(DisplayName)); owner.Changed(); } }
    public string Description { get => Model.Description ?? ""; set { Model.Description = Empty(value); Notify(nameof(Description)); owner.Changed(); } }
    public string Group { get => Model.Group ?? ""; set { Model.Group = Empty(value); Notify(nameof(Group)); owner.Changed(); } }
    public string DefaultText
    {
        get => SchemaEditorViewModel.DefaultText(Model.Default);
        set { Model.Default = owner.ParseDefault(value, Model); Notify(nameof(DefaultText)); owner.Changed(); }
    }
    public string Enum { get => Model.Enum ?? ""; set { Model.Enum = Empty(value?.Trim()); Notify(nameof(Enum)); owner.Changed(); } }
    public string Format { get => Model.Format ?? ""; set { Model.Format = Empty(value); Notify(nameof(Format)); owner.Changed(); Refresh(); } }
    public bool Persist { get => Model.Persist == true; set { Model.Persist = value ? true : (bool?)null; Notify(nameof(Persist)); owner.Changed(); } }
    /// <summary>Ids of <c>Table</c> columns separated by commas.</summary>
    public string ColumnsText
    {
        get => Model.Columns is null ? "" : string.Join(", ", Model.Columns);
        set
        {
            var columns = (value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim()).Where(id => id.Length != 0).ToList();
            Model.Columns = columns.Count == 0 ? null : columns;
            Notify(nameof(ColumnsText));
            owner.Changed();
        }
    }
    public string GroupBy { get => Model.GroupBy ?? ""; set { Model.GroupBy = Empty(value?.Trim()); Notify(nameof(GroupBy)); owner.Changed(); } }
    public string Key { get => Model.Key ?? ""; set { Model.Key = Empty(value?.Trim()); Notify(nameof(Key)); owner.Changed(); } }

    public string Unit { get => Model.Editor?.Unit ?? ""; set => SetEditor(nameof(Unit), editor => editor.Unit = Empty(value)); }
    public string PrecisionText { get => IntText(nameof(PrecisionText), Model.Editor?.Precision); set => SetInt(nameof(PrecisionText), "editor.precision", value, (e, v) => e.Precision = v); }
    public string MinText { get => DoubleText(nameof(MinText), Model.Editor?.Min); set => SetDouble(nameof(MinText), "editor.min", value, (e, v) => e.Min = v); }
    public string MaxText { get => DoubleText(nameof(MaxText), Model.Editor?.Max); set => SetDouble(nameof(MaxText), "editor.max", value, (e, v) => e.Max = v); }
    public string MaxLengthText { get => IntText(nameof(MaxLengthText), Model.Editor?.MaxLength); set => SetInt(nameof(MaxLengthText), "editor.maxLength", value, (e, v) => e.MaxLength = v); }
    public string VstColumnText { get => IntText(nameof(VstColumnText), Model.Editor?.VstColumn); set => SetInt(nameof(VstColumnText), "editor.vstColumn", value, (e, v) => e.VstColumn = v); }
    public bool ReadOnly { get => Model.Editor?.ReadOnly == true; set => SetEditor(nameof(ReadOnly), editor => editor.ReadOnly = value ? true : (bool?)null); }
    public bool Multiline { get => Model.Editor?.Multiline == true; set => SetEditor(nameof(Multiline), editor => editor.Multiline = value ? true : (bool?)null); }
    public bool AllowCustom { get => Model.Editor?.AllowCustom == true; set => SetEditor(nameof(AllowCustom), editor => editor.AllowCustom = value ? true : (bool?)null); }

    public ParameterKind? Kind => owner.Types.Find(Model.Type)?.Kind;
    public bool IsEnum => Kind == ParameterKind.Enum;
    public bool IsCalculated => Kind == ParameterKind.CalculatedString;
    public bool IsTable => Kind == ParameterKind.Table;
    public bool IsNumber => Kind == ParameterKind.Double || Kind == ParameterKind.Integer;
    public bool IsText => Kind == ParameterKind.String;

    /// <summary>«Мощность (Power)».</summary>
    public string DisplayName => string.IsNullOrEmpty(Model.Caption) || Model.Caption == Model.Id ? Model.Id ?? "" : Model.Caption + " (" + Model.Id + ")";
    /// <summary>Objects of the drawing storing the parameter.</summary>
    public int UsageCount => owner.Current?.FindParameter(Model.Id) is null ? 0 : owner.Usage.Count(SchemaSection.Parameter, Model.Id);
    public string UsageText => UsageCount == 0 ? "Не хранится в объектах чертежа" : "Хранится в объектах чертежа: " + UsageCount;
    public string TemplatesText
    {
        get
        {
            var templates = owner.TemplatesUsing(Model.Id);
            return templates.Count == 0 ? "Не входит в шаблоны" : "Шаблоны: " + string.Join(", ", templates.Select(template => template.DisplayName));
        }
    }
    public string ReferencesText
    {
        get
        {
            var references = owner.References(Model);
            return references.Count == 0 ? "" : "Используется: " + string.Join(", ", references);
        }
    }
    /// <summary>Result of the formula on the selected object (TZXDATA 9: предпросмотр).</summary>
    public string FormulaPreview => owner.FormulaPreview(Model);

    public bool Matches(string? search) =>
        string.IsNullOrWhiteSpace(search) || new[] { Model.Id, Model.Caption, Model.Group, Model.Type, Model.Description }
            .Any(text => text != null && text.IndexOf(search!.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0);

    internal void Refresh()
    {
        foreach (var name in new[] { nameof(DisplayName), nameof(UsageCount), nameof(UsageText), nameof(TemplatesText), nameof(ReferencesText), nameof(FormulaPreview),
                     nameof(Enum), nameof(Format), nameof(ColumnsText), nameof(GroupBy), nameof(Key) })
            Notify(name);
    }

    private string ObjectPath => "Параметр " + (string.IsNullOrEmpty(Model.Id) ? "(без Id)" : Model.Id);

    private void SetEditor(string property, Action<ParameterEditor> set)
    {
        var editor = Model.Editor ?? new ParameterEditor();
        set(editor);
        Model.Editor = editor.Unit is null && editor.Precision is null && editor.Min is null && editor.Max is null && editor.MaxLength is null &&
            editor.ReadOnly is null && editor.Multiline is null && editor.AllowCustom is null && editor.VstColumn is null && editor.Extra.Count == 0 ? null : editor;
        Notify(property);
        owner.Changed();
    }

    private string IntText(string property, int? value) =>
        invalidText.TryGetValue(property, out var text) ? text : value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private string DoubleText(string property, double? value) =>
        invalidText.TryGetValue(property, out var text) ? text : value?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    private void SetInt(string property, string field, string? text, Action<ParameterEditor, int?> set) =>
        SetNumber(property, field, text, "ожидается целое число", value =>
            int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : (int?)null, set);

    private void SetDouble(string property, string field, string? text, Action<ParameterEditor, double?> set) =>
        SetNumber(property, field, text, "ожидается число", value =>
            SchemaEditorViewModel.TryParseDouble(value, out var number) ? number : (double?)null, set);

    /// <summary>Text that is not a number stays in the box and is reported until it is corrected.</summary>
    private void SetNumber<T>(string property, string field, string? text, string error, Func<string, T?> parse, Action<ParameterEditor, T?> set) where T : struct
    {
        text ??= "";
        T? value = null;
        if (text.Trim().Length != 0 && (value = parse(text)) is null)
        {
            invalidText[property] = text;
            owner.SetInputError(this, ObjectPath, field, error + ": «" + text + "»");
            Notify(property);
            owner.Changed();
            return;
        }
        invalidText.Remove(property);
        owner.SetInputError(this, ObjectPath, field, null);
        SetEditor(property, editor => set(editor, value));
    }

    internal static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>An enum of the draft (TZXDATA 9, tab «ENUM»).</summary>
public sealed class EnumDraft : ObservableObject
{
    private readonly SchemaEditorViewModel owner;

    internal EnumDraft(SchemaEditorViewModel owner, SchemaEnum model)
    {
        this.owner = owner;
        Model = model;
        foreach (var item in model.Items) Items.Add(new EnumKeyDraft(owner, this, item));
    }

    public SchemaEnum Model { get; }
    public ObservableCollection<EnumKeyDraft> Items { get; } = new ObservableCollection<EnumKeyDraft>();
    public string Id { get => Model.Id ?? ""; set { owner.RenameEnum(this, value); Notify(nameof(Id)); Notify(nameof(DisplayName)); } }
    public string Caption { get => Model.Caption ?? ""; set { Model.Caption = ParameterDraft.Empty(value); Notify(nameof(Caption)); Notify(nameof(DisplayName)); owner.Changed(); } }
    public string DisplayName => string.IsNullOrEmpty(Model.Caption) || Model.Caption == Model.Id ? Model.Id ?? "" : Model.Caption + " (" + Model.Id + ")";
    public string UsedByText
    {
        get
        {
            var parameters = owner.ParametersOf(Model.Id);
            return parameters.Count == 0 ? "Не используется параметрами" : "Используется параметрами: " + string.Join(", ", parameters.Select(p => p.Id));
        }
    }

    internal void Refresh()
    {
        Notify(nameof(UsedByText));
        foreach (var item in Items) item.Refresh();
    }
}

/// <summary>A key of an enum: <c>Key</c> is stored in XDATA, <c>Caption</c> is shown.</summary>
public sealed class EnumKeyDraft : ObservableObject
{
    private readonly SchemaEditorViewModel owner;

    internal EnumKeyDraft(SchemaEditorViewModel owner, EnumDraft enumDraft, SchemaEnumItem model) { this.owner = owner; Owner = enumDraft; Model = model; }

    public EnumDraft Owner { get; }
    public SchemaEnumItem Model { get; }
    public string Key { get => Model.Key ?? ""; set { owner.RenameKey(this, value); Notify(nameof(Key)); Notify(nameof(UsageCount)); Notify(nameof(UsageText)); } }
    public string Caption { get => Model.Caption ?? ""; set { Model.Caption = value ?? ""; Notify(nameof(Caption)); owner.Changed(); } }
    /// <summary>Objects storing the key in parameters of the enum.</summary>
    public int UsageCount => owner.KeyUsage(Owner.Model.Id, Model.Key);
    public string UsageText => UsageCount == 0 ? "" : "объектов: " + UsageCount;

    internal void Refresh() { Notify(nameof(UsageCount)); Notify(nameof(UsageText)); }
}

/// <summary>A template of the draft (TZXDATA 9, tab «Шаблоны»).</summary>
public sealed class TemplateDraft : ObservableObject
{
    private readonly SchemaEditorViewModel owner;

    internal TemplateDraft(SchemaEditorViewModel owner, SchemaTemplate model)
    {
        this.owner = owner;
        Model = model;
        Reload();
    }

    public SchemaTemplate Model { get; }
    /// <summary>«Параметры шаблона» in their order.</summary>
    public ObservableCollection<TemplateParameterDraft> Parameters { get; } = new ObservableCollection<TemplateParameterDraft>();
    public string Id { get => Model.Id ?? ""; set { owner.RenameTemplate(this, value); Notify(nameof(Id)); Notify(nameof(DisplayName)); } }
    public string Caption { get => Model.Caption ?? ""; set { Model.Caption = ParameterDraft.Empty(value); Notify(nameof(Caption)); Notify(nameof(DisplayName)); owner.Changed(); } }
    /// <summary>Entity types separated by commas; empty — any object.</summary>
    public string EntityTypesText
    {
        get => Model.EntityTypes is null ? "" : string.Join(", ", Model.EntityTypes);
        set
        {
            var types = (value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(type => type.Trim()).Where(type => type.Length != 0).ToList();
            Model.EntityTypes = types.Count == 0 ? null : types;
            Notify(nameof(EntityTypesText));
            owner.Changed();
        }
    }
    public string DisplayName => Model.DisplayName;
    public int UsageCount => owner.Current?.FindTemplate(Model.Id) is null ? 0 : owner.Usage.Count(SchemaSection.Template, Model.Id);
    public string UsageText => UsageCount == 0 ? "Не назначен объектам чертежа" : "Назначен объектам чертежа: " + UsageCount;

    internal void Reload()
    {
        Parameters.Clear();
        foreach (var parameter in Model.Parameters) Parameters.Add(new TemplateParameterDraft(owner, this, parameter));
    }

    internal void Refresh()
    {
        Notify(nameof(UsageCount));
        Notify(nameof(UsageText));
        foreach (var parameter in Parameters) parameter.Refresh();
    }
}

/// <summary>A parameter reference of a template with an optional default override.</summary>
public sealed class TemplateParameterDraft : ObservableObject
{
    private readonly SchemaEditorViewModel owner;

    internal TemplateParameterDraft(SchemaEditorViewModel owner, TemplateDraft template, TemplateParameter model) { this.owner = owner; Owner = template; Model = model; }

    public TemplateDraft Owner { get; }
    public TemplateParameter Model { get; }
    public string Id => Model.Id ?? "";
    public SchemaParameter? Parameter => owner.Draft.FindParameter(Model.Id);
    public string DisplayName
    {
        get
        {
            var parameter = Parameter;
            if (parameter is null) return Id + " — нет в схеме";
            return string.IsNullOrEmpty(parameter.Caption) || parameter.Caption == parameter.Id ? Id : parameter.Caption + " (" + Id + ")";
        }
    }
    /// <summary>Default of the template; empty — the default of the parameter (<see cref="InheritedDefault"/>).</summary>
    public string DefaultText
    {
        get => SchemaEditorViewModel.DefaultText(Model.Default);
        set { Model.Default = owner.ParseDefault(value, Parameter); Notify(nameof(DefaultText)); Notify(nameof(DefaultSummary)); owner.Changed(); }
    }
    public string InheritedDefault => SchemaEditorViewModel.DefaultText(Parameter?.Default);
    public string DefaultSummary => Model.Default is null
        ? (Parameter?.Default is null ? "без значения по умолчанию" : "по умолчанию параметра: " + InheritedDefault)
        : "переопределено: " + DefaultText;

    internal void Refresh()
    {
        Notify(nameof(Id)); Notify(nameof(DisplayName)); Notify(nameof(InheritedDefault)); Notify(nameof(DefaultSummary));
    }
}
