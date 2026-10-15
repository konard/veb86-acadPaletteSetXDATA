using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AcadPaletteSetXData.Core;

public sealed class SelectionViewModel : ObservableObject
{
    internal PropertyEditorViewModel Editor { get; }
    private readonly PropertyEditorViewModel editor;
    private ParsedEntityData? selectedEntity;
    private bool hasSelection;
    private string status = "Выберите объекты в чертеже";
    private string diagnostics = "";

    public SelectionViewModel(PropertyEditorViewModel editor, bool enableDraftPreview = false)
    {
        this.editor = editor; Editor = editor;
        if (!enableDraftPreview) editor.LoadSelection(Array.Empty<ParsedEntityData>());
        else Status = "Предпросмотр локального черновика";
    }

    public ObservableCollection<ParsedEntityData> Entities { get; } = new ObservableCollection<ParsedEntityData>();
    /// <summary>XDATA v2 values of the selection from the value cache (TZXDATA 10.2), in the order of <see cref="Entities"/>.</summary>
    public IReadOnlyList<ObjectValues> Values { get; private set; } = Array.Empty<ObjectValues>();
    public ObservableCollection<XDataNode> Tree { get; } = new ObservableCollection<XDataNode>();
    public bool HasSelection { get => hasSelection; private set => SetProperty(ref hasSelection, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Diagnostics { get => diagnostics; private set => SetProperty(ref diagnostics, value); }

    public ParsedEntityData? SelectedEntity
    {
        get => selectedEntity;
        set
        {
            if (ReferenceEquals(selectedEntity, value)) return;
            if (value != null && !Entities.Contains(value)) throw new ArgumentException("Объект отсутствует в выделении.", nameof(value));
            SetProperty(ref selectedEntity, value);
            Tree.Clear();
            if (value != null) Tree.Add(value.Tree);
            Diagnostics = value == null ? "" : string.Join("\n", value.Warnings);
        }
    }

    public void Apply(IReadOnlyList<EntityDataSnapshot> snapshots, string? error = null, ObjectValuesCache? cache = null)
    {
        var previousHandle = SelectedEntity?.Handle;
        // Parse first, then replace the whole selection so a failure cannot expose a partial update.
        var parsed = snapshots.Select(snapshot => new XDataParser().Parse(snapshot)).ToArray();
        var values = cache?.Get(snapshots) ?? Array.Empty<ObjectValues>();
        AddWarnings(parsed, values);
        Values = values;
        SelectedEntity = null;
        Tree.Clear(); Entities.Clear();
        foreach (var entity in parsed) Entities.Add(entity);
        HasSelection = Entities.Count != 0;
        SelectedEntity = Entities.FirstOrDefault(entity => entity.Handle == previousHandle) ?? Entities.FirstOrDefault();
        editor.LoadSelection(Entities);
        Status = error ?? (HasSelection ? "Выделено объектов: " + Entities.Count + " • Групповые свойства" : "Выберите объекты в чертеже");
    }

    /// <summary>v2 warnings (TZXDATA 6.5) and duplicates of <c>DEVICE_ID</c> in the selection (6.4) go to the diagnostics.</summary>
    private static void AddWarnings(IReadOnlyList<ParsedEntityData> parsed, IReadOnlyList<ObjectValues> values)
    {
        if (values.Count == 0) return;
        var byHandle = parsed.GroupBy(entity => entity.Handle, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in values)
            if (byHandle.TryGetValue(item.Handle, out var entity))
                foreach (var warning in item.Warnings) entity.Warnings.Add(warning);
        foreach (var duplicate in XDataV2.FindDuplicateDeviceIds(values))
        {
            if (byHandle.TryGetValue(duplicate.Original, out var original))
                original.Warnings.Add(XDataV2.DeviceIdKey + " " + duplicate.DeviceId + " повторяется у объектов " + string.Join(", ", duplicate.Copies) + "; этот объект сохраняет ID.");
            foreach (var copy in duplicate.Copies)
                if (byHandle.TryGetValue(copy, out var entity))
                    entity.Warnings.Add(XDataV2.DeviceIdKey + " " + duplicate.DeviceId + " совпадает с объектом " + duplicate.Original + "; копии нужен новый GUID.");
        }
    }
}
