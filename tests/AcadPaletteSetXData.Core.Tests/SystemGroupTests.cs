using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #48: parameters of the schema group <c>SYSTEM</c> are hidden in the «Свойства» tab; the checkbox of the
/// general settings window shows them. The settings are kept per user in a JSON file.
/// </summary>
public sealed class SystemGroupTests
{
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "examples", "schema.json");

    [Fact]
    public void SystemGroupIsHiddenUntilTheSettingShowsIt()
    {
        // Device_Class is in the DEVICE template, Tray_Width is stored outside it, Poles is offered by «+ Параметр».
        var drawing = new Drawing(SystemCache("Device_Class", "Tray_Width", "Poles"),
            Device("A", Pair("Power", 1.0), Pair("Device_Class", "служебное"), Pair("Tray_Width", 200)));
        var settings = new PaletteSettings();
        var selection = new SelectionViewModel(new PropertyEditorViewModel());
        var grid = new PropertyGridViewModel(selection, CultureInfo.InvariantCulture, settings: settings);
        Assert.Same(settings, grid.Settings);
        using (new SelectionController(drawing, selection))
        {
            Assert.True(grid.CanEdit);
            Assert.Equal(new[] { "Общие", "Обозначение", "Электрика", "Материалы", "Спецификация", "Соединения" }, grid.Roots.Select(row => row.Caption));
            Assert.DoesNotContain(All(grid.Roots), row => row.Id == "Device_Class" || row.Id == "Tray_Width");
            Assert.DoesNotContain(All(grid.Roots), row => string.Equals(row.Caption, "SYSTEM", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(grid.Rows, row => row.Id == "Device_Class");
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "Poles");
            Assert.Contains(grid.AvailableParameters, option => option.Key == "Motor_Speed");
            Assert.Equal("Device_Type", Row(grid, "Device_Type").Id); // Other parameters of the group stay.

            settings.ShowSystemGroup = true; // «Показывать скрытую группу SYSTEM»: the grid is rebuilt at once.
            Assert.Equal(new[] { "Общие", "system", "Обозначение", "Электрика", "Материалы", "Спецификация", "Соединения", PropertyGridViewModel.GroupExtra },
                grid.Roots.Select(row => row.Caption));
            Assert.Equal("служебное", Row(grid, "Device_Class").Value);
            Assert.Equal("200", Row(grid, "Tray_Width").Value);
            Assert.Contains(grid.AvailableParameters, option => option.Key == "Poles");

            settings.ShowSystemGroup = false;
            Assert.DoesNotContain(All(grid.Roots), row => row.Id == "Device_Class" || row.Id == "Tray_Width");
            Assert.DoesNotContain(grid.AvailableParameters, option => option.Key == "Poles");
        }
    }

    [Fact]
    public void PaletteViewModelSharesItsSettingsWithTheGrid()
    {
        var palette = new PaletteViewModel();
        Assert.Same(palette.Settings, palette.Grid.Settings);
        Assert.False(palette.Settings.ShowSystemGroup);
    }

    [Theory]
    [InlineData("SYSTEM", true)]
    [InlineData("system", true)]
    [InlineData(" System ", true)]
    [InlineData("SYSTEMS", false)]
    [InlineData("Общие", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SystemGroupNameIgnoresCaseAndSpaces(string? group, bool expected) =>
        Assert.Equal(expected, PaletteSettings.IsSystemGroup(group));

    [Fact]
    public void SettingsRoundTripThroughJsonAndKeepUnknownProperties()
    {
        var settings = PaletteSettings.FromJson("{\"future\": [1, 2], \"showSystemGroup\": false}");
        Assert.False(settings.ShowSystemGroup);
        settings.ShowSystemGroup = true;
        var json = settings.ToJson();
        Assert.Contains("\"future\"", json);
        var again = PaletteSettings.FromJson(json);
        Assert.True(again.ShowSystemGroup);
        Assert.Contains("\"future\"", again.ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[true]")]
    [InlineData("{\"showSystemGroup\": \"yes\"}")]
    [InlineData("{\"showSystemGroup\": true, \"showSystemGroup\": true}")]
    public void BrokenSettingsGiveTheDefaults(string? text)
    {
        var settings = PaletteSettings.FromJson(text);
        Assert.False(settings.ShowSystemGroup);
        Assert.True(JsonValue.DeepEquals(JsonValue.Parse(new PaletteSettings().ToJson()), JsonValue.Parse(settings.ToJson())), settings.ToJson());
    }

    [Fact]
    public void CloneIsADraftUntilCopiedBack()
    {
        var settings = new PaletteSettings();
        var changes = 0;
        settings.PropertyChanged += (_, _) => changes++;
        var draft = settings.Clone();
        draft.ShowSystemGroup = true;
        Assert.False(settings.ShowSystemGroup);
        Assert.Equal(0, changes);
        settings.CopyFrom(draft);
        Assert.True(settings.ShowSystemGroup);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SettingsFileIsSavedAndLoaded()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AcadPaletteSetXData.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "nested", PaletteSettings.FileName);
            Assert.False(PaletteSettings.Load(path).ShowSystemGroup); // A missing file gives the defaults.
            Assert.True(new PaletteSettings { ShowSystemGroup = true }.Save(path));
            Assert.True(PaletteSettings.Load(path).ShowSystemGroup);
            Assert.True(new PaletteSettings().Save(path)); // Overwrites the file.
            Assert.False(PaletteSettings.Load(path).ShowSystemGroup);
            Assert.False(File.Exists(path + ".tmp"));
            File.WriteAllText(path, "{ broken");
            Assert.False(PaletteSettings.Load(path).ShowSystemGroup);
            Assert.False(new PaletteSettings().Save(folder)); // A folder is not a file: false, no exception.
            Assert.False(File.Exists(folder + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DefaultPathIsInTheRoamingProfile()
    {
        Assert.EndsWith(Path.Combine("AcadPaletteSetXData", PaletteSettings.FileName), PaletteSettings.DefaultPath);
    }

    /// <summary>The example schema with <paramref name="ids"/> moved to the group <c>system</c> (any case is SYSTEM).</summary>
    private static ObjectValuesCache SystemCache(params string[] ids)
    {
        var json = (JsonObject)JsonValue.Parse(File.ReadAllText(ExamplePath));
        foreach (var parameter in ((JsonArray)json["parameters"]!).Cast<JsonObject>())
            if (ids.Contains(((JsonString)parameter["id"]!).Value))
                parameter.Set("group", new JsonString("system"));
        var result = ParameterSchemaJson.Load(json.ToJson());
        Assert.NotNull(result.Schema);
        Assert.False(result.HasErrors, string.Join("\n", result.Issues.Select(issue => issue.Message)));
        return new ObjectValuesCache(new SchemaCache(() => new SchemaNodReadResult(result.Schema, null, result.Issues)));
    }
}
