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

    /// <summary>True when the palette exists and AutoCAD shows it.</summary>
    public bool IsVisible => host?.Visible == true;

    public void Toggle()
    {
        // Read native visibility: the user can also close the palette via its title bar.
        var current = EnsureHost();
        current.Visible = !current.Visible;
    }

    /// <summary>The «Включить» button of the Bobrov menu and ribbon: shows the palette, creating it on first use.</summary>
    public void Show()
    {
        var current = EnsureHost();
        if (!current.Visible) current.Visible = true;
    }

    /// <summary>The «Выключить» button: hides the palette; a palette that was never shown is not created.</summary>
    public void Hide()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(PaletteController));
        if (host != null && host.Visible) host.Visible = false;
    }

    private IPaletteHost EnsureHost()
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

        return host;
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
