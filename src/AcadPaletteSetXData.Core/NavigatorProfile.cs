using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// A column of the navigator (issue #66): <c>{"id","caption","value","width","visible"}</c>. The value is a
/// <see cref="NavigatorValueSource"/> — a parameter id or a <c>@@[Id]</c> format.
/// </summary>
public sealed class NavigatorColumn
{
    public const double DefaultWidth = 120;
    public const double MinWidth = 16;
    public const double MaxWidth = 2000;

    public NavigatorColumn(string id, string caption, string value, double width = DefaultWidth, bool visible = true)
    {
        Id = id ?? ""; Caption = caption ?? ""; Value = value ?? ""; Width = width; Visible = visible;
    }

    public string Id { get; set; }
    public string Caption { get; set; }
    public string Value { get; set; }
    public double Width { get; set; }
    public bool Visible { get; set; }
    /// <summary>Unknown fields of the file, kept for newer versions.</summary>
    public JsonObject Extra { get; private set; } = new JsonObject();

    public NavigatorColumn Clone() => new NavigatorColumn(Id, Caption, Value, Width, Visible) { Extra = Extra };

    public static double ClampWidth(double width) =>
        double.IsNaN(width) || double.IsInfinity(width) ? DefaultWidth : Math.Max(MinWidth, Math.Min(MaxWidth, width));
}

/// <summary>
/// A navigator profile (issue #66), one file of <c>navigator\profiles</c>:
/// <c>{"version":1,"id":"electrical","name":"Электрооборудование","tree":{"expression":"…"},"columns":[…]}</c>.
/// The tree depends on <see cref="TreeExpression"/> only; columns only add values to leaves.
/// </summary>
public sealed class NavigatorProfile
{
    public const int CurrentVersion = 1;
    /// <summary>The control expression of the issue (ZCAD <c>TreeBuildMap</c> of electrical devices).</summary>
    public const string ControlExpression = "-DB_link|-DESC_MountingParts|+NMO_BaseName|+NMO_Suffix|-@@[NMO_Name]";
    public const string DefaultId = "electrical";

    public NavigatorProfile(string id, string name, string treeExpression, IEnumerable<NavigatorColumn>? columns = null)
    {
        Id = id ?? ""; Name = name ?? ""; TreeExpression = treeExpression ?? "";
        if (columns != null) Columns.AddRange(columns);
    }

    public int Version { get; set; } = CurrentVersion;
    /// <summary>The id is also the file name: <c>profiles\&lt;id&gt;.json</c>.</summary>
    public string Id { get; set; }
    public string Name { get; set; }
    public string TreeExpression { get; set; }
    public List<NavigatorColumn> Columns { get; } = new List<NavigatorColumn>();
    public JsonObject Extra { get; private set; } = new JsonObject();
    public JsonObject TreeExtra { get; private set; } = new JsonObject();

    /// <summary>The profile used when there are no profile files: the control expression and the columns of the issue.</summary>
    public static NavigatorProfile CreateDefault() => new NavigatorProfile(DefaultId, "Электрооборудование", ControlExpression, new[]
    {
        new NavigatorColumn("name", "Наименование", "@@[NMO_Name]", 160),
        new NavigatorColumn("base", "Базовое имя", "NMO_BaseName", 110),
        new NavigatorColumn("suffix", "Суффикс", "NMO_Suffix", 80),
        new NavigatorColumn("link", "Связь", "DB_link", 120, false),
        new NavigatorColumn("mounting", "Монтажные части", "DESC_MountingParts", 140, false)
    });

    public NavigatorProfile Clone()
    {
        var copy = new NavigatorProfile(Id, Name, TreeExpression, Columns.Select(column => column.Clone())) { Version = Version };
        copy.Extra = Extra; copy.TreeExtra = TreeExtra;
        return copy;
    }

    public NavigatorTreeExpression ParseTree() => NavigatorTreeExpression.Parse(TreeExpression);

