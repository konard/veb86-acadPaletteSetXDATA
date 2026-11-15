using System;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class PaletteControllerTests
{
    [Fact]
    public void CreatesOnlyOnFirstToggleAndReusesTheHost()
    {
        var source = new FakeThemeSource { CurrentTheme = PaletteTheme.Light };
        var host = new FakePaletteHost();
        var creations = 0;
        using var controller = new PaletteController(() => { creations++; return host; }, source);

        Assert.Equal(0, creations);
        Assert.Equal(0, source.Subscribers);
        controller.Toggle();
        Assert.True(host.Visible);
        Assert.Equal(PaletteTheme.Light, host.Theme);
        Assert.Equal(1, source.Subscribers);
        controller.Toggle();
        Assert.False(host.Visible);
        controller.Toggle();
        Assert.True(host.Visible);
        Assert.Equal(1, creations);
    }

    [Fact]
    public void ToggleReadsNativeVisibilityAfterTheUserClosesThePalette()
    {
        var host = new FakePaletteHost();
        using var controller = new PaletteController(() => host, new FakeThemeSource());
        controller.Toggle();
        host.Visible = false; // AutoCAD's native close button.
        controller.Toggle();
        Assert.True(host.Visible);
    }

    [Fact]
    public void ThemeChangesApplyEvenWhileHiddenAndStopAfterDisposal()
    {
        var source = new FakeThemeSource();
        var host = new FakePaletteHost();
        var controller = new PaletteController(() => host, source);
        controller.Toggle();
        controller.Toggle();
        source.Change(PaletteTheme.Light);
        Assert.Equal(PaletteTheme.Light, host.Theme);
        Assert.False(host.Visible);
        controller.Dispose();
        controller.Dispose();
        Assert.Equal(0, source.Subscribers);
        Assert.Equal(1, host.DisposeCalls);
        source.Change(PaletteTheme.Dark);
        Assert.Equal(PaletteTheme.Light, host.Theme);
        Assert.Throws<ObjectDisposedException>(() => controller.Toggle());
    }

    [Fact]
    public void DisposingAnUnusedControllerDoesNotCreateAWindow()
    {
        var source = new FakeThemeSource();
        using (new PaletteController(() => throw new InvalidOperationException(), source)) { }
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public void FailedCreationCanBeRetriedWithoutLeakingThemeSubscriptions()
    {
        var attempts = 0;
        var source = new FakeThemeSource();
        var host = new FakePaletteHost();
        using var controller = new PaletteController(
            () => ++attempts == 1 ? throw new InvalidOperationException("creation failed") : host, source);
        Assert.Throws<InvalidOperationException>(() => controller.Toggle());
        Assert.Equal(0, source.Subscribers);
        controller.Toggle();
        Assert.True(host.Visible);
        Assert.Equal(1, source.Subscribers);
    }

    private sealed class FakePaletteHost : IPaletteHost
    {
        public bool Visible { get; set; }
        public PaletteTheme Theme { get; private set; }
        public int DisposeCalls { get; private set; }
        public void ApplyTheme(PaletteTheme theme) => Theme = theme;
        public void Dispose() => DisposeCalls++;
    }

    private sealed class FakeThemeSource : IThemeSource
    {
        private EventHandler? changed;
        public PaletteTheme CurrentTheme { get; set; }
        public int Subscribers => changed?.GetInvocationList().Length ?? 0;
        public event EventHandler ThemeChanged
        {
            add => changed += value;
            remove => changed -= value;
        }
        public void Change(PaletteTheme theme)
        {
            CurrentTheme = theme;
            changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
