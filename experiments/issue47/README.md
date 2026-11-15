# Issue #47: «Выберите объекты» while AutoCAD opens the startup drawing

AutoCAD itself is not available in CI, so the issue is checked with models of the startup in the unit tests:

- `tests/AcadPaletteSetXData.Core.Tests/StartupSchedulingTests.cs`: the readiness gate
  (`StartupReadiness`), the scheduler that ignores its own `WSCURRENT` (`BobrovInstallScheduler`),
  and a model of AutoCAD where every menu bar change raises `WSCURRENT` on the next Idle.
- `tests/AcadPaletteSetXData.AutoCAD.Tests/PaletteApplicationTests.cs`: the production
  `PaletteApplication.cs` and `AutoCadStartup.cs` compiled against stubs. The load message waits while
  the drawing opens (`DocumentCreateStarted`), while `Application.IsQuiescent`/`Editor.IsQuiescent`
  are false and while `CommandInProgress` is set.

## The tests fail without the fix

The old behaviour was restored temporarily in three places, and the tests were run with
`DOTNET_ROLL_FORWARD=Major dotnet test tests/<project>`. The three places:

- `AutoCadStartup.IsReady` always returned true (no readiness gate).
- `BobrovInstallScheduler.Request` also accepted requests during the installation.
- `BobrovMenuInstaller.IsInstalled` always returned false (a reinstall on every `WSCURRENT`).

Results:

```text
Core.Tests:    Failed: 3, Passed: 412
  StartupSchedulingTests.IsInstalledOnlyReadsTheMenu
  StartupSchedulingTests.RequestsRaisedByTheInstallationItselfAreIgnored
  StartupSchedulingTests.WorkspaceChangesRaisedByTheMenuSettleInsteadOfLooping
AutoCAD.Tests: Failed: 2, Passed: 7
  PaletteApplicationTests.LoadingDuringTheStartupDrawingOpenWaitsForAQuietIdle
  PaletteApplicationTests.ACanceledDrawingOpenDoesNotBlockTheStartup
```

With the fix: Core.Tests 415 passed, AutoCAD.Tests 9 passed.

## Tracing in AutoCAD

Set the user environment variable `XDATAPALETTE_TRACE=1` and start AutoCAD by opening a drawing. The
decisions of the startup are written to `%TEMP%\acadPaletteSetXDATA-trace.log` and to the debugger
output (DebugView). Tracing is off by default and never writes to the command line.
