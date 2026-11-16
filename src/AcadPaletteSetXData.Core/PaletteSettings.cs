using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// General settings of the palette (issue #48), edited in the «Настройки» window of the ⚙ menu and kept per user in
/// <see cref="DefaultPath"/>. A missing or broken file gives the defaults: the palette must open in any case.
/// </summary>
public sealed class PaletteSettings : ObservableObject
{
    /// <summary>Parameters of this schema group are service data: the «Свойства» tab hides them unless <see cref="ShowSystemGroup"/>.</summary>
    public const string SystemGroup = "SYSTEM";
    public const string FileName = "settings.json";
    private const string ShowSystemGroupKey = "showSystemGroup";
    private const string MaxSelectionCountKey = "maxSelectionCount";
    /// <summary>The default of <see cref="MaxSelectionCount"/> (issue #59).</summary>
    public const int DefaultMaxSelectionCount = 15000;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    private bool showSystemGroup;
    private int maxSelectionCount = DefaultMaxSelectionCount;
    private JsonObject? source;

    /// <summary>Show the parameters of the hidden group <see cref="SystemGroup"/> in the «Свойства» tab; off by default.</summary>
    public bool ShowSystemGroup { get => showSystemGroup; set => SetProperty(ref showSystemGroup, value); }

    /// <summary>
    /// Issue #59: a selection of more objects is not read by the palette (the objects are only counted), so a large
    /// selection is as fast as without the plugin. 0 — no limit; 15000 by default.
    /// </summary>
    public int MaxSelectionCount
    {
        get => maxSelectionCount;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Предел выделения не может быть отрицательным.");
            SetProperty(ref maxSelectionCount, value);
        }
    }

    /// <summary>
    /// The text of the selection limit in the settings window: digits, spaces between groups allowed («15 000»); null when
    /// it is not an integer from 0 to <see cref="int.MaxValue"/>.
    /// </summary>
    public static int? ParseMaxSelectionCount(string? text)
    {
        var digits = (text ?? "").Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "");
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>A selection of <paramref name="count"/> objects is over <see cref="MaxSelectionCount"/> and is not read.</summary>
    public bool IsOverSelectionLimit(int count) => maxSelectionCount > 0 && count > maxSelectionCount;

    /// <summary><c>%APPDATA%\AcadPaletteSetXData\settings.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadPaletteSetXData", FileName);

    /// <summary>The group name is <see cref="SystemGroup"/>, ignoring case and surrounding spaces.</summary>
    public static bool IsSystemGroup(string? group) =>
        group != null && string.Equals(group.Trim(), SystemGroup, StringComparison.OrdinalIgnoreCase);

    /// <summary>A draft for the settings window: «ОК» copies it back with <see cref="CopyFrom"/>, «Отмена» drops it.</summary>
    public PaletteSettings Clone() =>
        new PaletteSettings { showSystemGroup = showSystemGroup, maxSelectionCount = maxSelectionCount, source = source };

    public void CopyFrom(PaletteSettings other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        source = other.source;
        ShowSystemGroup = other.ShowSystemGroup;
        MaxSelectionCount = other.MaxSelectionCount;
    }

    /// <summary>JSON of the settings; unknown properties of the loaded file are kept for newer versions of the plugin.</summary>
    public string ToJson()
    {
        var json = new JsonObject();
        if (source != null)
            foreach (var property in source)
                json.Add(property.Key, property.Value);
        json.Set(ShowSystemGroupKey, JsonBoolean.From(ShowSystemGroup));
        json.Set(MaxSelectionCountKey, new JsonNumber((long)MaxSelectionCount));
        return json.ToJson();
    }

    /// <summary>Settings of <paramref name="text"/>; invalid JSON, another root or wrong types give the defaults.</summary>
    public static PaletteSettings FromJson(string? text)
    {
        var settings = new PaletteSettings();
        if (string.IsNullOrWhiteSpace(text)) return settings;
        try
        {
            if (JsonValue.Parse(text!) is not JsonObject json) return settings;
            settings.source = json;
            if (json[ShowSystemGroupKey] is JsonBoolean flag) settings.showSystemGroup = flag.Value;
            // A fraction, a negative number or another type keeps the default limit.
            if (json[MaxSelectionCountKey] is JsonNumber limit && limit.TryGetInt32(out var count) && count >= 0)
                settings.maxSelectionCount = count;
        }
        catch (Exception error) when (error is FormatException || error is ArgumentException)
        {
            settings = new PaletteSettings();
        }
        return settings;
    }

    /// <summary>Reads <paramref name="path"/>; a missing, unreadable or broken file gives the defaults.</summary>
    public static PaletteSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path, Utf8)) : new PaletteSettings();
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
        {
            return new PaletteSettings();
        }
    }

    /// <summary>Writes <paramref name="path"/> through a temporary file; false when the folder is not writable.</summary>
    public bool Save(string path)
    {
        var temporary = path + ".tmp";
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(temporary, ToJson(), Utf8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
            return true;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException) { }
            return false;
        }
    }
}
