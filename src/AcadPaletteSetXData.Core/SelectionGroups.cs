using System;
using System.Collections.Generic;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>Changes the selection of the drawing (the PICKFIRST set of AutoCAD) for the buttons * and − (issue #41).</summary>
public interface ISelectionChanger
{
    /// <summary>
    /// Selects exactly <paramref name="handles"/> of the current selection; an empty list clears the selection. Objects and
    /// their XDATA are not modified.
    /// </summary>
    void SetSelection(IReadOnlyList<string> handles);
}

/// <summary>The new selection asked by * or −: handles of the objects that stay selected.</summary>
public sealed class SelectionChange : EventArgs
{
    public SelectionChange(IReadOnlyList<string> handles) => Handles = handles ?? throw new ArgumentNullException(nameof(handles));
    public IReadOnlyList<string> Handles { get; }
}

/// <summary>
/// One item of the group list above the panel (issue #41): the objects of the selection with the same display name,
/// or «Все» for the whole selection.
/// </summary>
public sealed class SelectionGroup
{
    public const string AllName = "Все";

    internal SelectionGroup(string name, IReadOnlyList<string> handles, bool isAll = false)
    {
        Name = name; Handles = handles; IsAll = isAll;
    }

    /// <summary>The <c>SYSTEMTYPE</c> value, the AutoCAD name of the object type or «Все».</summary>
    public string Name { get; }
    /// <summary>Handles of the objects of the group in the order of the selection.</summary>
    public IReadOnlyList<string> Handles { get; }
    public int Count => Handles.Count;
    /// <summary>The «Все» item: the whole current selection.</summary>
    public bool IsAll { get; }
    /// <summary><c>Имя (N)</c>, the text of the list item.</summary>
    public string Text => Name + " (" + Count + ")";
    public override string ToString() => Text;
}

/// <summary>
/// Groups the selection by <c>SYSTEMTYPE</c> or by the AutoCAD display name of the object type (issue #41).
/// Only reads the snapshots: XDATA and <c>SYSTEMTYPE</c> are never written.
/// </summary>
public static class SelectionGroups
{
    /// <summary>The XDATA parameter whose non-empty value names the group of an object.</summary>
    public const string SystemTypeKey = "SYSTEMTYPE";

    /// <summary>
    /// The groups in the order of the list: «Все» first when there is more than one group, then the groups by count
    /// (the larger first) and, for the same count, in the order of the first object in the selection.
    /// </summary>
    public static IReadOnlyList<SelectionGroup> Build(IReadOnlyList<EntityDataSnapshot> snapshots)
    {
        if (snapshots is null) throw new ArgumentNullException(nameof(snapshots));
        var order = new List<string>();
        var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            var name = DisplayName(snapshot);
            if (!members.TryGetValue(name, out var handles))
            {
                members.Add(name, handles = new List<string>());
                order.Add(name);
            }
            handles.Add(snapshot.Handle);
        }
        var groups = order.Select((name, index) => (Group: new SelectionGroup(name, members[name].AsReadOnly()), Index: index))
            .OrderByDescending(item => item.Group.Count).ThenBy(item => item.Index).Select(item => item.Group).ToList();
        if (groups.Count > 1)
            groups.Insert(0, new SelectionGroup(SelectionGroup.AllName, snapshots.Select(snapshot => snapshot.Handle).ToArray(), true));
        return groups.AsReadOnly();
    }

    /// <summary>
    /// The name of the group of an object: the non-empty <c>SYSTEMTYPE</c> value, otherwise the name AutoCAD shows for the
    /// object type in the current language (<see cref="EntityDataSnapshot.TypeDisplayName"/>).
    /// </summary>
    public static string DisplayName(EntityDataSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        return SystemType(snapshot.XData) ?? snapshot.TypeDisplayName;
    }

    /// <summary>
    /// The trimmed non-empty value of <c>SYSTEMTYPE</c> in XDATA, null when the parameter is missing or empty. Read in
    /// each RegApp, the plugin RegApp first:
    /// <list type="bullet">
    /// <item>XDATA v2 (the section starts with <c>(1070 . 2)</c>): the pair <c>(1000 . "SYSTEMTYPE") (1000|1040|1070|1071 . value)</c>;</item>
    /// <item>v1 strings: <c>(1000 . "SYSTEMTYPE=value")</c>, spaces around <c>=</c> are allowed;</item>
    /// <item>a RegApp named <c>SYSTEMTYPE</c>: its first string <c>(1000 . "value")</c>.</item>
    /// </list>
    /// The Id is compared without case, as Ids of the schema.
    /// </summary>
    public static string? SystemType(IReadOnlyList<DataValue> xdata)
    {
        if (xdata is null) throw new ArgumentNullException(nameof(xdata));
        var sections = Sections(xdata);
        foreach (var section in sections.OrderBy(section => IsPluginApp(section.App) ? 0 : 1))
        {
            var value = Find(xdata, section.App, section.Start, section.End);
            if (value != null) return value;
        }
        return null;
    }

    private static List<(string App, int Start, int End)> Sections(IReadOnlyList<DataValue> xdata)
    {
        var sections = new List<(string App, int Start, int End)>();
        var index = 0;
        while (index < xdata.Count)
        {
            if (xdata[index].TypeCode != 1001) { index++; continue; }
            var app = xdata[index].Value as string ?? "";
            var end = index + 1;
            while (end < xdata.Count && xdata[end].TypeCode != 1001) end++;
            sections.Add((app, index + 1, end));
            index = end;
        }
        // Values before the first RegApp (not produced by AutoCAD, kept for detached data) form a section without a name.
        if (xdata.Count != 0 && xdata[0].TypeCode != 1001)
        {
            var end = 0;
            while (end < xdata.Count && xdata[end].TypeCode != 1001) end++;
            sections.Insert(0, ("", 0, end));
        }
        return sections;
    }

    private static bool IsPluginApp(string app) =>
        app.Equals(DrawingNames.RegApp, StringComparison.OrdinalIgnoreCase) || app.Equals(DrawingNames.BackupRegApp, StringComparison.OrdinalIgnoreCase);

    private static string? Find(IReadOnlyList<DataValue> xdata, string app, int start, int end)
    {
        if (ParameterSchema.SameId(app, SystemTypeKey))
        {
            for (var index = start; index < end; index++)
                if (xdata[index].TypeCode == 1000 && NonEmpty(xdata[index].Value as string) is string text) return text;
            return null;
        }
        if (IsTyped(xdata, start, end))
        {
            // XDATA v2: (1070 . 2) and pairs; a string value equal to "SYSTEMTYPE" is not mistaken for an Id.
            foreach (var node in XDataV2.Tokens.ReadPairs(xdata, start + 1, end, out _))
                if (node.Id != null && ParameterSchema.SameId(node.Id.Trim(), SystemTypeKey) && node.Value.Count == 1 && IsScalar(node.Value[0]))
                    return NonEmpty(XDataV2.FormatValue(node.Value[0].Value));
            return null;
        }
        // v1: key=value strings, at any depth of lists as the header fields of XDataParser.
        for (var index = start; index < end; index++)
        {
            var value = xdata[index];
            if (value.TypeCode != 1000 || !(value.Value is string text)) continue;
            var equals = text.IndexOf('=');
            if (equals > 0 && ParameterSchema.SameId(text.Substring(0, equals).Trim(), SystemTypeKey) &&
                NonEmpty(text.Substring(equals + 1)) is string field) return field;
        }
        return null;
    }

    private static bool IsTyped(IReadOnlyList<DataValue> xdata, int start, int end) =>
        start < end && xdata[start].TypeCode == 1070 &&
        (xdata[start].Value switch { short s => s, int n => n, long l => l, _ => 0L }) >= XDataV2.FormatVersion;

    private static bool IsScalar(DataValue value) =>
        value.TypeCode == 1000 || value.TypeCode == 1040 || value.TypeCode == 1070 || value.TypeCode == 1071;

    private static string? NonEmpty(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
