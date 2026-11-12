# Issue #39: runs install.cmd/uninstall.cmd of the manual install archive (Windows only) against a
# temporary %APPDATA%, so the real profile of the build agent is never touched.
param(
    [Parameter(Mandatory)] [string] $ManualInstallZipPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'install.cmd and uninstall.cmd need cmd.exe and robocopy (Windows).' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
$script:checks = 0
function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Invoke-Cmd([string] $Script, [string] $AppData) {
    $saved = $env:APPDATA
    try {
        $env:APPDATA = $AppData
        # < nul answers the final «pause» of the scripts.
        $output = & cmd.exe /d /c "`"$Script`" < nul" 2>&1 | Out-String
        $code = $LASTEXITCODE
    } finally { $env:APPDATA = $saved }
    Write-Host $output
    return @{ Code = $code; Output = $output }
}

try {
    $unpacked = Join-Path $temporary 'unpacked dir'  # a space in the path must not break the scripts
    $appData = Join-Path $temporary 'Roaming'
    New-Item $appData -ItemType Directory -Force | Out-Null
    Expand-Archive $ManualInstallZipPath $unpacked
    $bundle = Join-Path $appData 'Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle'
    $dll = Join-Path $bundle 'Contents\AutoCAD2021-2024\acadPaletteSetXDATA.dll'
    $sourceDll = Join-Path $unpacked 'AppData\Roaming\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle\Contents\AutoCAD2021-2024\acadPaletteSetXDATA.dll'

    # Another plugin in ApplicationPlugins must survive install and uninstall.
    $neighbour = Join-Path $appData 'Autodesk\ApplicationPlugins\Other.bundle\PackageContents.xml'
    New-Item (Split-Path $neighbour) -ItemType Directory -Force | Out-Null
    Set-Content $neighbour '<ApplicationPackage />'

    $result = Invoke-Cmd (Join-Path $unpacked 'install.cmd') $appData
    Assert ($result.Code -eq 0) "install.cmd failed with exit code $($result.Code)."
    Assert (Test-Path (Join-Path $bundle 'PackageContents.xml')) 'install.cmd must copy PackageContents.xml into %APPDATA%.'
    Assert ((Get-FileHash $dll).Hash -eq (Get-FileHash $sourceDll).Hash) 'install.cmd must copy the DLL into %APPDATA%.'
    Assert ($null -eq (Get-Item $dll -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'install.cmd must unblock the installed files.'

    # Reinstall over an older version: stale files of the bundle are removed.
    $stale = Join-Path $bundle 'Contents\stale.txt'
    Set-Content $stale 'old version'
    Set-Content $dll 'old version' -Stream Zone.Identifier
    $result = Invoke-Cmd (Join-Path $unpacked 'install.cmd') $appData
    Assert ($result.Code -eq 0) 'Reinstalling must succeed.'
    Assert (-not (Test-Path $stale)) 'Reinstalling must remove the files of the previous version.'
    Assert ($null -eq (Get-Item $dll -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'Reinstalling must unblock the DLL.'
    Assert (Test-Path $neighbour) 'install.cmd must not touch other plugins.'

    $result = Invoke-Cmd (Join-Path $unpacked 'uninstall.cmd') $appData
    Assert ($result.Code -eq 0) "uninstall.cmd failed with exit code $($result.Code)."
    Assert (-not (Test-Path $bundle)) 'uninstall.cmd must remove the bundle.'
    Assert (Test-Path $neighbour) 'uninstall.cmd must not touch other plugins.'
    $result = Invoke-Cmd (Join-Path $unpacked 'uninstall.cmd') $appData
    Assert ($result.Code -eq 0) 'uninstall.cmd must succeed when nothing is installed.'

    # install.cmd started outside the unpacked archive reports the problem instead of copying nothing.
    $lonely = Join-Path $temporary 'lonely'
    New-Item $lonely -ItemType Directory | Out-Null
    Copy-Item (Join-Path $unpacked 'install.cmd') $lonely
    $result = Invoke-Cmd (Join-Path $lonely 'install.cmd') $appData
    Assert ($result.Code -eq 1 -and -not (Test-Path $bundle)) 'install.cmd without the AppData folder must fail.'

    $global:LASTEXITCODE = 0
    Write-Host "Passed $script:checks manual install checks."
} finally {
    if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force }
}
