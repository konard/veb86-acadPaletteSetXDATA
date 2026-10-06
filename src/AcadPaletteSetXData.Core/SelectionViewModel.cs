using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AcadPaletteSetXData.Core;

public sealed class SelectionViewModel : ObservableObject
{
    private readonly PropertyEditorViewModel editor;
    private ParsedEntityData? selectedEntity;
    private bool hasSelection;
    private string status = "Выберите объекты в чертеже";
    private string diagnostics = "";

    public SelectionViewModel(PropertyEditorViewModel editor, bool enableDraftPreview = false)
    {
        this.editor = editor;
        if (!enableDraftPreview) editor.LoadSelection(null);
        else Status = "Предпросмотр локального черновика";
    }

    public ObservableCollection<ParsedEntityData> Entities { get; } = new ObservableCollection<ParsedEntityData>();
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
            editor.LoadSelection(value);
            Tree.Clear();
            if (value != null) Tree.Add(value.Tree);
            Diagnostics = value == null ? "" : string.Join("\n", value.Warnings);
        }
    }

    public void Apply(IReadOnlyList<EntityDataSnapshot> snapshots, string? error = null)
    {
        var previousHandle = SelectedEntity?.Handle;
        // Parse first, then replace the whole selection so a failure cannot expose a partial update.
        var parsed = snapshots.Select(snapshot => new XDataParser().Parse(snapshot)).ToArray();
        SelectedEntity = null;
        editor.LoadSelection(null);
        Tree.Clear(); Entities.Clear();
        foreach (var entity in parsed) Entities.Add(entity);
        HasSelection = Entities.Count != 0;
        SelectedEntity = Entities.FirstOrDefault(entity => entity.Handle == previousHandle) ?? Entities.FirstOrDefault();
        Status = error ?? (HasSelection ? "Выделено объектов: " + Entities.Count + " • Просмотр данных" : "Выберите объекты в чертеже");
    }
}
