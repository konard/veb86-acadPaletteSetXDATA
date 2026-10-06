using System;
using System.Collections.Generic;
using System.Diagnostics;

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
        source.SelectionChanged += Refresh;
        Refresh(this, EventArgs.Empty);
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (disposed) return;
        try { model.Apply(source.ReadSelection()); }
        catch (Exception error)
        {
            Trace.WriteLine(error, "XDATAPALETTE selection read");
            model.Apply(Array.Empty<EntityDataSnapshot>(), "Ошибка чтения: " + error.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.SelectionChanged -= Refresh;
    }
}
