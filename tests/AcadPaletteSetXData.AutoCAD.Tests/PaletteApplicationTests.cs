using System;
using System.Linq;
using AcadPaletteSetXData.AutoCAD;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using Xunit;

namespace AcadPaletteSetXData.AutoCAD.Tests;

public sealed class PaletteApplicationTests : IDisposable
{
    private readonly PaletteApplication application = new PaletteApplication();

    public PaletteApplicationTests()
    {
        Application.DocumentManager.MdiActiveDocument = null;
        Application.IsQuiescent = true;
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
        // Issue #53: the plugin no longer offers a «Bobrov» menu or ribbon tab.
        Assert.DoesNotContain("Bobrov", message);
        Assert.DoesNotContain("XDATAPALETTE_O", message);
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

    // Issue #47: the bundle loads the plugin while AutoCAD opens the startup drawing; the plugin must not touch
    // the command line until the drawing is open and nothing runs.
    [Fact]
    public void LoadingDuringTheStartupDrawingOpenWaitsForAQuietIdle()
    {
        var document = new FakeDocument();
        Application.DocumentManager.MdiActiveDocument = document;
        Application.IsQuiescent = false;
        application.Initialize();
        Assert.Empty(document.Editor.Messages);
        Assert.Equal(1, Application.IdleSubscribers);

        Application.IsQuiescent = true;
        Application.DocumentManager.RaiseCreateStarted(document);
        Application.RaiseIdle();
        Assert.Empty(document.Editor.Messages);

        Application.DocumentManager.RaiseCreated(document);
        document.Editor.IsQuiescent = false;
        Application.RaiseIdle();
        document.Editor.IsQuiescent = true;
        document.CommandInProgress = "SELECT";
        Application.RaiseIdle();
        Assert.Empty(document.Editor.Messages);
        Assert.Equal(1, Application.IdleSubscribers);

        document.CommandInProgress = "";
        Application.RaiseIdle();
        Assert.Contains("XDATAPALETTE", Assert.Single(document.Editor.Messages));
        Assert.Equal(0, Application.IdleSubscribers);
        Assert.Equal(0, AutoCadPaletteHost.Creations);
    }

    [Fact]
    public void ACanceledDrawingOpenDoesNotBlockTheStartup()
    {
        var document = new FakeDocument();
        application.Initialize();
        Application.DocumentManager.RaiseCreateStarted(document);
        Application.DocumentManager.MdiActiveDocument = document;
        Application.RaiseIdle();
        Assert.Empty(document.Editor.Messages);
        Application.DocumentManager.RaiseCreationCanceled(document);
        Application.RaiseIdle();
        Assert.Single(document.Editor.Messages);
    }

    [Fact]
    public void TerminationStopsWatchingTheDrawingOpen()
    {
        application.Initialize();
        Assert.Equal(3, Application.DocumentManager.CreationSubscribers);
        application.Initialize();
        Assert.Equal(3, Application.DocumentManager.CreationSubscribers);
        application.Terminate();
        Assert.Equal(0, Application.DocumentManager.CreationSubscribers);
    }

    // Issue #53: menus and ribbon tabs belong to a separate module; this plugin keeps only the command line
    // toggle XDATAPALETTE (XDATAPALETTE_ON/XDATAPALETTE_OFF of the removed «Bobrov» buttons are gone).
    [Fact]
    public void OnlyTheXDataPaletteToggleCommandIsRegistered()
    {
        var commands = typeof(PaletteApplication).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(CommandMethodAttribute), false)
                .Cast<CommandMethodAttribute>()
                .Select(attribute => (attribute.GlobalName, attribute.Flags, method.Name)))
            .ToList();

        var command = Assert.Single(commands);
        Assert.Equal("XDATAPALETTE", command.GlobalName);
        Assert.Equal(nameof(PaletteApplication.TogglePalette), command.Name);
        Assert.True(command.Flags.HasFlag(CommandFlags.Session));
        Assert.True(command.Flags.HasFlag(CommandFlags.UsePickSet));
    }

    [Fact]
    public void TheToggleCommandShowsAndHidesOnePalette()
    {
        var document = new FakeDocument();
        Application.DocumentManager.MdiActiveDocument = document;

        PaletteApplication.TogglePalette();
        Assert.Equal(1, AutoCadPaletteHost.Creations);
        Assert.True(AutoCadPaletteHost.Last!.Visible);

        PaletteApplication.TogglePalette();
        Assert.False(AutoCadPaletteHost.Last.Visible);

        // The palette closed by its title bar is shown again by the next toggle.
        PaletteApplication.TogglePalette();
        AutoCadPaletteHost.Last.Visible = false;
        PaletteApplication.TogglePalette();
        Assert.True(AutoCadPaletteHost.Last.Visible);
        Assert.Equal(1, AutoCadPaletteHost.Creations);
        Assert.Empty(document.Editor.Messages);
    }

    public void Dispose()
    {
        application.Terminate();
        Application.DocumentManager.MdiActiveDocument = null;
        Application.IsQuiescent = true;
    }
}
