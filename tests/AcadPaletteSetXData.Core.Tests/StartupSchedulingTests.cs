using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

// Issue #47: with the bundle loading the plugin at AutoCAD startup, «Выберите объекты» flickered in the command
// line and the startup drawing did not open until the prompt was cancelled. The startup work ran on the first Idle,
// also while the drawing was being opened or a command waited for input. Since issue #53 the only startup work is
// the load message; the «Bobrov» menu and ribbon tab and their WSCURRENT scheduler were removed.
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
}
