using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #66: the «Навигатор» tab. The expression of the tree (ZCAD semantics), the values of levels and columns
/// computed by the existing <c>CalculatedString</c> mechanism, the tree builder, the profile files and the view model.
/// The schema is examples/navigator-schema.json.
/// </summary>
public sealed class NavigatorTests
{
    // ---- the expression ----

    [Fact]
    public void ParsesTheControlExpressionOfTheIssue()
    {
        var expression = NavigatorTreeExpression.Parse("-DB_link|-DESC_MountingParts|+NMO_BaseName|+NMO_Suffix|-@@[NMO_Name]");

        Assert.True(expression.IsValid);
        Assert.Equal(new[] { "DB_link", "DESC_MountingParts", "NMO_BaseName", "NMO_Suffix", "@@[NMO_Name]" }, expression.Levels.Select(level => level.Text));
        Assert.Equal(new[] { false, false, true, true, false }, expression.Levels.Select(level => level.Enabled));
        Assert.Equal(new[] { "NMO_BaseName", "NMO_Suffix" }, expression.Groups.Select(level => level.Text));
        Assert.Null(expression.Leaf); // «-@@[NMO_Name]»: leaves are named by the object type.
        Assert.Equal(new[] { "NMO_BaseName", "NMO_Suffix" }, expression.References);
        Assert.Equal(NavigatorSourceKind.Parameter, expression.Levels[2].Source.Kind);
        Assert.Equal(NavigatorSourceKind.Format, expression.Levels[4].Source.Kind);
        Assert.Equal(new[] { "NMO_Name" }, expression.Levels[4].Source.References);
    }

    [Fact]
    public void AnEnabledLastPartNamesTheLeavesAndBarsInsideReferencesDoNotSplit()
    {
        var expression = NavigatorTreeExpression.Parse(" +Phase | +@@[NMO_Name] (@@[Power] кВт) ");
        Assert.True(expression.IsValid);
        Assert.Single(expression.Groups);
        Assert.Equal("@@[NMO_Name] (@@[Power] кВт)", expression.Leaf!.Text);
        Assert.Equal(new[] { "Phase", "NMO_Name", "Power" }, expression.References);

        var formula = NavigatorTreeExpression.Parse("+=@@[Power] * 2|+@@[A|B]");
        Assert.Equal(2, formula.Levels.Count);
        Assert.Equal(NavigatorSourceKind.Formula, formula.Levels[0].Source.Kind);
    }

