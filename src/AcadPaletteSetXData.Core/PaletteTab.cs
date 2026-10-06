namespace AcadPaletteSetXData.Core;

public sealed class PaletteTab
{
    public PaletteTab(string header, string title, string description, object? content = null)
    {
        Header = header;
        Title = title;
        Description = description;
        Content = content ?? this;
    }

    public string Header { get; }
    public string Title { get; }
    public string Description { get; }
    public object Content { get; }
}
