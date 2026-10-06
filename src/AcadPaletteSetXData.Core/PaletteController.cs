using System;

namespace AcadPaletteSetXData.Core;

/// <summary>Owns one lazily created palette for the lifetime of the plugin.</summary>
public sealed class PaletteController : IDisposable
{
    private readonly Func<IPaletteHost> createHost;
    private readonly IThemeSource themeSource;
    private IPaletteHost? host;
    private bool disposed;

    public PaletteController(Func<IPaletteHost> createHost, IThemeSource themeSource)
    {
        this.createHost = createHost ?? throw new ArgumentNullException(nameof(createHost));
        this.themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));
    }

    public void Toggle()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(PaletteController));

        if (host == null)
        {
            var created = createHost();
            try
            {
                created.ApplyTheme(themeSource.CurrentTheme);
            }
            catch
            {
                created.Dispose();
                throw;
            }

            host = created;
            themeSource.ThemeChanged += OnThemeChanged;
        }

        // Read native visibility: the user can also close the palette via its title bar.
        host.Visible = !host.Visible;
    }

    private void OnThemeChanged(object? sender, EventArgs args)
    {
        host?.ApplyTheme(themeSource.CurrentTheme);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        themeSource.ThemeChanged -= OnThemeChanged;
        host?.Dispose();
        host = null;
    }
}
