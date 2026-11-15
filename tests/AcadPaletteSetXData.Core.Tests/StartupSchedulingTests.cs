using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.BobrovUiTests;

namespace AcadPaletteSetXData.Core.Tests;

// Issue #47: with the bundle loading the plugin at AutoCAD startup, «Выберите объекты» flickered in the command
// line and the startup drawing did not open until the prompt was cancelled. The startup work (the «Bobrov» menu,
// MENUBAR, the ribbon tab, the load message) ran on the first Idle, also while the drawing was being opened or a
// command waited for input, and every menu change that raised WSCURRENT scheduled the next installation.
public sealed class StartupSchedulingTests
{
    [Fact]
    public void StartupWorkWaitsUntilADrawingIsOpenAndNothingRuns()
    {
        var readiness = new StartupReadiness();
        Assert.True(readiness.IsReady(true, true, true, ""));
        Assert.True(readiness.IsReady(true, true, true, null));
        Assert.Null(readiness.WaitReason(true, true, true, ""));

        Assert.False(readiness.IsReady(false, true, true, ""));
        Assert.False(readiness.IsReady(true, false, true, ""));
        Assert.False(readiness.IsReady(true, true, false, ""));
        Assert.False(readiness.IsReady(true, true, true, "SELECT"));
        Assert.Contains("SELECT", readiness.WaitReason(true, true, true, "SELECT"));

        readiness.DocumentCreateStarted();
        Assert.True(readiness.OpeningDocument);
        Assert.False(readiness.IsReady(true, true, true, ""));
        Assert.Equal("чертёж открывается", readiness.WaitReason(true, true, true, ""));
        readiness.DocumentCreateFinished();
        Assert.True(readiness.IsReady(true, true, true, ""));
    }

    [Fact]
    public void InstallationRunsOnceOnTheFirstQuietIdle()
    {
        var runs = 0;
        var scheduler = new BobrovInstallScheduler(() => runs++);
        Assert.False(scheduler.OnIdle(true));
        Assert.Equal(0, runs);

        Assert.True(scheduler.Request());
        Assert.True(scheduler.Pending);
        // The startup drawing is being opened, a command or LISP waits for input: nothing is changed.
        for (var i = 0; i < 10; i++) Assert.False(scheduler.OnIdle(false));
        Assert.Equal(0, runs);
        Assert.True(scheduler.Pending);

        Assert.True(scheduler.OnIdle(true));
        Assert.False(scheduler.OnIdle(true));
        Assert.Equal(1, runs);
        Assert.False(scheduler.Pending);
    }

    [Fact]
    public void RequestsRaisedByTheInstallationItselfAreIgnored()
    {
        BobrovInstallScheduler? scheduler = null;
        var requestsDuringInstall = new List<bool>();
        scheduler = new BobrovInstallScheduler(() =>
        {
            Assert.True(scheduler!.Installing);
            // MENUBAR and InsertInMenuBar raise WSCURRENT synchronously.
            requestsDuringInstall.Add(scheduler.Request());
        });

        scheduler.Request();
        Assert.True(scheduler.OnIdle(true));
        Assert.Equal(new[] { false }, requestsDuringInstall);
        Assert.False(scheduler.Pending);
        Assert.False(scheduler.Installing);
        Assert.False(scheduler.OnIdle(true));
        Assert.Equal(1, scheduler.Runs);
    }

    [Fact]
    public void AFailedInstallationDoesNotBlockTheNextRequest()
    {
        var scheduler = new BobrovInstallScheduler(() => throw new InvalidOperationException("COM"));
        scheduler.Request();
        Assert.Throws<InvalidOperationException>(() => scheduler.OnIdle(true));
        Assert.False(scheduler.Installing);
        Assert.True(scheduler.Request());
        scheduler.Cancel();
        Assert.False(scheduler.OnIdle(true));
        Assert.Throws<ArgumentNullException>(() => new BobrovInstallScheduler(null!));
    }

    [Fact]
    public void WorkspaceChangesRaisedByTheMenuSettleInsteadOfLooping()
    {
        // A model of AutoCAD: every change of the menu bar raises WSCURRENT, delivered on the next Idle.
        var bar = new FakeMenuBar();
        var groups = new FakeMenuGroups(bar, "ACAD");
        var queuedWorkspaceChanges = 0;
        var menuChanges = 0;
        var scheduler = new BobrovInstallScheduler(() =>
        {
            if (BobrovMenuInstaller.IsInstalled(groups)) return;
            Assert.Null(BobrovMenuInstaller.Install(groups, bar));
            menuChanges++;
            queuedWorkspaceChanges++;
        });

        scheduler.Request();
        for (var idle = 0; idle < 100; idle++)
        {
            for (; queuedWorkspaceChanges > 0; queuedWorkspaceChanges--) scheduler.Request();
            scheduler.OnIdle(true);
        }

        Assert.Equal(1, menuChanges);
        Assert.Equal(2, scheduler.Runs);
        Assert.False(scheduler.Pending);
        Assert.Single(bar.Items);

        // A real workspace switch reloads the menu bar from the CUIX without «Bobrov»: the menu comes back once.
        bar.Items.Clear();
        groups.Groups["ACAD"].Menus.Items[0].OnMenuBar = false;
        scheduler.Request();
        for (var idle = 0; idle < 100; idle++)
        {
            for (; queuedWorkspaceChanges > 0; queuedWorkspaceChanges--) scheduler.Request();
            scheduler.OnIdle(true);
        }

        Assert.Equal(2, menuChanges);
        Assert.Equal("Bobrov", Assert.Single(bar.Items).Name);
    }

    [Fact]
    public void IsInstalledOnlyReadsTheMenu()
    {
        var bar = new FakeMenuBar();
        var groups = new FakeMenuGroups(bar, "ACAD");
        Assert.False(BobrovMenuInstaller.IsInstalled(null));
        Assert.False(BobrovMenuInstaller.IsInstalled(new object()));
        Assert.False(BobrovMenuInstaller.IsInstalled(new FakeMenuGroups(bar)));
        Assert.False(BobrovMenuInstaller.IsInstalled(groups));
        Assert.Empty(groups.Groups["ACAD"].Menus.Items);

        Assert.Null(BobrovMenuInstaller.Install(groups, bar));
        Assert.True(BobrovMenuInstaller.IsInstalled(groups));

        var menu = groups.Groups["ACAD"].Menus.Items.Single();
        menu.OnMenuBar = false;
        Assert.False(BobrovMenuInstaller.IsInstalled(groups));
        menu.OnMenuBar = true;
        menu.Items.RemoveAt(1);
        Assert.False(BobrovMenuInstaller.IsInstalled(groups));
        Assert.Single(menu.Items);
        Assert.Single(bar.Items);
    }
}
