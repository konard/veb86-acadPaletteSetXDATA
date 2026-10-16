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

/// <summary>Owns the selection subscription; a failed read cannot leave stale object data visible.</summary>
public sealed class SelectionController : IDisposable
{
    private readonly ISelectionSource source;
    private readonly SelectionViewModel model;
    private bool disposed;

    public SelectionController(ISelectionSource source, SelectionViewModel model)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        model.Editor.SetWritable(source is ISelectionWriter);
        model.SetWritable(source is ISelectionWriter);
        model.Editor.EditRequested += Write;
        model.EditRequested += Write;
        source.SelectionChanged += Refresh;
        model.RefreshRequested += Refresh;
        Refresh(this, EventArgs.Empty);
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (disposed) return;
        try { model.Apply(source.ReadSelection(), cache: (source as IObjectValuesSource)?.Values); }
        catch (Exception error)
        {
            Trace.WriteLine(error, "XDATAPALETTE selection read");
            model.Apply(Array.Empty<EntityDataSnapshot>(), "Ошибка чтения: " + error.Message);
        }
    }

    private void Write(object? sender, SelectionEdit edit)
    {
        if (disposed || !(source is ISelectionWriter writer)) return;
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.SelectionChanged -= Refresh;
        model.RefreshRequested -= Refresh;
        model.Editor.EditRequested -= Write;
        model.EditRequested -= Write;
        model.Editor.SetWritable(false);
        model.SetWritable(false);
    }
}
