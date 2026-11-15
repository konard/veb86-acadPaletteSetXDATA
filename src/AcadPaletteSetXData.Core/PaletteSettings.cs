using System;
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
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    private bool showSystemGroup;
    private JsonObject? source;

    /// <summary>Show the parameters of the hidden group <see cref="SystemGroup"/> in the «Свойства» tab; off by default.</summary>
    public bool ShowSystemGroup { get => showSystemGroup; set => SetProperty(ref showSystemGroup, value); }

    /// <summary><c>%APPDATA%\AcadPaletteSetXData\settings.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadPaletteSetXData", FileName);

    /// <summary>The group name is <see cref="SystemGroup"/>, ignoring case and surrounding spaces.</summary>
    public static bool IsSystemGroup(string? group) =>
        group != null && string.Equals(group.Trim(), SystemGroup, StringComparison.OrdinalIgnoreCase);

    /// <summary>A draft for the settings window: «ОК» copies it back with <see cref="CopyFrom"/>, «Отмена» drops it.</summary>
    public PaletteSettings Clone() => new PaletteSettings { showSystemGroup = showSystemGroup, source = source };

    public void CopyFrom(PaletteSettings other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        source = other.source;
        ShowSystemGroup = other.ShowSystemGroup;
    }

    /// <summary>JSON of the settings; unknown properties of the loaded file are kept for newer versions of the plugin.</summary>
    public string ToJson()
    {
        var json = new JsonObject();
        if (source != null)
            foreach (var property in source)
                json.Add(property.Key, property.Value);
        json.Set(ShowSystemGroupKey, JsonBoolean.From(ShowSystemGroup));
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
