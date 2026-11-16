using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AcadPaletteSetXData.Core;

public interface ISelectionSource
{
    event EventHandler SelectionChanged;
    IReadOnlyList<EntityDataSnapshot> ReadSelection();
}

/// <summary>
/// Issue #59: a source that counts the selection without reading the objects. The controller asks the count first and
/// does not read a selection over <see cref="PaletteSettings.MaxSelectionCount"/>.
/// </summary>
public interface ISelectionCounter
{
    int CountSelection();
}

/// <summary>
/// Issue #59: a source that can stop following the selection of the drawing. While <see cref="Tracking"/> is false
/// the source takes no part in selecting objects: it is not subscribed to the selection events and raises nothing.
/// </summary>
public interface ISelectionTracker
{
    bool Tracking { get; set; }
}

/// <summary>
/// Owns the selection subscription; a failed read cannot leave stale object data visible.
/// Issue #59: an inactive controller (the palette is hidden) neither reads the selection nor lets the source follow it;
/// a selection over <see cref="PaletteSettings.MaxSelectionCount"/> is counted, not read.
/// </summary>
public sealed class SelectionController : IDisposable
{
    private readonly ISelectionSource source;
    private readonly SelectionViewModel model;
    private readonly PaletteSettings? settings;
    private bool active;
    private bool disposed;

    /// <param name="settings">Gives the selection limit (issue #59); null — no limit.</param>
    /// <param name="active">False: the palette is hidden, nothing is read until <see cref="Active"/> is set.</param>
    public SelectionController(ISelectionSource source, SelectionViewModel model, PaletteSettings? settings = null, bool active = true)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.settings = settings;
        model.Editor.SetWritable(source is ISelectionWriter);
        model.SetWritable(source is ISelectionWriter);
        model.Editor.EditRequested += Write;
        model.EditRequested += Write;
        model.SelectionChangeRequested += ChangeSelection;
        model.SetSelectionChangeable(source is ISelectionChanger);
        source.SelectionChanged += Refresh;
        model.RefreshRequested += Refresh;
        if (settings != null) settings.PropertyChanged += OnSettingsChanged;
        this.active = active;
        if (source is ISelectionTracker tracker) tracker.Tracking = active;
        if (active) Refresh(this, EventArgs.Empty);
    }

    /// <summary>
    /// Issue #59: the palette is shown. Setting true makes the source follow the selection and reads it at once;
    /// setting false makes the source stop following it and clears the palette, so a hidden palette takes no part in
    /// selecting objects.
    /// </summary>
    public bool Active
    {
        get => active;
        set
        {
            if (disposed || active == value) return;
            active = value;
            if (source is ISelectionTracker tracker) tracker.Tracking = value;
            if (value) Refresh(this, EventArgs.Empty);
            else model.Apply(Array.Empty<EntityDataSnapshot>());
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // A new limit applies to the current selection at once.
        if (args.PropertyName == nameof(PaletteSettings.MaxSelectionCount)) Refresh(this, EventArgs.Empty);
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (disposed || !active) return;
        try
        {
            // Issue #59: the count goes first, so the objects of a too large selection are not opened at all.
            if (settings != null && source is ISelectionCounter counter)
            {
                var count = counter.CountSelection();
                if (settings.IsOverSelectionLimit(count)) { model.ApplyOverLimit(count, settings.MaxSelectionCount); return; }
            }
            var snapshots = source.ReadSelection();
            if (settings != null && settings.IsOverSelectionLimit(snapshots.Count))
            {
                model.ApplyOverLimit(snapshots.Count, settings.MaxSelectionCount);
                return;
            }
            model.Apply(snapshots, cache: (source as IObjectValuesSource)?.Values);
        }
        catch (Exception error)
        {
            Trace.WriteLine(error, "XDATAPALETTE selection read");
            model.Apply(Array.Empty<EntityDataSnapshot>(), "Ошибка чтения: " + error.Message);
        }
    }

    private void Write(object? sender, SelectionEdit edit)
    {
        if (disposed || !active || !(source is ISelectionWriter writer)) return;
        try
        {
            writer.WriteSelection(model.Entities.Select(entity => entity.Handle).ToArray(), edit);
            Refresh(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            Trace.WriteLine(error, "XDATAPALETTE selection write");
            // The group is rolled back: the fresh values come back with the error, the selection is kept.
            try { model.Apply(source.ReadSelection(), "Ошибка записи: " + error.Message, (source as IObjectValuesSource)?.Values); }
            catch { model.Apply(Array.Empty<EntityDataSnapshot>(), "Ошибка записи: " + error.Message); }
        }
    }

    /// <summary>* and − of the group list (issue #41): select the asked objects, then read the selection and rebuild the list.</summary>
    private void ChangeSelection(object? sender, SelectionChange change)
    {
        if (disposed || !active || !(source is ISelectionChanger changer)) return;
        try
        {
            changer.SetSelection(change.Handles);
            Refresh(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            Trace.WriteLine(error, "XDATAPALETTE selection change");
            try { model.Apply(source.ReadSelection(), "Ошибка выделения: " + error.Message, (source as IObjectValuesSource)?.Values); }
            catch { model.Apply(Array.Empty<EntityDataSnapshot>(), "Ошибка выделения: " + error.Message); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (settings != null) settings.PropertyChanged -= OnSettingsChanged;
        source.SelectionChanged -= Refresh;
        model.RefreshRequested -= Refresh;
        model.Editor.EditRequested -= Write;
        model.EditRequested -= Write;
        model.SelectionChangeRequested -= ChangeSelection;
        model.SetSelectionChangeable(false);
        model.Editor.SetWritable(false);
        model.SetWritable(false);
    }
}
