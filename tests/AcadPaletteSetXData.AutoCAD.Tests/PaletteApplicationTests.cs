using System;
using System.Linq;
using AcadPaletteSetXData.Core;
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
        BobrovInterface.Starts = 0;
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

    [Fact]
    public void LoadingStartsTheBobrovMenuAndRibbonAndTerminationRemovesTheirHandlers()
    {
        application.Initialize();
        Assert.True(BobrovInterface.Running);
        Assert.Equal(1, BobrovInterface.Starts);
        Assert.Equal(0, AutoCadPaletteHost.Creations);
        application.Terminate();
        Assert.False(BobrovInterface.Running);
    }

    [Fact]
    public void BobrovOnAndOffCommandsShowAndHideOnePalette()
    {
        var document = new FakeDocument();
        Application.DocumentManager.MdiActiveDocument = document;

        PaletteApplication.HidePalette();
        Assert.Equal(0, AutoCadPaletteHost.Creations);

        PaletteApplication.ShowPalette();
        PaletteApplication.ShowPalette();
        Assert.Equal(1, AutoCadPaletteHost.Creations);
        Assert.True(AutoCadPaletteHost.Last!.Visible);

        PaletteApplication.HidePalette();
        PaletteApplication.HidePalette();
        Assert.False(AutoCadPaletteHost.Last.Visible);

        PaletteApplication.TogglePalette();
        Assert.True(AutoCadPaletteHost.Last.Visible);
        Assert.Equal(1, AutoCadPaletteHost.Creations);
        Assert.Empty(document.Editor.Messages);
    }

    [Fact]
    public void BobrovButtonsCallRegisteredSessionCommands()
    {
        var commands = typeof(PaletteApplication).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(CommandMethodAttribute), false)
                .Cast<CommandMethodAttribute>()
                .Select(attribute => (attribute.GlobalName, attribute.Flags, method.Name)))
            .ToDictionary(item => item.GlobalName);

        Assert.Equal(new[] { "XDATAPALETTE", "XDATAPALETTE_OFF", "XDATAPALETTE_ON" }, commands.Keys.OrderBy(name => name));
        foreach (var button in BobrovUi.PaletteCommands)
        {
            Assert.True(commands.ContainsKey(button.Command), button.Command);
            Assert.True(commands[button.Command].Flags.HasFlag(CommandFlags.Session), button.Command);
        }
        Assert.Equal(nameof(PaletteApplication.ShowPalette), commands[BobrovUi.ShowCommand].Name);
        Assert.Equal(nameof(PaletteApplication.HidePalette), commands[BobrovUi.HideCommand].Name);
    }

    public void Dispose()
    {
        application.Terminate();
        Application.DocumentManager.MdiActiveDocument = null;
        Application.IsQuiescent = true;
    }
}
