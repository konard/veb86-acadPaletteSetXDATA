using System;
using AcadPaletteSetXData.AutoCAD;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Xunit;

namespace AcadPaletteSetXData.AutoCAD.Tests;

public sealed class PaletteApplicationTests : IDisposable
{
    private readonly PaletteApplication application = new PaletteApplication();

    public PaletteApplicationTests()
    {
        Application.DocumentManager.MdiActiveDocument = null;
        AutoCadPaletteHost.Creations = 0;
    }

    [Fact]
    public void LoadingAnnouncesCommandWithoutCreatingPalette()
    {
        var document = new FakeDocument();
        Application.DocumentManager.MdiActiveDocument = document;
        application.Initialize();
        var message = Assert.Single(document.Editor.Messages);
        Assert.Contains("acadPaletteSetXDATA", message);
        Assert.Contains("загружено", message);
        Assert.Contains("XDATAPALETTE", message);
        Assert.Equal(0, AutoCadPaletteHost.Creations);
        Assert.Equal(0, Application.IdleSubscribers);
        Application.RaiseIdle();
        Assert.Single(document.Editor.Messages);
        PaletteApplication.TogglePalette();
        Assert.Equal(1, AutoCadPaletteHost.Creations);
    }

    [Fact]
    public void LoadingWithoutDrawingAnnouncesOnceWhenDrawingBecomesAvailable()
    {
        application.Initialize();
        Assert.Equal(1, Application.IdleSubscribers);
        Application.RaiseIdle();
        Assert.Equal(0, AutoCadPaletteHost.Creations);
        var document = new FakeDocument();
        Application.DocumentManager.MdiActiveDocument = document;
        Application.RaiseIdle();
        Assert.Contains("XDATAPALETTE", Assert.Single(document.Editor.Messages));
        Assert.Equal(0, Application.IdleSubscribers);
        Application.RaiseIdle();
        Application.DocumentManager.MdiActiveDocument = new FakeDocument();
        Application.RaiseIdle();
        Assert.Empty(Application.DocumentManager.MdiActiveDocument.Editor.Messages);
        Assert.Equal(0, AutoCadPaletteHost.Creations);
    }

    [Fact]
    public void TerminationCancelsPendingAnnouncement()
    {
        application.Initialize();
        application.Terminate();
        Assert.Equal(0, Application.IdleSubscribers);
        var document = new FakeDocument();
        Application.DocumentManager.MdiActiveDocument = document;
        Application.RaiseIdle();
        Assert.Empty(document.Editor.Messages);
    }

    public void Dispose()
    {
        application.Terminate();
        Application.DocumentManager.MdiActiveDocument = null;
    }
}