    /// <summary>An id that is a safe file name: Latin letters, digits, <c>_</c> and <c>-</c>.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id!.Length <= 64 && id.All(symbol => symbol < 128 && (char.IsLetterOrDigit(symbol) || symbol == '_' || symbol == '-'));

    /// <summary>
    /// Errors make the profile unusable (it is not applied, the previous one stays); warnings are shown only:
    /// the references the schema of the drawing does not know (their objects go to the empty group).
    /// </summary>
    public NavigatorValidation Validate(SchemaSnapshot? schema = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (Version != CurrentVersion) errors.Add("версия профиля " + Version.ToString(CultureInfo.InvariantCulture) + " не поддерживается (ожидается 1)");
        if (!IsValidId(Id)) errors.Add("id «" + Id + "»: допустимы латинские буквы, цифры, _ и -");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("имя профиля не задано");
        var tree = ParseTree();
        errors.AddRange(tree.Errors.Select(error => "дерево: " + error));
        var references = new List<string>(tree.References);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Columns.Count; i++)
        {
            var column = Columns[i];
            var name = "колонка " + (i + 1).ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(column.Id) ? "" : " «" + column.Id + "»");
            if (string.IsNullOrWhiteSpace(column.Id)) errors.Add(name + ": id не задан");
            else if (!ids.Add(column.Id)) errors.Add(name + ": id повторяется");
            if (string.IsNullOrWhiteSpace(column.Caption)) errors.Add(name + ": заголовок не задан");
            if (double.IsNaN(column.Width) || column.Width < NavigatorColumn.MinWidth || column.Width > NavigatorColumn.MaxWidth)
                errors.Add(name + ": ширина должна быть от " + NavigatorColumn.MinWidth.ToString(CultureInfo.InvariantCulture) + " до " +
                    NavigatorColumn.MaxWidth.ToString(CultureInfo.InvariantCulture));
            var source = NavigatorValueSource.Parse(column.Value);
            if (source.Error != null) errors.Add(name + ": " + source.Error);
            else references.AddRange(source.References);
        }
        foreach (var id in Unknown(references, schema)) warnings.Add("параметр " + id + " не найден в схеме чертежа");
        return new NavigatorValidation(errors, warnings);
    }

    /// <summary>The ids of <paramref name="references"/> that are neither in the schema nor entity properties.</summary>
    internal static IReadOnlyList<string> Unknown(IEnumerable<string> references, SchemaSnapshot? schema)
    {
        if (schema?.Schema is null) return Array.Empty<string>();
        return references.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !schema.Parameters.TryGetValue(id, out _) && !EntityProperties.IsGeometryId(id)).ToArray();
    }

    // ---- JSON ----

    public string ToJson()
    {
        var json = new JsonObject();
        json.Add("version", new JsonNumber(Version));
        json.Add("id", new JsonString(Id));
        json.Add("name", new JsonString(Name));
        var tree = new JsonObject();
        tree.Add("expression", new JsonString(TreeExpression));
        foreach (var extra in TreeExtra) if (!tree.ContainsKey(extra.Key)) tree.Add(extra.Key, extra.Value);
        json.Add("tree", tree);
        json.Add("columns", new JsonArray(Columns.Select(column =>
        {
            var item = new JsonObject();
            item.Add("id", new JsonString(column.Id));
            item.Add("caption", new JsonString(column.Caption));
            item.Add("value", new JsonString(column.Value));
            item.Add("width", new JsonNumber(Math.Round(column.Width, 1)));
            item.Add("visible", JsonBoolean.From(column.Visible));
            foreach (var extra in column.Extra) if (!item.ContainsKey(extra.Key)) item.Add(extra.Key, extra.Value);
            return (JsonValue)item;
        })));
        foreach (var extra in Extra) if (!json.ContainsKey(extra.Key)) json.Add(extra.Key, extra.Value);
        return json.ToJson();
    }

    /// <summary>Reads a profile; a broken JSON or a wrong structure throws <see cref="FormatException"/> with the reason.</summary>
    public static NavigatorProfile Parse(string text)
    {
        JsonValue value;
        try { value = JsonValue.Parse(text ?? ""); }
        catch (JsonException failure) { throw new FormatException("некорректный JSON: " + failure.Message, failure); }
        if (value is not JsonObject json) throw new FormatException("профиль должен быть объектом JSON");
        var version = json["version"] is JsonNumber number && number.TryGetInt32(out var parsed) ? parsed
            : throw new FormatException("нет числа «version»");
        var tree = json["tree"] as JsonObject ?? throw new FormatException("нет объекта «tree»");
        var profile = new NavigatorProfile(String(json, "id", true)!, String(json, "name", true)!, String(tree, "expression", true)!) { Version = version };
        if (json["columns"] is JsonArray columns)
        {
            foreach (var item in columns)
            {
                if (item is not JsonObject column) throw new FormatException("элемент «columns» должен быть объектом");
                var width = column["width"] switch
                {
                    null => NavigatorColumn.DefaultWidth,
                    JsonNumber size => size.ToDouble(),
                    _ => throw new FormatException("«width» должен быть числом")
                };
                var visible = column["visible"] switch
                {
                    null => true,
                    JsonBoolean flag => flag.Value,
                    _ => throw new FormatException("«visible» должен быть true или false")
                };
                var parsedColumn = new NavigatorColumn(String(column, "id", true)!, String(column, "caption", false) ?? String(column, "id", true)!,
                    String(column, "value", true)!, width, visible);
                foreach (var extra in column.Where(pair => !Known(pair.Key, "id", "caption", "value", "width", "visible"))) parsedColumn.Extra.Add(extra.Key, extra.Value);
                profile.Columns.Add(parsedColumn);
            }
        }
        else if (json["columns"] != null) throw new FormatException("«columns» должен быть массивом");
        foreach (var extra in json.Where(pair => !Known(pair.Key, "version", "id", "name", "tree", "columns"))) profile.Extra.Add(extra.Key, extra.Value);
        foreach (var extra in tree.Where(pair => !Known(pair.Key, "expression"))) profile.TreeExtra.Add(extra.Key, extra.Value);
        return profile;
    }

    private static bool Known(string key, params string[] names) => names.Contains(key, StringComparer.Ordinal);

    private static string? String(JsonObject json, string name, bool required)
    {
        var value = json[name];
        if (value is JsonString text) return text.Value;
        if (value is null && !required) return null;
        throw new FormatException(value is null ? "нет строки «" + name + "»" : "«" + name + "» должен быть строкой");
    }

    public override string ToString() => Name;
}

