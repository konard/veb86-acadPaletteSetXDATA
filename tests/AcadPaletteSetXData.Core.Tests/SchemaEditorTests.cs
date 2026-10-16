using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// TZXDATA stage 8: the schema editor (section 9) — parameters, enums, templates, import/export, the draft and «Применить».
/// Acceptance: a parameter, enum or template created without changing code is available in VST at once; objects used by
/// the drawing cannot be deleted; «Применить» is one Undo step.
/// </summary>
public sealed class SchemaEditorTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");

    [Fact]
    public void TheGearButtonRunsOnlySchemaCommandsThroughTheHost()
    {
        var grid = new PropertyGridViewModel(new SelectionViewModel(new PropertyEditorViewModel()), CultureInfo.InvariantCulture);
        Assert.False(grid.CanRunSchemaCommands);
        Assert.False(grid.SchemaCommand.CanExecute(null));
        Assert.False(grid.RunSchemaCommand(PropertyGridViewModel.SchemaEdit));

        var started = new List<string>();
        var canExecuteChanged = 0;
        grid.SchemaCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        grid.SchemaCommandRunner = started.Add;
        Assert.True(grid.CanRunSchemaCommands);
        Assert.True(grid.SchemaCommand.CanExecute(null));
        Assert.Equal(1, canExecuteChanged);

        grid.SchemaCommand.Execute(null);
        Assert.True(grid.RunSchemaCommand("velec_schema_import"));
        grid.SchemaCommand.Execute(PropertyGridViewModel.SchemaExport);
        Assert.False(grid.RunSchemaCommand("ERASE"));
        Assert.Equal(new[] { "VELEC_SCHEMA_EDIT", "VELEC_SCHEMA_IMPORT", "VELEC_SCHEMA_EXPORT" }, started);
    }

    [Fact]
    public void CreatedParameterEnumAndTemplateAreAvailableInVstAfterApply()
    {
        var nod = new FakeNod(Example());
        var cache = new SchemaCache(nod.Read);
        var drawing = new Drawing(new ObjectValuesCache(cache), Device("A", Pair("Power", 1.5)));
        var (_, grid, controller) = Open(drawing);
        using (controller)
        {
            Assert.DoesNotContain(All(grid.Roots), row => row.Id == "Mode");
            var editor = Editor(cache.Get().Schema, drawing.Snapshots);

            var modes = editor.AddEnum();
            modes.Id = "Mode";
            modes.Caption = "Режим";
            modes.Items[0].Key = "AUTO";
            modes.Items[0].Caption = "Автоматический";
            var manual = editor.AddEnumKey(modes)!;
            manual.Key = "MANUAL";
            manual.Caption = "Ручной";

            var mode = editor.AddParameter();
            mode.Id = "Mode";
            mode.Type = "Enum";
            mode.Caption = "Режим работы";
            mode.Group = "Электрика";
            mode.Enum = "Mode";
            mode.DefaultText = "AUTO";
            editor.SelectedTemplate = editor.FindTemplate("DEVICE");
            Assert.Contains(editor.FindParameter("Mode"), editor.AvailableParameters);
            editor.SelectedAvailable = editor.FindParameter("Mode");
            editor.AddToTemplateCommand.Execute(null);
            Assert.DoesNotContain(editor.FindParameter("Mode"), editor.AvailableParameters);

            var panel = editor.AddTemplate();
            panel.Id = "PANEL";
            panel.Caption = "Щит";
            panel.EntityTypesText = "BlockReference";
            editor.AddToTemplate(editor.FindParameter("Device_Type"), panel);
            editor.AddToTemplate(editor.FindParameter("Mode"), panel)!.DefaultText = "MANUAL";

            Assert.Empty(editor.Issues);
            Assert.True(editor.CanApply);
            Assert.Equal(new[] { "+ параметр Mode", "+ справочник Mode", "~ шаблон DEVICE", "+ шаблон PANEL" }, editor.ChangeLines);
            var result = nod.Apply(editor);
            Assert.True(result.Changed);
            Assert.Equal(1, nod.Commits); // One transaction, one Undo step.
            // Records of the following parameters are rewritten too: they keep the order of the list.
            Assert.Superset(new HashSet<string> { "INFO", "ENUMS/Mode", "PARAMETERS/Mode", "TEMPLATES/DEVICE", "TEMPLATES/PANEL" }, new HashSet<string>(result.Written));
            Assert.DoesNotContain("TEMPLATES/CABLE", result.Written);
            Assert.Empty(result.Erased);

            // AutoCadSchemaCaches invalidates the cache after the command; the palette rebuilds with the new schema.
            cache.Invalidate();
            grid.Refresh();
            var row = Row(grid, "Mode");
            Assert.Equal("Режим работы", row.Caption);
            Assert.Equal("Автоматический", row.Value);
            Assert.Equal(PropertyState.Default, row.State);
            Assert.Equal(new[] { "AUTO", "MANUAL" }, row.Options.Select(option => option.Key));
            Assert.Contains(grid.AvailableTemplates, option => option.Key == "PANEL" && option.Caption == "Щит (PANEL)");

            var stamp = nod.Read().Info!;
            Assert.Equal(SchemaEditorViewModel.StampSource, stamp.Source);
            Assert.Equal(SchemaEditorViewModel.StampMode, stamp.Mode);
        }
    }

    [Fact]
    public void ObjectsUsedByTheDrawingCannotBeDeleted()
    {
        var objects = new[] { Device("A", Pair("Power", 1.5), Pair("Voltage", "_AC_220V_50Hz")), Device("B", Pair("Voltage", "_AC_220V_50Hz")) };
        var editor = Editor(Example(), objects);
        var power = editor.FindParameter("Power")!;
        Assert.Equal(1, power.UsageCount);
        Assert.False(editor.DeleteParameter(power));
        Assert.Equal("Параметр Power хранится в объектах чертежа (1) — удаление запрещено.", editor.Status);
        // A parameter of the schema (templates, tables, formulas) is not deleted while it is referenced.
        Assert.False(editor.DeleteParameter(editor.FindParameter("Motor_Speed")));
        Assert.Equal("Параметр Motor_Speed используется: шаблон MOTOR — сначала уберите ссылки.", editor.Status);
        Assert.False(editor.DeleteParameter(editor.FindParameter("GC_HDGroup")));
        Assert.Contains("формула GC_Name", editor.Status);
        Assert.False(editor.DeleteParameter(editor.FindParameter("Mat_Mark")));
        Assert.Contains("таблица Materials", editor.Status);
        // TZXDATA 4.3: an enum referenced by parameters; the message lists them.
        Assert.False(editor.DeleteEnum(editor.FindEnum("Voltage")));
        Assert.Equal("Справочник Voltage используется параметрами: Voltage — удаление запрещено.", editor.Status);
        // A template assigned to objects.
        Assert.Equal(2, editor.FindTemplate("DEVICE")!.UsageCount);
        Assert.False(editor.DeleteTemplate(editor.FindTemplate("DEVICE")));
        Assert.Equal("Шаблон DEVICE назначен объектам чертежа (2) — удаление запрещено.", editor.Status);
        // Ids of stored objects cannot change: XDATA keeps the old Id.
        power.Id = "Power2";
        Assert.Equal("Power", power.Id);
        Assert.Contains("менять Id нельзя", editor.Status);
        Assert.False(editor.IsDirty);

        // Unused objects are deleted.
        Assert.True(editor.DeleteTemplate(editor.FindTemplate("MOTOR")));
        Assert.True(editor.DeleteParameter(editor.FindParameter("Motor_Speed")));
        Assert.True(editor.DeleteParameter(editor.FindParameter("Poles")));
        Assert.True(editor.IsDirty);
        Assert.Empty(editor.Issues);
        Assert.Equal(new[] { "− параметр Poles", "− параметр Motor_Speed", "− шаблон MOTOR" }, editor.ChangeLines);
    }

    [Fact]
    public void AnEnumKeyStoredInObjectsIsDeletedOnlyAfterConfirmationWithTheNumberOfObjects()
    {
        var objects = new[] { Device("A", Pair("Voltage", "_AC_220V_50Hz")), Device("B", Pair("Voltage", "_AC_220V_50Hz")), Device("C", Pair("Voltage", "_DC_24V")) };
        var editor = Editor(Example(), objects);
        var voltage = editor.FindEnum("Voltage")!;
        Assert.Equal("Используется параметрами: Voltage", voltage.UsedByText);
        var used = voltage.Items.Single(item => item.Key == "_AC_220V_50Hz");
        Assert.Equal(2, used.UsageCount);
        Assert.Equal("объектов: 2", used.UsageText);
        Assert.Equal(0, voltage.Items.Single(item => item.Key == "_AC_380V_50Hz").UsageCount);

        Assert.False(editor.DeleteEnumKey(used)); // No confirmation available: refused.
        Assert.Contains("используется объектами (2)", editor.Status);
        var questions = new List<string>();
        editor.Confirm = question => { questions.Add(question); return false; };
        Assert.False(editor.DeleteEnumKey(used));
        used.Key = "AC220";
        Assert.Equal("_AC_220V_50Hz", used.Key);
        Assert.Equal(2, questions.Count);
        Assert.Contains("хранится в объектах чертежа: 2", questions[0]);
        Assert.False(editor.IsDirty);

        editor.Confirm = _ => true;
        Assert.True(editor.DeleteEnumKey(used));
        Assert.Equal("Ключ «_AC_220V_50Hz» удалён (объектов с этим значением: 2).", editor.Status);
        Assert.Equal(new[] { "_AC_380V_50Hz", "_DC_24V" }, editor.Draft.FindEnum("Voltage")!.Items.Select(item => item.Key));
        // The default of Voltage refers to the deleted key: «Применить» is blocked until it is corrected (TZXDATA 4.5).
        Assert.Contains(editor.Issues, issue => issue.Object == "Параметр Voltage" && issue.Field == "default");
        Assert.False(editor.CanApply);
        editor.FindParameter("Voltage")!.DefaultText = "_AC_380V_50Hz";
        Assert.True(editor.CanApply);

        // Unused keys: added, moved and deleted without questions.
        editor.Confirm = null;
        editor.SelectedEnum = voltage;
        var key = editor.AddEnumKey(voltage)!;
        Assert.Equal("KEY3", key.Key);
        editor.MoveEnumKey(key, -1);
        Assert.Equal(new[] { "_AC_380V_50Hz", "KEY3", "_DC_24V" }, voltage.Items.Select(item => item.Key));
        Assert.Equal(voltage.Items.Select(item => item.Key), editor.Draft.FindEnum("Voltage")!.Items.Select(item => item.Key));
        Assert.True(editor.DeleteEnumKey(key));
    }

    [Fact]
    public void ApplyIsOneTransactionAndCancelLeavesTheDrawingUnchanged()
    {
        var nod = new FakeNod(Example());
        var before = nod.Records();

        var cancelled = Editor(nod.Read().Schema, Array.Empty<EntityDataSnapshot>());
        cancelled.AddParameter();
        cancelled.FindParameter("Power")!.Caption = "Активная мощность";
        Assert.True(cancelled.IsDirty);
        // «Отмена»: the draft is dropped, nothing is written.
        Assert.Equal(before, nod.Records());
        Assert.Equal(0, nod.Commits);

        var editor = Editor(nod.Read().Schema, Array.Empty<EntityDataSnapshot>());
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanApply);
        var power = editor.FindParameter("Power")!;
        power.Caption = "Активная мощность";
        power.Unit = "Вт";
        power.PrecisionText = "0";
        editor.MoveParameter(power, -1);
        editor.FindTemplate("TRAY")!.Caption = "Кабельный лоток";
        editor.AddParameter().Id = "Room";
        Assert.True(editor.CanApply);
        var result = nod.Apply(editor);
        Assert.Equal(1, nod.Commits);
        Assert.Equal(ParameterSchemaJson.Write(editor.Draft), ParameterSchemaJson.Write(nod.Read().Schema!));
        Assert.Superset(new HashSet<string> { "INFO", "PARAMETERS/Current", "PARAMETERS/Power", "PARAMETERS/Room", "TEMPLATES/TRAY" }, new HashSet<string>(result.Written));
        Assert.DoesNotContain("TEMPLATES/DEVICE", result.Written);
        Assert.Empty(result.Erased);
        Assert.Equal(1, result.Written.Count(path => path == "INFO"));

        // A draft with errors is never written.
        var invalid = Editor(nod.Read().Schema, Array.Empty<EntityDataSnapshot>());
        invalid.FindParameter("Power")!.Type = "Decimal";
        invalid.FindParameter("Room")!.Id = "Power";
        Assert.True(invalid.HasErrors);
        Assert.False(invalid.CanApply);
        Assert.Throws<InvalidOperationException>(() => nod.Apply(invalid));
        Assert.Equal(1, nod.Commits);
    }

    [Fact]
    public void TheDraftIsValidatedWhileEditing()
    {
        var editor = Editor(Example(), Array.Empty<EntityDataSnapshot>());
        var item = editor.AddParameter();
        Assert.Equal("Param1", item.Id);
        item.Id = "1st";
        Assert.Contains(editor.Issues, issue => issue.Object == "Параметр 1st" && issue.Field == "id");
        Assert.False(editor.CanApply);
        item.Id = "First";
        Assert.Empty(editor.Issues);

        item.Type = "Integer";
        item.DefaultText = "12";
        Assert.Equal(new JsonNumber(12), item.Model.Default, JsonComparer.Instance);
        item.DefaultText = "двенадцать";
        Assert.Contains(editor.Issues, issue => issue.Object == "Параметр First" && issue.Field == "default");
        item.DefaultText = "";
        Assert.Null(item.Model.Default);

        // A value that is not a number stays in the box and blocks «Применить» until corrected.
        item.Type = "Double";
        item.PrecisionText = "два";
        Assert.Equal("два", item.PrecisionText);
        var issue = Assert.Single(editor.Issues);
        Assert.Equal("Параметр First — editor.precision — ожидается целое число: «два»", issue.ToString());
        Assert.False(editor.CanApply);
        item.PrecisionText = "2";
        Assert.Empty(editor.Issues);
        Assert.Equal(2, item.Model.Editor!.Precision);
        item.PrecisionText = "";
        Assert.Null(item.Model.Editor); // An empty editor is not written.

        item.MinText = "0,5";
        item.MaxText = "0.25";
        Assert.Contains(editor.Issues, problem => problem.Object == "Параметр First" && problem.Field == "editor.min");
        item.MaxText = "10";
        Assert.Empty(editor.Issues);

        // The type of a stored parameter changes with a warning: «Применить» stays available.
        var objects = Editor(Example(), new[] { Device("A", Pair("Device_Class", "ЩР")) });
        objects.FindParameter("Device_Class")!.Type = "Integer";
        var warning = Assert.Single(objects.Issues);
        Assert.Equal(SchemaIssueSeverity.Warning, warning.Severity);
        Assert.Contains("значения в объектах (1)", warning.Message);
        Assert.True(objects.CanApply);
    }

    [Fact]
    public void TheParameterListIsSearchedAndShowsTemplatesAndReferences()
    {
        var editor = Editor(Example(), Array.Empty<EntityDataSnapshot>());
        Assert.Equal(editor.Parameters.Count, editor.FilteredParameters.Count);
        editor.ParameterSearch = "кабел";
        Assert.Equal(new[] { "Cable_Mark", "Cable_Cores", "Cable_Section", "Cable_Length" }, editor.FilteredParameters.Select(item => item.Id));
        editor.ParameterSearch = "Mat_";
        Assert.Equal(new[] { "Mat_Category", "Mat_Mark", "Mat_Count", "Mat_InSpec", "Mat_Comment" }, editor.FilteredParameters.Select(item => item.Id));
        Assert.Equal("Mat_Category", editor.SelectedParameter!.Id);
        // A new parameter that does not match the search clears it so that it is visible.
        var added = editor.AddParameter();
        Assert.Equal("", editor.ParameterSearch);
        Assert.Same(added, editor.SelectedParameter);
        Assert.Equal(editor.Parameters.IndexOf(editor.FindParameter("Mat_Category")!) + 1, editor.Parameters.IndexOf(added));

        var voltage = editor.FindParameter("Voltage")!;
        Assert.Equal("Шаблоны: Устройство (DEVICE), Кабель (CABLE), Электродвигатель (MOTOR)", voltage.TemplatesText);
        Assert.Equal("Не входит в шаблоны", added.TemplatesText);
        Assert.Equal("Используется: таблица Materials", editor.FindParameter("Mat_Category")!.ReferencesText);
        Assert.Equal(new[] { "_AC_220V_50Hz", "_AC_380V_50Hz", "_DC_24V" }, editor.FindEnum(voltage.Enum)!.Items.Select(item => item.Key));
        Assert.Contains("Voltage", editor.EnumIds);
        Assert.Contains("GC_Name", editor.ParameterIds);
        Assert.True(voltage.IsEnum);
        Assert.True(editor.FindParameter("GC_Name")!.IsCalculated);
        Assert.True(editor.FindParameter("Materials")!.IsTable);
        Assert.Equal("Mat_Category, Mat_Mark, Mat_Count, Mat_InSpec, Mat_Comment", editor.FindParameter("Materials")!.ColumnsText);
    }

    [Fact]
    public void TheFormulaIsPreviewedOnTheSelectedObjectWithTheDraft()
    {
        var selected = Device("A", Pair("GC_HeadDevice", "ЩР1"), Pair("GC_HDGroup", "2"), Pair("Power", 1.5));
        var editor = Editor(Example(), new[] { selected });
        var name = editor.FindParameter("GC_Name")!;
        Assert.Equal("«ЩР1.2» (BlockReference [A])", name.FormulaPreview);
        Assert.Equal("", editor.FindParameter("Power")!.FormulaPreview);

        editor.SelectedParameter = name;
        name.Format = "QF-";
        editor.InsertReference("GC_HDGroup");
        Assert.Equal("QF-@@[GC_HDGroup]", name.Format);
        editor.InsertReferenceCommand.Execute("Power");
        editor.InsertReference("GC_HeadDevice", 0);
        Assert.Equal("@@[GC_HeadDevice]QF-@@[GC_HDGroup]@@[Power]", name.Format);
        name.Format = "@@[GC_HeadDevice]: @@[Power] кВт";
        Assert.Equal("«ЩР1: 1.50 кВт» (BlockReference [A])", name.FormulaPreview);
        name.Format = "@@[Unknown]";
        Assert.StartsWith("Ошибка: ", name.FormulaPreview);
        Assert.Contains(editor.Issues, issue => issue.Object == "Параметр GC_Name" && issue.Field == "format");

        var nothing = Editor(Example(), Array.Empty<EntityDataSnapshot>());
        Assert.StartsWith("Нет выбранного объекта", nothing.FindParameter("GC_Name")!.FormulaPreview);
    }

    [Fact]
    public void RenamingAnUnusedObjectUpdatesItsReferences()
    {
        var editor = Editor(Example(), Array.Empty<EntityDataSnapshot>());
        editor.FindParameter("GC_HDGroup")!.Id = "GC_Group";
        Assert.Equal("@@[GC_HeadDevice].@@[GC_Group]", editor.FindParameter("GC_Name")!.Format);
        Assert.Contains(editor.Draft.FindTemplate("DEVICE")!.Parameters, item => item.Id == "GC_Group");
        editor.FindParameter("Mat_Mark")!.Id = "Mat_Code";
        Assert.Contains("Mat_Code", editor.Draft.FindParameter("Materials")!.Columns!);
        editor.FindEnum("Phase")!.Id = "Phases";
        Assert.Equal("Phases", editor.Draft.FindParameter("Phase")!.Enum);
        editor.FindTemplate("TRAY")!.Id = "CABLE_TRAY";
        Assert.Empty(editor.Issues);
        Assert.Equal(
            new[] { "+ параметр GC_Group", "~ параметр GC_Name", "~ параметр Phase", "+ параметр Mat_Code", "~ параметр Materials", "− параметр GC_HDGroup", "− параметр Mat_Mark",
                "+ справочник Phases", "− справочник Phase", "~ шаблон DEVICE", "~ шаблон CABLE", "+ шаблон CABLE_TRAY", "~ шаблон MOTOR", "− шаблон TRAY" },
            editor.ChangeLines);
    }

    [Fact]
    public void TemplatesListTheirParametersInOrderWithDefaultOverrides()
    {
        var editor = Editor(Example(), Array.Empty<EntityDataSnapshot>());
        var motor = editor.FindTemplate("MOTOR")!;
        editor.SelectedTemplate = motor;
        Assert.Equal("BlockReference", motor.EntityTypesText);
        Assert.DoesNotContain(editor.AvailableParameters, item => item.Id == "Poles");
        Assert.Contains(editor.AvailableParameters, item => item.Id == "Device_Class");
        var poles = motor.Parameters.Single(item => item.Id == "Poles");
        Assert.Equal("Число полюсов (Poles)", poles.DisplayName);
        Assert.Equal("3", poles.DefaultText);
        Assert.Equal("1", poles.InheritedDefault);
        Assert.Equal("переопределено: 3", poles.DefaultSummary);
        poles.DefaultText = "4";
        Assert.Equal(new JsonNumber(4), poles.Model.Default, JsonComparer.Instance);
        poles.DefaultText = "";
        Assert.Equal("по умолчанию параметра: 1", poles.DefaultSummary);

        editor.SelectedTemplateParameter = poles;
        editor.MoveTemplateParameterUpCommand.Execute(null);
        Assert.Equal("Poles", motor.Model.Parameters[8].Id);
        Assert.Equal(motor.Model.Parameters.Select(item => item.Id), motor.Parameters.Select(item => item.Id));
        Assert.True(editor.RemoveFromTemplate(poles));
        Assert.Contains(editor.AvailableParameters, item => item.Id == "Poles");
        motor.EntityTypesText = "BlockReference, Circle";
        Assert.Equal(new[] { "BlockReference", "Circle" }, motor.Model.EntityTypes);
        motor.EntityTypesText = " ";
        Assert.Null(motor.Model.EntityTypes);
        Assert.Empty(editor.Issues);
    }

    [Fact]
    public void ImportAndExportWorkOnTheDraft()
    {
        var editor = Editor(Example(), new[] { Device("A", Pair("Power", 1.5)) });
        Assert.Equal(ParameterSchemaJson.Write(Example()), editor.ExportJson());

        var report = editor.ImportJson("{ \"format\": \"BobrovXDATA.schema\", \"version\": 1, \"parameters\": [" +
            "{ \"id\": \"Room\", \"type\": \"String\", \"caption\": \"Помещение\" }, { \"id\": \"Power\", \"type\": \"Integer\", \"caption\": \"Мощность\" }] }", SchemaImportMode.Merge);
        Assert.Contains(report, line => line.Contains("Room"));
        Assert.Contains("Room", editor.ParameterIds);
        Assert.Equal("Double", editor.FindParameter("Power")!.Type); // Merge keeps the type of a conflicting parameter.
        Assert.True(editor.IsDirty);
        Assert.Contains("+ параметр Room", editor.ChangeLines);

        // Replace would delete Power and DEVICE, which objects use: refused, the draft is kept.
        var json = editor.ExportJson();
        report = editor.ImportJson("{ \"format\": \"BobrovXDATA.schema\", \"version\": 1, \"parameters\": [] }", SchemaImportMode.Replace);
        Assert.Contains("используемые объекты: параметр Power (1), шаблон DEVICE (1)", report.Last());
        Assert.Equal(json, editor.ExportJson());
        report = editor.ImportJson("{ \"parameters\": [", SchemaImportMode.Replace);
        Assert.Equal("Файл содержит ошибки; черновик не изменён.", report.Last());
        Assert.Equal(json, editor.ExportJson());
    }

    [Fact]
    public void UsageCountsTextValuesOfV2V1AndTableCells()
    {
        var objects = new[]
        {
            Device("A", Pair("Voltage", "_DC_24V"), Materials(("Concrete", "M1", 1, true, ""), ("Concrete", "M2", 2, true, ""))),
            Device("B", Pair("voltage", "_DC_24V"), Materials(("Fasteners", "M3", 1, true, ""))),
            new EntityDataSnapshot("C", "Line", new[] { V(1001, DrawingNames.RegApp), V(1000, "Voltage=_DC_24V") }, Array.Empty<DataRecord>())
        };
        var usage = SchemaUsage.Scan(objects.Select(item => item.XData));
        Assert.Equal(3, usage.CountValue("Voltage", "_DC_24V"));
        Assert.Equal(0, usage.CountValue("Voltage", "_dc_24v")); // Keys are exact.
        Assert.Equal(1, usage.CountValue("Mat_Category", "Concrete")); // Once per object.
        Assert.Equal(1, usage.CountValue("MAT_CATEGORY", "Fasteners"));
        Assert.Equal(0, usage.CountValue(null, "Concrete"));
        var editor = Editor(Example(), objects);
        Assert.Equal(1, editor.FindEnum("MaterialCategory")!.Items.Single(item => item.Key == "Concrete").UsageCount);
    }

    private static SchemaEditorViewModel Editor(ParameterSchema? schema, IReadOnlyList<EntityDataSnapshot> objects) =>
        new SchemaEditorViewModel(schema, SchemaUsage.Scan(objects.Select(item => item.XData)), objects, CultureInfo.InvariantCulture);

    private static ParameterSchema Example()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(ExamplePath));
        Assert.False(result.HasErrors);
        return result.Schema!;
    }

    private sealed class JsonComparer : IEqualityComparer<JsonValue?>
    {
        public static readonly JsonComparer Instance = new JsonComparer();
        public bool Equals(JsonValue? x, JsonValue? y) => JsonValue.DeepEquals(x, y);
        public int GetHashCode(JsonValue? obj) => 0;
    }

    /// <summary>NOD records of a drawing: every transaction is committed once, as one AutoCAD Undo step.</summary>
    private sealed class FakeNod
    {
        private readonly List<DataRecord> store = new List<DataRecord>();

        public FakeNod(ParameterSchema schema)
        {
            using var transaction = new Transaction(this);
            SchemaNodWriter.Write(transaction, schema, SchemaNodInfo.Now("schema.json", SchemaImportMode.Replace));
            Commits = 0;
        }

        public int Commits { get; private set; }
        public SchemaNodReadResult Read() => SchemaNod.FromRecords(store.ToList());
        public string[] Records() => store.Select(record => record.Name + ":" + string.Join("|", record.Values.Select(value => value.TypeCode + "=" + value.Value))).ToArray();

        public SchemaNodWriteResult Apply(SchemaEditorViewModel editor)
        {
            using var transaction = new Transaction(this);
            return editor.Apply(transaction);
        }

        private sealed class Transaction : ISchemaNodTransaction
        {
            private readonly FakeNod nod;
            private readonly List<Action> pending = new List<Action>();
            public Transaction(FakeNod nod) { this.nod = nod; Records = nod.store.ToList(); }
            public IReadOnlyList<DataRecord> Records { get; }
            public IReadOnlyList<string> Warnings => Array.Empty<string>();
            public void Write(DataRecord record) => pending.Add(() =>
            {
                var index = nod.store.FindIndex(existing => string.Equals(existing.Name, record.Name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) nod.store.Add(record);
                else nod.store[index] = new DataRecord(nod.store[index].Name, record.Values);
            });
            public void Erase(string path) => pending.Add(() => nod.store.RemoveAll(existing => string.Equals(existing.Name, path, StringComparison.OrdinalIgnoreCase)));
            public void Commit() { pending.ForEach(action => action()); pending.Clear(); nod.Commits++; }
            public void Dispose() => pending.Clear();
        }
    }
}
