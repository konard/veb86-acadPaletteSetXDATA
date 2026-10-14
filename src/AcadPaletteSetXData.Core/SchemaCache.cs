using System;
using System.Collections.Generic;

namespace AcadPaletteSetXData.Core;

/// <summary>Schema read from NOD with dictionaries by Id (case-insensitive, first object wins on duplicates).</summary>
public sealed class SchemaSnapshot
{
    public SchemaSnapshot(SchemaNodReadResult read)
    {
        Read = read ?? throw new ArgumentNullException(nameof(read));
        var schema = read.Schema;
        if (schema is null) return;
        foreach (var parameter in schema.Parameters) Add(parameters, parameter.Id, parameter);
        foreach (var item in schema.Enums) Add(enums, item.Id, item);
        foreach (var template in schema.Templates) Add(templates, template.Id, template);
    }

    private readonly Dictionary<string, SchemaParameter> parameters = new Dictionary<string, SchemaParameter>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SchemaEnum> enums = new Dictionary<string, SchemaEnum>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SchemaTemplate> templates = new Dictionary<string, SchemaTemplate>(StringComparer.OrdinalIgnoreCase);

    public SchemaNodReadResult Read { get; }
    public ParameterSchema? Schema => Read.Schema;
    public IReadOnlyDictionary<string, SchemaParameter> Parameters => parameters;
    public IReadOnlyDictionary<string, SchemaEnum> Enums => enums;
    public IReadOnlyDictionary<string, SchemaTemplate> Templates => templates;

    private static void Add<T>(Dictionary<string, T> map, string? id, T item)
    {
        if (id != null && !map.ContainsKey(id)) map.Add(id, item);
    }
}

/// <summary>
/// Schema cache of one document (TZXDATA 10.1): built from NOD on first use and kept until
/// <see cref="Invalidate"/> (changes of <c>VELEC</c> objects, import, Undo/Redo, another document).
/// </summary>
public sealed class SchemaCache
{
    private readonly Func<SchemaNodReadResult> load;
    private SchemaSnapshot? snapshot;

    public SchemaCache(Func<SchemaNodReadResult> load) { this.load = load ?? throw new ArgumentNullException(nameof(load)); }

    /// <summary>How many times NOD was read; used by tests to check that the cache is reused.</summary>
    public int LoadCount { get; private set; }
    public bool IsLoaded => snapshot != null;

    /// <summary>Raised when a loaded snapshot is dropped.</summary>
    public event EventHandler? Invalidated;

    public SchemaSnapshot Get()
    {
        if (snapshot is null)
        {
            LoadCount++;
            snapshot = new SchemaSnapshot(load());
        }
        return snapshot;
    }

    public void Invalidate()
    {
        if (snapshot is null) return;
        snapshot = null;
        Invalidated?.Invoke(this, EventArgs.Empty);
    }
}