public sealed class NavigatorValidation
{
    public NavigatorValidation(IReadOnlyList<string> errors, IReadOnlyList<string> warnings) { Errors = errors; Warnings = warnings; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsValid => Errors.Count == 0;
    public override string ToString() => string.Join("; ", Errors.Concat(Warnings));
}

/// <summary>
/// General settings of the navigator, <c>navigator\navigator.json</c>: the active profile and the UI state that is
/// not part of a profile. The general <c>settings.json</c> of the palette is not changed.
/// </summary>
public sealed class NavigatorSettings
{
    public const double DefaultTreeWidth = 220;

    public string? ActiveProfile { get; set; }
    /// <summary>The name of the group of objects without a value; «Не задано» by default.</summary>
    public string EmptyText { get; set; } = NavigatorTreeBuilder.DefaultEmptyText;
    /// <summary>The width of the tree column (names of groups and objects).</summary>
    public double TreeWidth { get; set; } = DefaultTreeWidth;
    /// <summary>The id of the column the leaves are sorted by; null — the tree column.</summary>
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }
    public JsonObject Extra { get; private set; } = new JsonObject();

    public NavigatorSettings Clone() => new NavigatorSettings
    {
        ActiveProfile = ActiveProfile, EmptyText = EmptyText, TreeWidth = TreeWidth, SortColumn = SortColumn, SortDescending = SortDescending, Extra = Extra
    };

