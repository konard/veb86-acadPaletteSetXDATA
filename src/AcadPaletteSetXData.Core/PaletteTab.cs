namespace AcadPaletteSetXData.Core;

public sealed class PaletteTab
{
    public PaletteTab(string header, string title, string description)
    {
        Header = header;
        Title = title;
        Description = description;
    }

    public string Header { get; }
    public string Title { get; }
    public string Description { get; }
}