    [Theory]
    [InlineData("", "выражение дерева пустое")]
    [InlineData("   ", "выражение дерева пустое")]
    [InlineData("+A||+B", "уровень 2 пустой")]
    [InlineData("+A|B", "уровень 2 «B»: нужен префикс")]
    [InlineData("+A B", "уровень 1 «+A B»")]
    [InlineData("+@@[A", "уровень 1")]
    [InlineData("+=@@[A] +", "уровень 1")]
    [InlineData("+@@ text", "уровень 1")]
    public void ReportsErrorsOfTheExpression(string text, string error)
    {
        var expression = NavigatorTreeExpression.Parse(text);
        Assert.False(expression.IsValid);
        Assert.Contains(expression.Errors, item => item.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledLevelIsNotCheckedNorReferenced()
    {
        var expression = NavigatorTreeExpression.Parse("-not an id|+NMO_BaseName|-");
        Assert.True(expression.IsValid);
        Assert.Equal(new[] { "NMO_BaseName" }, expression.References);
    }

    // ---- values ----

    [Fact]
    public void ValuesAreFormattedByTheTypeOfTheParameter()
    {
        var cache = Cache();
        var target = new NavigatorObject(cache.Get(Device("1A", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "1"), Pair("Power", 1.23456),
            Pair("Phase", "B"), Pair("Count", 2), Pair("Reserve", (short)1))), "Отрезок", CultureInfo.InvariantCulture);

        Assert.Equal("ЩР1", Value(target, "@@[NMO_Name]").Text);       // a CalculatedString of the schema
        Assert.Equal("ЩР1", Value(target, "NMO_Name").Text);
        Assert.Equal("1.23", Value(target, "Power").Text);             // Double with Editor.Precision = 2
        Assert.Equal("Фаза B", Value(target, "Phase").Text);           // Enum caption
        Assert.Equal("B", Value(target, "@@[Phase:key]").Text);         // Enum key
        Assert.Equal("Да", Value(target, "Reserve").Text);             // Boolean
        Assert.StartsWith("0.617", Value(target, "PowerPerUnit").Text);
        Assert.Equal("ЩР-1 (1.23 кВт)", Value(target, "@@[NMO_BaseName]-@@[NMO_Suffix] (@@[Power] кВт)").Text);
        Assert.Equal("2.47", Value(target, "=ROUND(@@[Power] * @@[Count], 2)").Text);
        Assert.Equal("Отрезок", target.TypeName);
        Assert.Equal("1A", target.Handle);
    }

    [Fact]
    public void MissingEmptyUnknownAndFailedValuesAreDistinguished()
    {
        var cache = Cache();
        var target = new NavigatorObject(cache.Get(Device("2B", Pair("NMO_BaseName", ""), Pair("Power", 3.0), Pair("Count", 0))), null, CultureInfo.InvariantCulture);

        Assert.True(Value(target, "NMO_Suffix").IsEmpty);              // missing
        Assert.True(Value(target, "NMO_BaseName").IsEmpty);            // an empty string
        Assert.True(Value(target, "@@[NMO_Name]").IsEmpty);            // a format of empty references
        var unknown = Value(target, "No_Such_Parameter");
        Assert.True(unknown.IsEmpty);
        Assert.True(unknown.IsUnknown);
        Assert.Contains("No_Such_Parameter", unknown.Error);
        var failed = Value(target, "PowerPerUnit");                    // 3 / 0
        Assert.True(failed.IsError);
        Assert.Equal(FormulaExpression.ErrorText, failed.Text);
        Assert.NotNull(failed.Error);
        Assert.True(Value(target, "=@@[PowerPerUnit] & \"!\"").IsError); // depends on an error
        var invalid = NavigatorValueSource.Parse("not an id");
        Assert.False(invalid.IsValid);
        Assert.True(invalid.Evaluate(target).IsError);
        Assert.Equal("Line", target.TypeName);
    }

    [Fact]
    public void ValuesAreComputedOncePerObjectAndSource()
    {
        var cache = Cache();
        var target = new NavigatorObject(cache.Get(Device("3C", Pair("NMO_BaseName", "ЩР"))));
        var source = NavigatorValueSource.Parse("@@[NMO_Name]");
        Assert.False(target.IsCalculated);
        Assert.Equal("ЩР", target.Value(source).Text);
        Assert.Equal("ЩР", target.Value(NavigatorValueSource.Parse("@@[NMO_Name]")).Text);
        Assert.Equal(1, target.EvaluationCount);
        Assert.True(target.IsCalculated);
    }

    // ---- the tree ----

    [Fact]
    public void BuildsTheTreeOfTheControlExpression()
    {
        var objects = Objects(
            Device("10", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "2")),
            Device("11", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "10")),
            Device("12", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "2")),
            Device("13", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "")),
            Device("14"),
            Device("15", Pair("NMO_BaseName", "ВРУ"), Pair("NMO_Suffix", "1"), Pair("DB_link", "x")));

        var tree = NavigatorTreeBuilder.Build(objects, NavigatorTreeExpression.Parse(NavigatorProfile.ControlExpression));

        Assert.Equal(6, tree.ObjectCount);
        Assert.Equal(0, tree.ErrorCount);
        Assert.Equal(new[]
        {
            "ВРУ (1)", "  1 (1)", "    Line",
            "ЩР (4)", "  2 (2)", "    Line (2)", "      Line", "      Line", "  10 (1)", "    Line", "  Не задано (1)", "    Line",
            "Не задано (1)", "  Не задано (1)", "    Line"
        }, Lines(tree));
        var auto = tree.Nodes.Single(node => node.Kind == NavigatorNodeKind.AutoGroup);
        Assert.Equal(new[] { "10", "12" }, auto.Children.Select(node => node.Handle));
        Assert.All(tree.Nodes.Where(node => node.IsGroup), node => Assert.Null(node.Handle)); // groups are not objects
        Assert.All(tree.Leaves, node => Assert.NotNull(node.Handle));
        Assert.Equal("/g:ЩР/g:2/g:Line/o:12", tree.Leaves.Single(node => node.Handle == "12").Key);
        Assert.True(tree.Nodes.Single(node => node.Key == "/g:Не задано").IsEmptyValue);
    }

    [Fact]
    public void ErrorsOfOneObjectStayInTheErrorGroup()
    {
        var objects = Objects(
            Device("20", Pair("Power", 4.0), Pair("Count", 2), Pair("NMO_BaseName", "A")),
            Device("21", Pair("Power", 4.0), Pair("Count", 0), Pair("NMO_BaseName", "B")));

        var tree = NavigatorTreeBuilder.Build(objects, NavigatorTreeExpression.Parse("+PowerPerUnit|+NMO_BaseName"), "Пусто");

        Assert.Equal(new[] { "2 (1)", "  A", FormulaExpression.ErrorText + " (1)", "  B" }, Lines(tree));
        Assert.Equal(1, tree.ErrorCount);
        var error = tree.Root.Children.Last();
        Assert.True(error.IsError);
        Assert.StartsWith("21: ", error.Error);

        var leafError = NavigatorTreeBuilder.Build(objects, NavigatorTreeExpression.Parse("+NMO_BaseName|+PowerPerUnit"));
        var failed = leafError.Leaves.Single(node => node.Handle == "21");
        Assert.True(failed.IsError);
        Assert.Equal(FormulaExpression.ErrorText, failed.Name);
        Assert.Equal("2", leafError.Leaves.Single(node => node.Handle == "20").Name);
    }

    [Fact]
    public void UnknownParametersGoToTheEmptyGroupAndAreReported()
    {
        var cache = Cache();
        var objects = new[] { new NavigatorObject(cache.Get(Device("30", Pair("NMO_BaseName", "A")))) };
        var expression = NavigatorTreeExpression.Parse("+Unknown_Id|+NMO_BaseName");

        var tree = NavigatorTreeBuilder.Build(objects, expression, "—");

        Assert.Equal(new[] { "— (1)", "  A" }, Lines(tree));
        Assert.Equal(new[] { "Unknown_Id" }, expression.UnknownReferences(cache.Schema));
        var profile = new NavigatorProfile("p", "P", "+Unknown_Id|+NMO_BaseName");
        var validation = profile.Validate(cache.Schema);
        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, warning => warning.Contains("Unknown_Id", StringComparison.Ordinal));
    }

    [Fact]
    public void SortsNaturallyKeepingGroupsAndBothDirections()
    {
        var objects = Objects(
            Device("40", Pair("NMO_BaseName", "Щ10"), Pair("Power", 1.0)),
            Device("41", Pair("NMO_BaseName", "Щ2"), Pair("Power", 3.0)),
            Device("42", Pair("NMO_BaseName", "Щ2"), Pair("NMO_Suffix", "a"), Pair("Power", 2.0)),
            Device("43", Pair("Power", 5.0)));
        var tree = NavigatorTreeBuilder.Build(objects, NavigatorTreeExpression.Parse("+NMO_BaseName|+@@[NMO_Name]"));
        Assert.Equal(new[] { "Щ2 (2)", "  Щ2", "  Щ2a", "Щ10 (1)", "  Щ10", "Не задано (1)", "  Line" }, Lines(tree));

        tree.Sort(null, true);
        Assert.Equal(new[] { "Щ10 (1)", "  Щ10", "Щ2 (2)", "  Щ2a", "  Щ2", "Не задано (1)", "  Line" }, Lines(tree));

        var power = NavigatorValueSource.Parse("Power");
        tree.Sort(node => node.Object!.Value(power).Text, true);
        Assert.Equal(new[] { "Щ2 (2)", "  Щ2", "  Щ2a", "Щ10 (1)", "  Щ10", "Не задано (1)", "  Line" }, Lines(tree));

        Assert.True(NaturalComparer.Instance.Compare("A2", "A10") < 0);
        Assert.True(NaturalComparer.Instance.Compare("a", "B") < 0);
    }

    [Fact]
    public void TheTreeDependsOnTheExpressionOnly()
    {
        var objects = Objects(Device("50", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1")), Device("51", Pair("NMO_BaseName", "B")));
        var first = NavigatorProfile.CreateDefault();
        var second = first.Clone();
        second.Columns.Reverse();
        second.Columns.RemoveAt(0);
        second.Columns[0].Visible = false;
        Assert.Equal(Lines(NavigatorTreeBuilder.Build(objects, first.ParseTree())), Lines(NavigatorTreeBuilder.Build(objects, second.ParseTree())));
    }

    // ---- profiles and files ----

    [Fact]
    public void ProfilesRoundTripThroughJsonKeepingUnknownFields()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "navigator", "profiles", "electrical.json"));
        var profile = NavigatorProfile.Parse(text);
        Assert.Equal("electrical", profile.Id);
        Assert.Equal("Электрооборудование", profile.Name);
        Assert.Equal(NavigatorProfile.ControlExpression, profile.TreeExpression);
        Assert.Equal(7, profile.Columns.Count);
        Assert.False(profile.Columns.Single(column => column.Id == "link").Visible);
        Assert.True(profile.Validate(Cache().Schema).IsValid);
        Assert.Empty(profile.Validate(Cache().Schema).Warnings);

        var json = (JsonObject)JsonValue.Parse(profile.ToJson());
        json.Add("future", new JsonString("x"));
        var again = NavigatorProfile.Parse(json.ToJson());
        Assert.Contains("\"future\"", again.ToJson());
        Assert.Equal(profile.Columns.Select(column => (column.Id, column.Caption, column.Value, column.Width, column.Visible)),
            again.Columns.Select(column => (column.Id, column.Caption, column.Value, column.Width, column.Visible)));

        var phase = NavigatorProfile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "navigator", "profiles", "by-phase.json")));
        Assert.True(phase.Validate(Cache().Schema).IsValid);
        var settings = NavigatorSettings.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "navigator", "navigator.json")));
        Assert.Equal("electrical", settings.ActiveProfile);
    }

    [Theory]
    [InlineData("{", "некорректный JSON")]
    [InlineData("[]", "объектом JSON")]
    [InlineData("{\"id\":\"a\",\"name\":\"A\",\"tree\":{\"expression\":\"+A\"}}", "version")]
    [InlineData("{\"version\":1,\"id\":\"a\",\"name\":\"A\"}", "tree")]
    [InlineData("{\"version\":1,\"id\":\"a\",\"name\":\"A\",\"tree\":{\"expression\":\"+A\"},\"columns\":{}}", "массивом")]
    [InlineData("{\"version\":1,\"id\":\"a\",\"name\":\"A\",\"tree\":{\"expression\":\"+A\"},\"columns\":[{\"id\":\"c\",\"value\":\"A\",\"width\":\"x\"}]}", "width")]
    public void BrokenProfilesAreRejectedWithTheReason(string text, string reason)
    {
        var failure = Assert.Throws<FormatException>(() => NavigatorProfile.Parse(text));
        Assert.Contains(reason, failure.Message);
    }

    [Fact]
    public void ValidationReportsErrorsOfTheProfile()
    {
        var profile = new NavigatorProfile("bad id", " ", "+A|B", new[]
        {
            new NavigatorColumn("c", "C", "A"),
            new NavigatorColumn("c", "", "not an id", 5)
        }) { Version = 2 };
        var errors = profile.Validate().Errors;
        Assert.Contains(errors, error => error.Contains("версия"));
        Assert.Contains(errors, error => error.StartsWith("id"));
        Assert.Contains(errors, error => error.Contains("имя профиля"));
        Assert.Contains(errors, error => error.StartsWith("дерево: уровень 2"));
        Assert.Contains(errors, error => error.Contains("id повторяется"));
        Assert.Contains(errors, error => error.Contains("заголовок не задан"));
        Assert.Contains(errors, error => error.Contains("ширина"));
        Assert.Contains(errors, error => error.Contains("not an id"));
        Assert.True(NavigatorProfile.CreateDefault().Validate().IsValid);
    }

    [Fact]
    public void TheStoreGivesDefaultsWritesSafelyAndKeepsBrokenFiles()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(folder.Path);

        Assert.Empty(store.LoadProfiles());
        Assert.Equal(NavigatorSettings.DefaultTreeWidth, store.LoadSettings(out var missing).TreeWidth);
        Assert.Null(missing);
        Assert.Null(store.LoadProfile("electrical"));

        var profile = NavigatorProfile.CreateDefault();
        Assert.True(store.SaveProfile(profile, out var error), error);
        Assert.True(File.Exists(Path.Combine(folder.Path, "profiles", "electrical.json")));
        profile.Name = "Второе имя";
        Assert.True(store.SaveProfile(profile, out error), error); // replaces the existing file
        Assert.Equal("Второе имя", store.LoadProfile("electrical")!.Profile!.Name);
        Assert.Empty(Directory.GetFiles(store.ProfilesDirectory, "*.tmp"));

        Assert.True(store.SaveSettings(new NavigatorSettings { ActiveProfile = "electrical", TreeWidth = 300, SortColumn = "name", SortDescending = true }, out error), error);
        var settings = store.LoadSettings(out error);
        Assert.Null(error);
        Assert.Equal(("electrical", 300.0, "name", true), (settings.ActiveProfile, settings.TreeWidth, settings.SortColumn, settings.SortDescending));

        // A broken file is reported and not overwritten; the other profiles load.
        File.WriteAllText(store.ProfilePath("broken"), "{ broken");
        File.WriteAllText(store.ProfilePath("other"), new NavigatorProfile("mismatch", "M", "+A").ToJson());
        var files = store.LoadProfiles();
        Assert.Equal(3, files.Count);
        Assert.Contains("некорректный JSON", files.Single(file => file.Path.EndsWith("broken.json")).Error);
        Assert.Contains("не совпадает", files.Single(file => file.Path.EndsWith("other.json")).Error);
        Assert.NotNull(files.Single(file => file.Path.EndsWith("electrical.json")).Profile);
        Assert.Equal("{ broken", File.ReadAllText(store.ProfilePath("broken")));

        File.WriteAllText(store.SettingsPath, "not json");
        Assert.Equal(NavigatorSettings.DefaultTreeWidth, store.LoadSettings(out error).TreeWidth);
        Assert.NotNull(error);

        // An invalid profile is not written; the valid file stays.
        var invalid = NavigatorProfile.CreateDefault();
        invalid.TreeExpression = "";
        Assert.False(store.SaveProfile(invalid, out error));
        Assert.Equal("Второе имя", store.LoadProfile("electrical")!.Profile!.Name);

        Assert.True(store.DeleteProfile("electrical", out error), error);
        Assert.Null(store.LoadProfile("electrical"));
        Assert.False(store.DeleteProfile("..\\x", out _));
    }

    [Fact]
    public void AFailedWriteCleansUpTheTempFile()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "navigator.json");
        Directory.CreateDirectory(path); // the temporary file cannot be moved onto the destination
        Assert.False(NavigatorProfileStore.TryWrite(path, "{\"a\":1}", out var error));
        Assert.NotNull(error);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    // ---- the view model ----

    [Fact]
    public void TheNavigatorIsTheFourthTabAndReadsOnlyWhenShown()
    {
        var palette = new PaletteViewModel();
        var source = new FakeSource(Cache(), Device("60", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1")));
        palette.Navigator.Source = source;
        Assert.Equal("Навигатор", palette.Tabs[3].Header);
        Assert.Same(palette.Navigator, palette.Tabs[3].Content);
        Assert.Equal(0, source.Reads);

        palette.SelectedTabIndex = PaletteViewModel.NavigatorTabIndex;
        Assert.True(palette.Navigator.IsShown);
        Assert.Equal(1, source.Reads);
        Assert.Equal(new[] { "A (1)" }, palette.Navigator.Rows.Select(row => row.Caption));

        palette.SelectedTabIndex = 0;
        source.RaiseChanged(); // a hidden navigator only remembers the change
        Assert.Equal(1, source.Reads);
        palette.SelectedTabIndex = PaletteViewModel.NavigatorTabIndex;
        Assert.Equal(2, source.Reads);
        palette.SelectedTabIndex = 0;
        palette.SelectedTabIndex = PaletteViewModel.NavigatorTabIndex; // nothing changed: no read
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void ExpandsCollapsesAndKeepsExpandedGroupsAfterARefresh()
    {
        var source = new FakeSource(Cache(),
            Device("70", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1")),
            Device("71", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "2")),
            Device("72", Pair("NMO_BaseName", "B"), Pair("NMO_Suffix", "1")));
        var model = Shown(source);
        Assert.Equal(new[] { "A (2)", "B (1)" }, Captions(model));

        model.Rows[0].IsExpanded = true;
        Assert.Equal(new[] { "A (2)", "1 (1)", "2 (1)", "B (1)" }, Captions(model));
        model.Rows[1].IsExpanded = true;
        Assert.Equal(new[] { "A (2)", "1 (1)", "Line", "2 (1)", "B (1)" }, Captions(model));
        var leaf = model.Rows[2];
        Assert.True(leaf.IsLeaf);
        Assert.Equal(2, leaf.Depth);
        Assert.Equal(2 * NavigatorRow.IndentStep, leaf.Indent);
        Assert.Equal("▾", model.Rows[0].Glyph);
        Assert.Equal("", leaf.Glyph);

        model.SelectedRow = leaf;
        Assert.Equal(new[] { ("70", false) }, source.Selected);

        // A refresh builds new nodes; the expanded groups and the selected object stay.
        source.Set(Device("70", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1")),
            Device("71", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "2")),
            Device("73", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1")),
            Device("72", Pair("NMO_BaseName", "B"), Pair("NMO_Suffix", "1")));
        model.RefreshCommand.Execute(null);
        Assert.Equal(new[] { "A (3)", "1 (2)", "Line (2)", "2 (1)", "B (1)" }, Captions(model));
        Assert.Single(source.Selected); // restoring the selection does not select in AutoCAD again
        Assert.Null(model.SelectedRow); // the leaf moved into an automatic group, which is collapsed

        model.Rows[0].IsExpanded = false;
        Assert.Equal(new[] { "A (3)", "B (1)" }, Captions(model));
        model.ExpandAllCommand.Execute(null);
        Assert.Equal(10, model.Rows.Count);
        model.CollapseAllCommand.Execute(null);
        Assert.Equal(2, model.Rows.Count);
        model.ExpandTo(1);
        Assert.Equal(new[] { "A (3)", "1 (2)", "2 (1)", "B (1)", "1 (1)" }, Captions(model));
    }

    [Fact]
    public void GroupsSelectNothingLeavesSelectAndZoomAndAMissingObjectIsReported()
    {
        var source = new FakeSource(Cache(), Device("80", Pair("NMO_BaseName", "A")));
        var model = Shown(source, "+NMO_BaseName|+@@[NMO_Name]");
        model.SelectedRow = model.Rows[0];
        Assert.Empty(source.Selected);
        model.ActivateCommand.Execute(model.Rows[0]); // a group is expanded, not selected
        Assert.Equal(new[] { "A (1)", "A" }, Captions(model));
        Assert.Empty(source.Selected);

        model.ActivateCommand.Execute(model.Rows[1]);
        Assert.Equal(new[] { ("80", true) }, source.Selected);
        Assert.Empty(model.Rows[0].Cells[0].Text); // groups show their name only

        source.Erased.Add("80");
        model.SelectedRow = null;
        model.SelectedRow = model.Rows[1];
        Assert.Contains("80", model.Message);
        Assert.Contains(model.Diagnostics, line => line.Contains("не найден"));

        // The message about the selection is gone after «Обновить» and after a selection that succeeded...
        source.Erased.Clear();
        model.Refresh();
        Assert.Null(model.Message);
        model.SelectedRow = null;
        source.Erased.Add("80");
        model.SelectedRow = model.Rows[1];
        Assert.Contains("80", model.Message);
        source.Erased.Clear();
        model.SelectedRow = null;
        model.SelectedRow = model.Rows[1];
        Assert.Null(model.Message);

        // ...but a selection does not hide the message of another action, and a refresh does not either.
        Assert.False(model.SwitchProfile("missing"));
        model.SelectedRow = null;
        model.SelectedRow = model.Rows[1];
        Assert.Contains("missing", model.Message);
        model.Refresh();
        Assert.Contains("missing", model.Message);
    }

    [Fact]
    public void AGroupSelectsAllItsObjectsOnAllLevelsAndAMissingPartIsReported()
    {
        // Issue #68: the menu of a group selects every object grouped inside it, nested and automatic groups included.
        var source = new FakeSource(Cache(),
            Device("90", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1")),
            Device("91", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1")),
            Device("92", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "2")),
            Device("93", Pair("NMO_BaseName", "B"), Pair("NMO_Suffix", "1")));
        var model = Shown(source);
        model.ExpandAllCommand.Execute(null);
        Assert.Equal(new[] { "A (3)", "1 (2)", "Line (2)", "Line", "Line", "2 (1)", "Line", "B (1)", "1 (1)", "Line" }, Captions(model));

        Assert.True(model.SelectGroupCommand.CanExecute(model.Rows[0]));
        Assert.True(model.SelectGroupCommand.CanExecute(model.Rows[2])); // an automatic group
        Assert.False(model.SelectGroupCommand.CanExecute(model.Rows[3])); // an object has no such command
        Assert.Equal(new[] { "90", "91", "92" }, NavigatorViewModel.HandlesOf(model.Rows[0]));
        Assert.Equal(new[] { "90" }, NavigatorViewModel.HandlesOf(model.Rows[3]));

        model.SelectGroupCommand.Execute(model.Rows[0]);
        Assert.Equal(new[] { "90", "91", "92" }, Assert.Single(source.SelectedGroups));
        model.SelectGroupCommand.Execute(model.Rows[2]);
        Assert.Equal(new[] { "90", "91" }, source.SelectedGroups[1]);
        model.SelectGroupCommand.Execute(model.Rows[3]); // nothing for an object
        Assert.Equal(2, source.SelectedGroups.Count);
        Assert.Empty(source.Selected); // a group never selects one object
        Assert.Equal(new[] { "A (3)", "1 (2)", "Line (2)", "Line", "Line", "2 (1)", "Line", "B (1)", "1 (1)", "Line" }, Captions(model));
        Assert.Null(model.Message);

        // Without an argument the command works with the chosen row (the keyboard and the menu of the list).
        model.SelectedRow = model.Rows[7];
        Assert.True(model.SelectGroupCommand.CanExecute(null));
        model.SelectGroupCommand.Execute(null);
        Assert.Equal(new[] { "93" }, source.SelectedGroups[2]);
        model.SelectedRow = model.Rows[9];
        Assert.False(model.SelectGroupCommand.CanExecute(null));
        Assert.Equal(new[] { ("93", false) }, source.Selected);

        // Erased objects are reported, the rest is selected.
        source.Erased.Add("91");
        model.SelectGroupCommand.Execute(model.Rows[0]);
        Assert.Equal(new[] { "90", "92" }, source.SelectedGroups[3]);
        Assert.Contains("2 из 3", model.Message);
        Assert.Contains("«A»", model.Message);
        model.Refresh();
        Assert.Null(model.Message);

        source.Erased.Add("93");
        model.SelectGroupCommand.Execute(model.Rows.First(row => row.Caption == "B (1)"));
        Assert.Equal(4, source.SelectedGroups.Count); // nothing is passed to AutoCAD
        Assert.Contains("не найдены", model.Message);

        source.Fail = true;
        source.Erased.Clear();
        Assert.Null(Record.Exception(() => model.SelectGroupCommand.Execute(model.Rows[0])));
        Assert.Contains("не выбраны: нет доступа", model.Message);
    }

    [Fact]
    public void AClickOnTheChosenObjectSelectsItAgainAndActivateStillZoomsAndExpands()
    {
        // Issue #68: AutoCAD may drop the selection; a click on the row that is already chosen selects the object again.
        var source = new FakeSource(Cache(), Device("95", Pair("NMO_BaseName", "A")), Device("96", Pair("NMO_BaseName", "A")));
        var model = Shown(source, "+NMO_BaseName|+@@[NMO_Name]");
        model.SelectCommand.Execute(model.Rows[0]); // a group: nothing
        Assert.Empty(source.Selected);

        model.ActivateCommand.Execute(model.Rows[0]); // a double click on a group expands it...
        Assert.Equal(new[] { "A (2)", "A (2)" }, Captions(model)); // objects of the same name are grouped automatically
        model.ActivateCommand.Execute(model.Rows[1]); // ...an automatic group too...
        Assert.Equal(new[] { "A (2)", "A (2)", "A", "A" }, Captions(model));
        model.ActivateCommand.Execute(model.Rows[0]); // ...and collapses it again
        Assert.Equal(new[] { "A (2)" }, Captions(model));
        model.ActivateCommand.Execute(model.Rows[0]);
        Assert.Equal(new[] { "A (2)", "A (2)", "A", "A" }, Captions(model));
        Assert.Empty(source.Selected);

        model.SelectedRow = model.Rows[2];
        model.SelectCommand.Execute(model.Rows[2]);
        model.SelectCommand.Execute(null);
        Assert.Equal(new[] { ("95", false), ("95", false), ("95", false) }, source.Selected);
        model.ActivateCommand.Execute(model.Rows[3]); // a double click on an object selects and shows it
        Assert.Equal(("96", true), source.Selected[3]);
        Assert.Empty(source.SelectedGroups);
    }

    [Fact]
    public void SwitchingProfilesReusesTheParsedAndComputedValues()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(folder.Path);
        Copy(folder, "electrical.json");
        Copy(folder, "by-phase.json");
        var cache = Cache();
        var source = new FakeSource(cache,
            Device("90", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "1"), Pair("Phase", "A"), Pair("Power", 2.0), Pair("Count", 1)),
            Device("91", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "2"), Pair("Phase", "C"), Pair("Power", 1.5), Pair("Count", 0)));
        var model = new NavigatorViewModel(store) { Source = source };
        model.IsShown = true;
        var parses = cache.ParseCount;
        Assert.Equal(new[] { "by-phase", "electrical" }, model.Profiles.Select(profile => profile.Id));
        Assert.Equal("by-phase", model.Profile!.Id); // no navigator.json: the first profile
        model.ExpandAllCommand.Execute(null);
        Assert.Equal(new[] { "Фаза A (1)", "ЩР (1)", "ЩР1 (2.00 кВт)", "Фаза C (1)", "ЩР (1)", "ЩР2 (1.50 кВт)" }, Captions(model));
        var unit = model.Columns.Single(column => column.Id == "unit");
        Assert.Equal(FormulaExpression.ErrorText, model.Rows[5].Cells[unit.Slot].Text);
        Assert.True(model.Rows[5].Cells[unit.Slot].IsError);
        Assert.Contains("PowerPerUnit", model.Rows[5].ToolTip);

        model.SelectedProfile = model.Profiles.Single(profile => profile.Id == "electrical");
        Assert.Equal("electrical", model.Profile!.Id);
        Assert.Equal(new[] { "ЩР (2)" }, Captions(model));
        Assert.Equal(parses, cache.ParseCount);             // XDATA is not parsed again
        Assert.Equal(1, source.Reads);                      // the drawing is not read again
        Assert.Equal("electrical", store.LoadSettings(out _).ActiveProfile); // the choice is remembered

        var evaluations = CountEvaluations(model);
        model.SelectedProfile = model.Profiles.Single(profile => profile.Id == "by-phase");
        model.SelectedProfile = model.Profiles.Single(profile => profile.Id == "electrical");
        Assert.Equal(evaluations, CountEvaluations(model)); // values computed before are reused

        // A profile broken on disk is not applied: the current one stays.
        File.WriteAllText(store.ProfilePath("by-phase"), "{ broken");
        model.SelectedProfile = model.Profiles.Single(profile => profile.Id == "by-phase");
        Assert.Equal("electrical", model.Profile!.Id);
        Assert.Contains("не применён", model.Message);
        Assert.Equal("electrical", model.SelectedProfile!.Id);
    }

    [Fact]
    public void ColumnChangesMarkTheProfileChangedAndAreSavedWithoutRebuildingTheTree()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(folder.Path);
        var source = new FakeSource(Cache(), Device("A0", Pair("NMO_BaseName", "A")));
        var model = new NavigatorViewModel(store) { Source = source };
        model.IsShown = true;
        var tree = model.Tree;
        var changes = 0;
        model.ColumnsChanged += (_, _) => changes++;

        var name = model.Columns[0];
        name.Width = 250;
        Assert.True(model.IsDirty);
        Assert.True(model.SaveCommand.CanExecute(null));
        Assert.Same(tree, model.Tree);                       // the width does not rebuild the data
        Assert.Equal(0, changes);

        model.MoveVisibleColumn(0, 2);
        Assert.Equal(new[] { "base", "suffix", "name", "link", "mounting" }, model.Profile!.Columns.Select(column => column.Id));
        Assert.Same(tree, model.Tree);
        Assert.Equal(0, name.Slot);                          // cells keep their slot

        model.Columns.Single(column => column.Id == "link").Visible = true;
        Assert.Equal(1, changes);
        Assert.Equal(4, model.VisibleColumns.Count());
        Assert.Same(tree, model.Tree);

        model.TreeWidth = 333;
        Assert.True(model.Save());
        Assert.False(model.IsDirty);
        var saved = store.LoadProfile(NavigatorProfile.DefaultId)!.Profile!;
        Assert.Equal(new[] { "base", "suffix", "name", "link", "mounting" }, saved.Columns.Select(column => column.Id));
        Assert.Equal(250, saved.Columns.Single(column => column.Id == "name").Width);
        Assert.True(saved.Columns.Single(column => column.Id == "link").Visible);
        Assert.Equal(333, store.LoadSettings(out _).TreeWidth);
    }

    [Fact]
    public void SortingByAColumnKeepsTheGroupsAndIsRemembered()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(folder.Path);
        var profile = new NavigatorProfile("p", "P", "+NMO_BaseName|+@@[NMO_Name]", new[] { new NavigatorColumn("power", "Мощность", "Power") });
        Assert.True(store.SaveProfile(profile, out _));
        var source = new FakeSource(Cache(),
            Device("B0", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "1"), Pair("Power", 10.0)),
            Device("B1", Pair("NMO_BaseName", "A"), Pair("NMO_Suffix", "2"), Pair("Power", 9.0)),
            Device("B2", Pair("NMO_BaseName", "B"), Pair("NMO_Suffix", "3"), Pair("Power", 1.0)));
        var model = new NavigatorViewModel(store) { Source = source };
        model.IsShown = true;
        model.ExpandAllCommand.Execute(null);
        Assert.Equal(new[] { "A (2)", "A1", "A2", "B (1)", "B3" }, Captions(model));

        var power = model.Columns[0];
        model.SortCommand.Execute(power);
        Assert.Equal(" ▲", power.SortGlyph);
        Assert.Equal(new[] { "A (2)", "A2", "A1", "B (1)", "B3" }, Captions(model)); // 9 < 10, natural
        model.SortCommand.Execute(power);
        Assert.Equal(" ▼", power.SortGlyph);
        Assert.Equal("Мощность ▼", power.Header);
        Assert.Equal(new[] { "A (2)", "A1", "A2", "B (1)", "B3" }, Captions(model));
        Assert.Equal(("power", true), (store.LoadSettings(out _).SortColumn, store.LoadSettings(out _).SortDescending));

        model.SortCommand.Execute(null);
        Assert.Equal("", power.SortGlyph);
        Assert.Equal(" ▲", model.TreeSortGlyph);
        model.SortCommand.Execute(null);
        Assert.Equal(new[] { "B (1)", "B3", "A (2)", "A2", "A1" }, Captions(model));

        var reopened = new NavigatorViewModel(store) { Source = source };
        reopened.IsShown = true;
        Assert.Null(reopened.SortColumn);
        Assert.True(reopened.SortDescending);
    }

    [Fact]
    public void CreatesRenamesAndDeletesProfiles()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(folder.Path);
        var source = new FakeSource(Cache(), Device("C0", Pair("NMO_BaseName", "A"), Pair("Phase", "B")));
        var questions = new List<string>();
        var model = new NavigatorViewModel(store) { Source = source, Confirm = question => { questions.Add(question); return questions.Count > 1; } };
        model.IsShown = true;
        Assert.Equal(new[] { NavigatorProfile.DefaultId }, model.Profiles.Select(profile => profile.Id));
        Assert.False(model.NewProfile()); // no window: nothing is created

        model.EditProfileWindow = editor =>
        {
            Assert.True(editor.IsNew);
            Assert.Equal("profile1", editor.Id);
            editor.Name = "Фазы";
            editor.Expression = "+Phase|";
            Assert.False(editor.IsValid);
            Assert.Contains(editor.Errors, error => error.Contains("уровень 2 пустой"));
            editor.Expression = "+Phase|-x";
            Assert.True(editor.IsValid);
            Assert.Contains("2. x — отключено: имя объекта — тип", editor.ExpressionInfo);
            editor.AddColumn();
            editor.SelectedColumn!.Value = "not an id";
            Assert.False(editor.IsValid);
            Assert.NotNull(editor.SelectedColumn.SourceError);
            editor.SelectedColumn.Value = "Phase";
            editor.MoveUpCommand.Execute(null);
            Assert.Same(editor.SelectedColumn, editor.Columns[editor.Columns.Count - 2]);
            return true;
        };
        Assert.True(model.NewProfile());
        Assert.Equal("profile1", model.Profile!.Id);
        Assert.Equal(new[] { "Фаза B (1)" }, Captions(model));
        Assert.True(File.Exists(store.ProfilePath("profile1")));
        Assert.Equal(6, store.LoadProfile("profile1")!.Profile!.Columns.Count);

        Assert.True(model.RenameProfile("Фазы 2"));
        Assert.Equal("Фазы 2", store.LoadProfile("profile1")!.Profile!.Name);
        Assert.Equal("Фазы 2", model.SelectedProfile!.Name);

        Assert.False(model.DeleteProfile()); // the first question is answered «no»
        Assert.True(File.Exists(store.ProfilePath("profile1")));
        Assert.True(model.DeleteProfile());
        Assert.Contains("Фазы 2", questions[1]);
        Assert.False(File.Exists(store.ProfilePath("profile1")));
        Assert.Equal(NavigatorProfile.DefaultId, model.Profile!.Id);
        Assert.Equal(new[] { NavigatorProfile.DefaultId }, model.Profiles.Select(profile => profile.Id));
    }

    [Fact]
    public void ExportsTheCurrentLayoutAndImportsARenamedProfileIntoAnotherStore()
    {
        using var folder = new TempFolder();
        var sourceStore = new NavigatorProfileStore(Path.Combine(folder.Path, "source"));
        var original = new NavigatorProfile("phases", "По фазам", "+Phase|-x", new[]
        {
            new NavigatorColumn("phase", "Фаза", "Phase", 100),
            new NavigatorColumn("power", "Мощность", "Power", 80)
        });
        original.Extra.Add("metadata", new JsonString("переносится"));
        original.TreeExtra.Add("future", JsonBoolean.From(true));
        original.Columns[1].Extra.Add("unit", new JsonString("кВт"));
        Assert.True(sourceStore.SaveProfile(original, out var error), error);
        var exporter = new NavigatorViewModel(sourceStore);
        exporter.EnsureLoaded();
        exporter.MoveVisibleColumn(1, 0);
        exporter.Columns[1].Width = 157;
        exporter.Columns[1].Visible = false;
        exporter.TreeWidth = 399;

        var backupPath = Path.Combine(folder.Path, "renamed backup.json");
        Assert.True(exporter.ExportProfile(backupPath), exporter.Message);
        Assert.True(exporter.IsDirty);
        Assert.Equal(original.ToJson(), sourceStore.LoadProfile("phases")!.Profile!.ToJson());

        var targetStore = new NavigatorProfileStore(Path.Combine(folder.Path, "target"));
        var source = new FakeSource(Cache(), Device("C0", Pair("Phase", "B")));
        var importer = new NavigatorViewModel(targetStore) { Source = source, IsShown = true };
        var reads = source.Reads;
        var targetTreeWidth = importer.TreeWidth;
        Assert.True(importer.ImportProfile(backupPath), importer.Message);
        Assert.Equal(reads, source.Reads);
        Assert.Equal(new[] { "Фаза B (1)" }, Captions(importer));
        Assert.Equal("phases", importer.SelectedProfile!.Id);
        Assert.Equal("По фазам", importer.Profile!.Name);
        Assert.Equal(targetTreeWidth, importer.TreeWidth);
        Assert.Equal(new[] { "power", "phase" }, importer.Columns.Select(column => column.Id));
        Assert.Equal((157d, false), (importer.Columns[1].Width, importer.Columns[1].Visible));
        Assert.Equal("переносится", Assert.IsType<JsonString>(importer.Profile.Extra["metadata"]).Value);
        Assert.True(Assert.IsType<JsonBoolean>(importer.Profile.TreeExtra["future"]).Value);
        Assert.Equal("кВт", Assert.IsType<JsonString>(importer.Profile.Columns[0].Extra["unit"]).Value);
        Assert.Equal(importer.Profile.ToJson(), File.ReadAllText(backupPath));
        Assert.Equal(importer.Profile.ToJson(), targetStore.LoadProfile("phases")!.Profile!.ToJson());
        var reopened = new NavigatorViewModel(targetStore);
        reopened.EnsureLoaded();
        Assert.Equal(importer.Profile.ToJson(), reopened.Profile!.ToJson());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":1,\"parameters\":[],\"enums\":[],\"templates\":[]}")]
    [InlineData("{\"version\":2,\"id\":\"backup\",\"name\":\"Backup\",\"tree\":{\"expression\":\"+Phase\"}}")]
    [InlineData("{\"version\":1,\"id\":\"../backup\",\"name\":\"Backup\",\"tree\":{\"expression\":\"+Phase|\"}}")]
    public void InvalidProfileImportKeepsTheActiveProfileAndItsChanges(string json)
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(Path.Combine(folder.Path, "local"));
        var importPath = Path.Combine(folder.Path, "invalid.json");
        File.WriteAllText(importPath, json);
        var model = new NavigatorViewModel(store) { AskSave = _ => throw new InvalidOperationException() };
        model.EnsureLoaded();
        model.Columns[0].Width = 222;
        var original = model.Profile;

        Assert.False(model.ImportProfile(importPath));
        Assert.Contains(importPath, model.Message);
        Assert.Single(model.Profiles);
        Assert.Same(original, model.Profile);
        Assert.True(model.IsDirty);
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public void ImportReplacesAMatchingIdOnlyAfterConfirmation()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(Path.Combine(folder.Path, "local"));
        var original = NavigatorProfile.CreateDefault();
        Assert.True(store.SaveProfile(original, out var error), error);
        var model = new NavigatorViewModel(store);
        model.EnsureLoaded();
        model.Columns[0].Width = 222;
        var backup = new NavigatorProfile("ELECTRICAL", "Копия профиля", "+Phase");
        var importPath = Path.Combine(folder.Path, "backup.json");
        File.WriteAllText(importPath, backup.ToJson());

        var confirmed = false;
        model.Confirm = question =>
        {
            Assert.Contains("Несохранённые изменения", question);
            return confirmed;
        };
        Assert.False(model.ImportProfile(importPath));
        Assert.True(model.IsDirty);
        Assert.Equal(222, model.Columns[0].Width);
        Assert.Equal(original.ToJson(), store.LoadProfile(original.Id)!.Profile!.ToJson());

        confirmed = true;
        Assert.True(model.ImportProfile(importPath), model.Message);
        Assert.Single(model.Profiles);
        Assert.False(model.IsDirty);
        Assert.Equal(original.Id, model.Profile!.Id);
        Assert.Equal(backup.Name, store.LoadProfile(original.Id)!.Profile!.Name);
        Assert.Equal(backup.ToJson(), File.ReadAllText(importPath));
    }

    [Fact]
    public void ImportKeepsUnsavedChangesWhenCancelledOrWhenTheWriteFails()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(Path.Combine(folder.Path, "local"));
        var importPath = Path.Combine(folder.Path, "backup.json");
        File.WriteAllText(importPath, new NavigatorProfile("backup", "Копия", "+Phase").ToJson());
        var model = new NavigatorViewModel(store) { AskSave = _ => null };
        model.EnsureLoaded();
        model.Columns[0].Width = 222;

        Assert.False(model.ImportProfile(importPath));
        Assert.False(File.Exists(store.ProfilePath("backup")));

        model.AskSave = _ =>
        {
            Directory.CreateDirectory(store.ProfilesDirectory);
            File.WriteAllText(store.ProfilePath("backup"), "new file from another process");
            return false;
        };
        Assert.False(model.ImportProfile(importPath));
        Assert.Contains("не импортирован", model.Message);
        Assert.Equal("new file from another process", File.ReadAllText(store.ProfilePath("backup")));
        Assert.Empty(Directory.GetFiles(store.ProfilesDirectory, "*.tmp"));
        Assert.True(model.IsDirty);
        Assert.Equal(222, model.Columns[0].Width);
        Assert.Equal(NavigatorProfile.DefaultId, model.Profile!.Id);
    }

    [Fact]
    public void SwitchingAChangedProfileAsksToSaveIt()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(folder.Path);
        Copy(folder, "electrical.json");
        Copy(folder, "by-phase.json");
        var answer = (bool?)null;
        var model = new NavigatorViewModel(store) { Source = new FakeSource(Cache()), AskSave = _ => answer };
        model.IsShown = true;
        var electrical = model.Profiles.Single(profile => profile.Id == "electrical");
        var byPhase = model.Profiles.Single(profile => profile.Id == "by-phase");

        model.Columns[0].Width = 222;
        model.SelectedProfile = electrical; // cancel
        Assert.Equal("by-phase", model.Profile!.Id);
        Assert.True(model.IsDirty);

        answer = false; // do not save
        model.SelectedProfile = electrical;
        Assert.Equal("electrical", model.Profile!.Id);
        Assert.False(model.IsDirty);
        Assert.Equal(90, store.LoadProfile("by-phase")!.Profile!.Columns[0].Width);

        answer = true; // save
        model.Columns[0].Width = 199;
        model.SelectedProfile = model.Profiles.Single(profile => profile.Id == "by-phase");
        Assert.Equal("by-phase", model.Profile!.Id);
        Assert.Equal(199, store.LoadProfile("electrical")!.Profile!.Columns[0].Width);
        Assert.NotSame(byPhase, model.Profiles.Single(profile => profile.Id == "by-phase")); // read again from the file
    }

    [Fact]
    public void BrokenFilesAreReportedAndTheDefaultProfileIsUsed()
    {
        using var folder = new TempFolder();
        var store = new NavigatorProfileStore(folder.Path);
        Directory.CreateDirectory(store.ProfilesDirectory);
        File.WriteAllText(store.ProfilePath("broken"), "{");
        File.WriteAllText(store.SettingsPath, "{");
        var model = new NavigatorViewModel(store) { Source = new FakeSource(Cache()) };
        model.IsShown = true;
        Assert.Equal(NavigatorProfile.DefaultId, model.Profile!.Id);
        Assert.Contains(model.Diagnostics, line => line.Contains("broken.json"));
        Assert.Contains(model.Diagnostics, line => line.Contains("navigator.json"));
        Assert.Equal("{", File.ReadAllText(store.ProfilePath("broken")));
    }

    [Fact]
    public void AnotherDocumentDropsEverythingOfThePreviousOne()
    {
        var cache = Cache();
        var source = new FakeSource(cache, Device("D0", Pair("NMO_BaseName", "A")));
        var model = Shown(source);
        model.Rows[0].IsExpanded = true;
        source.Document = "other.dwg";
        source.Set(Device("D0", Pair("NMO_BaseName", "A")));
        source.RaiseChanged();
        Assert.Equal(new[] { "A (1)" }, Captions(model)); // the expanded state of the other drawing is not used

        source.Document = null;
        source.Set();
        source.RaiseChanged();
        Assert.Empty(model.Rows);
        Assert.Equal("Нет активного чертежа", model.Status);

        var failing = new FakeSource(cache) { Fail = true };
        model.Source = failing;
        Assert.Empty(model.Rows);
        Assert.Contains(model.Diagnostics, line => line.Contains("Чертёж не прочитан"));
        model.Shutdown();
        Assert.Null(model.Source);
    }

    // ---- helpers ----

    [Fact]
    public void TheExampleProfilesGroupTheExampleObjects()
    {
        // The drawing of the WPF smoke test: duplicates, an object without a base name and a division by zero.
        using var folder = new TempFolder();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "examples", "navigator", "navigator.json"), Path.Combine(folder.Path, "navigator.json"));
        Copy(folder, "electrical.json");
        Copy(folder, "by-phase.json");
        var source = new FakeSource(Cache(),
            Device("N1", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "1"), Pair("Power", 4.5), Pair("Phase", "A"), Pair("DB_link", "db-01")),
            Device("N2", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "2"), Pair("Power", 2.0), Pair("Phase", "B")),
            Device("N3", Pair("NMO_BaseName", "ЩР"), Pair("NMO_Suffix", "2"), Pair("Power", 1.0), Pair("Phase", "C")),
            Device("N4", Pair("NMO_BaseName", "ВРУ"), Pair("NMO_Suffix", "1"), Pair("Power", 12.5), Pair("Phase", "A")),
            Device("N5", Pair("Power", 0.5), Pair("Count", 0)),
            Device("N6", Pair("NMO_BaseName", "Лампа"), Pair("Power", 0.1), Pair("Count", 2), Pair("Phase", "B")));
        source.Types["N1"] = "Блок «Щит»";
        var model = new NavigatorViewModel(new NavigatorProfileStore(folder.Path)) { Source = source };
        model.IsShown = true;

        Assert.Equal("electrical", model.Profile!.Id);
        Assert.Equal(new[] { "ВРУ (1)", "Лампа (1)", "ЩР (3)", "Не задано (1)" }, Captions(model));
        Assert.Equal("Объектов: 6 · групп: 9", model.Status);
        Assert.Equal(" ▲", model.TreeSortGlyph);
        Assert.Equal(new[] { "name", "base", "suffix", "power", "phase" }, model.VisibleColumns.Select(column => column.Id));
        model.ExpandAllCommand.Execute(null);
        var leaf = model.Rows.Single(row => row.Node.Handle == "N1");
        Assert.Equal("Блок «Щит»", leaf.Caption);
        Assert.Equal(2, leaf.Depth);
        Assert.Equal(6, model.Rows.Count(row => row.IsLeaf));
        var power = model.Columns.Single(column => column.Id == "power");
        Assert.NotEqual("", leaf.Cells[power.Slot].Text);
        Assert.True(model.Rows.Where(row => row.IsGroup).All(row => row.Cells[power.Slot].IsEmpty));
        Assert.True(model.Rows.Single(row => row.Caption == "Не задано (1)" && row.Depth == 0).IsEmptyValue);

        model.SelectedProfile = model.Profiles.Single(profile => profile.Id == "by-phase");
        model.ExpandAllCommand.Execute(null);
        Assert.Equal("Фаза A (2)", model.Rows[0].Caption);
        var unit = model.Columns.Single(column => column.Id == "unit");
        var failed = model.Rows.Single(row => row.Node.Handle == "N5").Cells[unit.Slot];
        Assert.Equal(FormulaExpression.ErrorText, failed.Text);
        Assert.True(failed.IsError);
        Assert.Contains("PowerPerUnit", failed.Error);
    }

    private static NavigatorValue Value(NavigatorObject target, string source) => target.Value(NavigatorValueSource.Parse(source));

    private static int CountEvaluations(NavigatorViewModel model) => model.Tree!.Leaves.Sum(node => node.Object!.EvaluationCount);

    private static string[] Captions(NavigatorViewModel model) => model.Rows.Select(row => row.Caption).ToArray();

    private static NavigatorViewModel Shown(FakeSource source, string? expression = null)
    {
        var model = new NavigatorViewModel();
        if (expression != null)
        {
            model.EnsureLoaded();
            var profile = NavigatorProfile.CreateDefault();
            profile.TreeExpression = expression;
            Assert.True(model.ApplyEdited(profile));
        }
        model.Source = source;
        model.IsShown = true;
        return model;
    }

    private static string[] Lines(NavigatorTree tree) =>
        tree.Nodes.Select(node => new string(' ', node.Depth * 2) + node.Caption).ToArray();

    internal static ObjectValuesCache Cache()
    {
        var result = ParameterSchemaJson.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "navigator-schema.json")));
        Assert.NotNull(result.Schema);
        return new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
    }

    private static NavigatorObject[] Objects(params EntityDataSnapshot[] snapshots)
    {
        var cache = Cache();
        return snapshots.Select(snapshot => new NavigatorObject(cache.Get(snapshot), null, CultureInfo.InvariantCulture)).ToArray();
    }

    internal static EntityDataSnapshot Device(string handle, params DataValue[][] pairs)
    {
        var xdata = new List<DataValue> { V(1001, DrawingNames.RegApp), V(1070, (short)2) };
        foreach (var pair in pairs) xdata.AddRange(pair);
        return new EntityDataSnapshot(handle, "Line", xdata.ToArray(), Array.Empty<DataRecord>());
    }

    private static void Copy(TempFolder folder, string name)
    {
        var target = Path.Combine(folder.Path, "profiles", name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "examples", "navigator", "profiles", name), target);
    }

    internal sealed class FakeSource : INavigatorSource
    {
        private readonly ObjectValuesCache cache;

        public FakeSource(ObjectValuesCache cache, params EntityDataSnapshot[] objects) { this.cache = cache; Objects = objects; }

        public event EventHandler? Changed;
        public EntityDataSnapshot[] Objects { get; private set; }
        public string? Document { get; set; } = "drawing.dwg";
        public int Reads { get; private set; }
        public bool Fail { get; set; }
        public List<(string, bool)> Selected { get; } = new List<(string, bool)>();
        public HashSet<string> Erased { get; } = new HashSet<string>();
        public Dictionary<string, string> Types { get; } = new Dictionary<string, string>();

        public void Set(params EntityDataSnapshot[] objects) => Objects = objects;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public NavigatorSnapshot Read(bool full)
        {
            Reads++;
            if (Fail) throw new InvalidOperationException("нет доступа");
            if (Document is null) return NavigatorSnapshot.Empty;
            return new NavigatorSnapshot(Document, Objects.Select(item => new NavigatorItem(cache.Get(item), Types.TryGetValue(item.Handle, out var type) ? type : null)).ToArray(), cache.Schema);
        }

        public bool Select(string handle, bool zoom)
        {
            if (Erased.Contains(handle)) return false;
            Selected.Add((handle, zoom));
            return true;
        }

        public List<string[]> SelectedGroups { get; } = new List<string[]>();

        public int SelectAll(IReadOnlyList<string> handles)
        {
            if (Fail) throw new InvalidOperationException("нет доступа");
            var found = handles.Where(handle => !Erased.Contains(handle)).ToArray();
            if (found.Length > 0) SelectedGroups.Add(found);
            return found.Length;
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "navigator-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