    public string ToJson()
    {
        var json = new JsonObject();
        json.Add("version", new JsonNumber(1));
        json.Add("activeProfile", ActiveProfile is null ? JsonNull.Instance : new JsonString(ActiveProfile));
        json.Add("emptyText", new JsonString(EmptyText));
        json.Add("treeWidth", new JsonNumber(Math.Round(TreeWidth, 1)));
        json.Add("sortColumn", SortColumn is null ? JsonNull.Instance : new JsonString(SortColumn));
        json.Add("sortDescending", JsonBoolean.From(SortDescending));
        foreach (var extra in Extra) if (!json.ContainsKey(extra.Key)) json.Add(extra.Key, extra.Value);
        return json.ToJson();
    }

    /// <summary>Reads the settings; fields of a wrong type keep their defaults, a broken JSON throws <see cref="FormatException"/>.</summary>
    public static NavigatorSettings Parse(string text)
    {
        JsonValue value;
        try { value = JsonValue.Parse(text ?? ""); }
        catch (JsonException failure) { throw new FormatException("некорректный JSON: " + failure.Message, failure); }
        if (value is not JsonObject json) throw new FormatException("настройки навигатора должны быть объектом JSON");
        var settings = new NavigatorSettings();
        if (json["activeProfile"] is JsonString active) settings.ActiveProfile = active.Value;
        if (json["emptyText"] is JsonString empty && empty.Value.Trim().Length != 0) settings.EmptyText = empty.Value;
        if (json["treeWidth"] is JsonNumber width) settings.TreeWidth = NavigatorColumn.ClampWidth(width.ToDouble());
        if (json["sortColumn"] is JsonString sort) settings.SortColumn = sort.Value;
        if (json["sortDescending"] is JsonBoolean descending) settings.SortDescending = descending.Value;
        foreach (var extra in json.Where(pair => pair.Key is not ("version" or "activeProfile" or "emptyText" or "treeWidth" or "sortColumn" or "sortDescending")))
            settings.Extra.Add(extra.Key, extra.Value);
        return settings;
    }
}

/// <summary>A profile file: the profile, or the reason it could not be read (the file is left as is).</summary>
public sealed class NavigatorProfileFile
{
    public NavigatorProfileFile(string path, NavigatorProfile? profile, string? error) { Path = path; Profile = profile; Error = error; }
    public string Path { get; }
    public NavigatorProfile? Profile { get; }
    public string? Error { get; }
}

/// <summary>
/// The files of the navigator (issue #66): <c>%APPDATA%\AcadPaletteSetXData\navigator\navigator.json</c> and
/// <c>navigator\profiles\*.json</c>. Files are written to a temporary file that then replaces the old one, so a failed
/// write keeps the previous file; a broken file is reported and left untouched; missing files give the defaults.
/// </summary>
public sealed class NavigatorProfileStore
{
    public const string SettingsFileName = "navigator.json";
    public const string ProfilesFolder = "profiles";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public NavigatorProfileStore(string directory) { Directory = directory ?? throw new ArgumentNullException(nameof(directory)); }

    /// <summary><c>%APPDATA%\AcadPaletteSetXData\navigator</c>.</summary>
    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadPaletteSetXData", "navigator");

    public string Directory { get; }
    public string SettingsPath => System.IO.Path.Combine(Directory, SettingsFileName);
    public string ProfilesDirectory => System.IO.Path.Combine(Directory, ProfilesFolder);
    public string ProfilePath(string id) => System.IO.Path.Combine(ProfilesDirectory, id + ".json");

