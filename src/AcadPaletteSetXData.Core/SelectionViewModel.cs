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
    private string? error;
    private bool canWrite;

    public SelectionViewModel(PropertyEditorViewModel editor, bool enableDraftPreview = false)
    {
        this.editor = editor; Editor = editor;
        if (!enableDraftPreview) editor.LoadSelection(Array.Empty<ParsedEntityData>());
        else Status = "Предпросмотр локального черновика";
    }

    public ObservableCollection<ParsedEntityData> Entities { get; } = new ObservableCollection<ParsedEntityData>();
    /// <summary>XDATA v2 values of the selection from the value cache (TZXDATA 10.2), in the order of <see cref="Entities"/>.</summary>
    public IReadOnlyList<ObjectValues> Values { get; private set; } = Array.Empty<ObjectValues>();
    /// <summary><c>CalculatedString</c> results of <see cref="Values"/> computed from the cache (TZXDATA 7.3), in the same order.</summary>
    public IReadOnlyList<ObjectCalculation> Calculations { get; private set; } = Array.Empty<ObjectCalculation>();
    public ObservableCollection<XDataNode> Tree { get; } = new ObservableCollection<XDataNode>();
    public bool HasSelection { get => hasSelection; private set => SetProperty(ref hasSelection, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Diagnostics { get => diagnostics; private set => SetProperty(ref diagnostics, value); }
    /// <summary>The error of the last read or write, shown above the VST (TZXDATA 8.5); null after a successful read.</summary>
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    /// <summary>The source can write the selection: the VST edits values (TZXDATA 8.5, stage 6).</summary>
    public bool CanWrite { get => canWrite; private set => SetProperty(ref canWrite, value); }

    /// <summary>Raised at the end of <see cref="Apply"/>: the selection, its values and calculations are replaced.</summary>
    public event EventHandler? Applied;
    /// <summary>The palette asks to read the selection again (⟳ of the VST, TZXDATA 8.4).</summary>
    public event EventHandler? RefreshRequested;
    /// <summary>The VST confirmed a change of the selection: the controller writes it to every object in one transaction.</summary>
    public event EventHandler<SelectionEdit>? EditRequested;

    /// <summary>Asks the source to read the selection again; false when no source listens.</summary>
    public bool RequestRefresh()
    {
        var handler = RefreshRequested;
        if (handler is null) return false;
        handler(this, EventArgs.Empty);
        return true;
    }

    internal void SetWritable(bool value) => CanWrite = value;

    /// <summary>
    /// Asks the writer to apply <paramref name="edit"/> to the whole selection. The controller writes and reads the
    /// selection again synchronously: false when nothing can write it or the write failed (<see cref="Error"/>).
    /// </summary>
    public bool RequestEdit(SelectionEdit edit)
    {
        if (edit is null) throw new ArgumentNullException(nameof(edit));
        var handler = EditRequested;
        if (!CanWrite || handler is null || Entities.Count == 0) return false;
        handler(this, edit);
        return Error is null;
    }

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
        // With the value cache an unchanged object is not parsed again (TZXDATA 10.6).
        var parsed = snapshots.Select(snapshot => cache?.Parse(snapshot) ?? new XDataParser().Parse(snapshot)).ToArray();
        var values = cache?.Get(snapshots) ?? Array.Empty<ObjectValues>();
        var calculations = values.Select(item => new ObjectCalculation(item)).ToArray();
        AddWarnings(parsed, values, calculations);
        Values = values;
        Calculations = calculations;
        SelectedEntity = null;
        Tree.Clear(); Entities.Clear();
        foreach (var entity in parsed) Entities.Add(entity);
        HasSelection = Entities.Count != 0;
        SelectedEntity = Entities.FirstOrDefault(entity => entity.Handle == previousHandle) ?? Entities.FirstOrDefault();
        editor.LoadSelection(Entities);
        Error = error;
        Status = error ?? (HasSelection ? "Выделено объектов: " + Entities.Count + " • Групповые свойства" : "Выберите объекты в чертеже");
        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// v2 warnings (TZXDATA 6.5), duplicates of <c>DEVICE_ID</c> in the selection (6.4) and errors of the formulas an object
    /// uses (7.3) go to the diagnostics.
    /// </summary>
    private static void AddWarnings(IReadOnlyList<ParsedEntityData> parsed, IReadOnlyList<ObjectValues> values, IReadOnlyList<ObjectCalculation> calculations)
    {
        if (values.Count == 0) return;
        var byHandle = parsed.GroupBy(entity => entity.Handle, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in values)
            if (byHandle.TryGetValue(item.Handle, out var entity))
                foreach (var warning in item.Warnings) entity.Warnings.Add(warning);
        foreach (var calculation in calculations)
            if (byHandle.TryGetValue(calculation.Handle, out var entity))
                foreach (var warning in FormulaWarnings(calculation)) entity.Warnings.Add(warning);
        foreach (var duplicate in XDataV2.FindDuplicateDeviceIds(values))
        {
            if (byHandle.TryGetValue(duplicate.Original, out var original))
                original.Warnings.Add(XDataV2.DeviceIdKey + " " + duplicate.DeviceId + " повторяется у объектов " + string.Join(", ", duplicate.Copies) + "; этот объект сохраняет ID.");
            foreach (var copy in duplicate.Copies)
                if (byHandle.TryGetValue(copy, out var entity))
                    entity.Warnings.Add(XDataV2.DeviceIdKey + " " + duplicate.DeviceId + " совпадает с объектом " + duplicate.Original + "; копии нужен новый GUID.");
        }
    }

    /// <summary>Formulas stored on the object or listed in its template: errors and stale <c>Persist</c> values.</summary>
    internal static IEnumerable<string> FormulaWarnings(ObjectCalculation calculation)
    {
        foreach (var result in calculation.Results)
        {
            if (!calculation.IsUsed(result.Id)) continue;
            if (result.IsError) yield return result.Id + ": " + result.Error + ".";
            else if (result.IsStale)
                yield return result.Id + ": записано \"" + result.Stored + "\", вычислено \"" + result.Text + "\"; значение Persist обновится при изменении исходных параметров.";
        }
    }
}