    /// <summary>The settings; a missing file gives the defaults, a broken one the defaults and <paramref name="error"/>.</summary>
    public NavigatorSettings LoadSettings(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(SettingsPath)) return new NavigatorSettings();
            return NavigatorSettings.Parse(File.ReadAllText(SettingsPath, Utf8));
        }
        catch (Exception failure) when (failure is FormatException or IOException or UnauthorizedAccessException)
        {
            error = SettingsPath + ": " + failure.Message;
            return new NavigatorSettings();
        }
    }

    public bool SaveSettings(NavigatorSettings settings, out string? error) =>
        TryWrite(SettingsPath, (settings ?? throw new ArgumentNullException(nameof(settings))).ToJson(), out error);

    /// <summary>All profile files in the order of their names; broken ones carry the error and no profile.</summary>
    public IReadOnlyList<NavigatorProfileFile> LoadProfiles()
    {
        var result = new List<NavigatorProfileFile>();
        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(ProfilesDirectory)) return result;
            files = System.IO.Directory.GetFiles(ProfilesDirectory, "*.json");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            result.Add(new NavigatorProfileFile(ProfilesDirectory, null, failure.Message));
            return result;
        }
        foreach (var file in files.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) result.Add(ReadProfileFile(file, matchFileName: true));
        return result;
    }

    /// <summary>Reads one profile; the file name must match the id. A missing file gives null and no error.</summary>
    public NavigatorProfileFile? LoadProfile(string id)
    {
        if (!NavigatorProfile.IsValidId(id)) return new NavigatorProfileFile(id, null, "некорректный id профиля «" + id + "»");
        var path = ProfilePath(id);
        return File.Exists(path) ? ReadProfileFile(path, matchFileName: true) : null;
    }

    internal static NavigatorProfileFile ReadProfileFile(string path, bool matchFileName = false)
    {
        try
        {
            var profile = NavigatorProfile.Parse(File.ReadAllText(path, Utf8));
            if (matchFileName)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!string.Equals(profile.Id, name, StringComparison.OrdinalIgnoreCase))
                    return new NavigatorProfileFile(path, null, "id «" + profile.Id + "» не совпадает с именем файла «" + name + "»");
            }
            var validation = profile.Validate();
            return validation.IsValid ? new NavigatorProfileFile(path, profile, null) : new NavigatorProfileFile(path, null, string.Join("; ", validation.Errors));
        }
        catch (Exception failure) when (failure is FormatException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new NavigatorProfileFile(path, null, failure.Message);
        }
    }

    /// <summary>Saves a valid profile to <c>profiles\&lt;id&gt;.json</c>.</summary>
    public bool SaveProfile(NavigatorProfile profile, out string? error) => SaveProfile(profile, overwrite: true, out error);

    internal bool SaveProfile(NavigatorProfile profile, bool overwrite, out string? error)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        var validation = profile.Validate();
        if (!validation.IsValid) { error = string.Join("; ", validation.Errors); return false; }
        return TryWrite(ProfilePath(profile.Id), profile.ToJson(), overwrite, out error);
    }

    public bool DeleteProfile(string id, out string? error)
    {
        error = null;
        if (!NavigatorProfile.IsValidId(id)) { error = "некорректный id профиля «" + id + "»"; return false; }
        try
        {
            var path = ProfilePath(id);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to a temporary file next to <paramref name="path"/> and replaces it; on an error the
    /// temporary file is removed and the old file stays as it was.
    /// </summary>
    public static bool TryWrite(string path, string text, out string? error) => TryWrite(path, text, overwrite: true, out error);

    private static bool TryWrite(string path, string text, bool overwrite, out string? error)
    {
        error = null;
        string? temp = null;
        try
        {
            var folder = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);
            temp = System.IO.Path.Combine(folder ?? "", Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, text, Utf8);
            if (overwrite && File.Exists(path))
            {
                try { File.Replace(temp, path, null); }
                catch (PlatformNotSupportedException) { File.Copy(temp, path, true); File.Delete(temp); }
            }
            else File.Move(temp, path);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = path + ": " + failure.Message;
            try { if (temp != null && File.Exists(temp)) File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
