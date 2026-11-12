param(
    [string] $BuildDirectory = "$PSScriptRoot/../src/acadPaletteSetXDATA/bin/Release/net48"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
$script:checks = 0

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Assert-Failure([scriptblock] $Action, [string] $Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert ($null -ne $failure -and $failure -match $Pattern) "Expected failure matching '$Pattern'; got '$failure'."
}

try {
    Assert (Test-Path "$root/.github/workflows/build-release.yml") 'The automatic release workflow is missing.'
    Assert (Test-Path "$root/src/acadPaletteSetXDATA/acadPaletteSetXDATA.csproj") 'The required acadPaletteSetXDATA build target is missing.'
    & dotnet run --project "$root/experiments/AcadPaletteSetXData.RegistrationSmoke" -c Release -- "$BuildDirectory/acadPaletteSetXDATA.dll"
    Assert ($LASTEXITCODE -eq 0) 'The shipped DLL must register XDATAPALETTE and VELEC_SCHEMA_* commands and contain the complete plugin.'
    New-Item $temporary -ItemType Directory | Out-Null
    $inputDirectory = Join-Path $temporary 'input'
    New-Item $inputDirectory -ItemType Directory | Out-Null
    $zip = Join-Path $temporary 'acadPaletteSetXDATA.zip'
    $package = "$root/scripts/New-AcadPaletteSetXDataPackage.ps1"

    Assert-Failure { & $package -BuildDirectory $inputDirectory -ZipPath $zip } 'acadPaletteSetXDATA.dll'
    Assert (-not (Test-Path $zip)) 'Missing DLL must not produce a ZIP.'
    Set-Content "$inputDirectory/acadPaletteSetXDATA.dll" 'incomplete build'
    Assert-Failure { & $package -BuildDirectory $inputDirectory -ZipPath $zip } 'managed|assembly|image|PE'
    Assert (-not (Test-Path $zip)) 'Invalid DLL must not produce a ZIP.'

    Copy-Item "$BuildDirectory/acadPaletteSetXDATA.dll" "$inputDirectory/acadPaletteSetXDATA.dll" -Force
    $assembly = [Reflection.AssemblyName]::GetAssemblyName([IO.Path]::GetFullPath("$inputDirectory/acadPaletteSetXDATA.dll"))
    Assert ($assembly.Name -ceq 'acadPaletteSetXDATA') 'Build must have the exact acadPaletteSetXDATA assembly identity, not just the DLL filename.'
    Copy-Item "$PSHOME/System.Management.Automation.dll" "$inputDirectory/acadPaletteSetXDATA.dll" -Force
    Assert-Failure { & $package -BuildDirectory $inputDirectory -ZipPath $zip } 'acadPaletteSetXDATA managed assembly'
    Assert (-not (Test-Path $zip)) 'A renamed assembly with the wrong identity must not produce a ZIP.'
    Copy-Item "$BuildDirectory/acadPaletteSetXDATA.dll" "$inputDirectory/acadPaletteSetXDATA.dll" -Force
    $dllBytes = [IO.File]::ReadAllBytes("$inputDirectory/acadPaletteSetXDATA.dll")
    Assert ([Text.Encoding]::UTF8.GetString($dllBytes).Contains('.NETFramework,Version=v4.8')) 'Build must target .NET Framework 4.8.'
    $peOffset = [BitConverter]::ToInt32($dllBytes, 0x3c)
    Assert ([BitConverter]::ToUInt16($dllBytes, $peOffset + 4) -eq 0x8664) 'Build must target x64.'
    $dllBytes[$peOffset + 4] = 0x4c
    $dllBytes[$peOffset + 5] = 0x01
    [IO.File]::WriteAllBytes("$inputDirectory/acadPaletteSetXDATA.dll", $dllBytes)
    Assert-Failure { & $package -BuildDirectory $inputDirectory -ZipPath $zip } 'x64|image|PE|format|assembly'
    Assert (-not (Test-Path $zip)) 'An assembly with the wrong architecture must not produce a ZIP.'
    Copy-Item "$BuildDirectory/acadPaletteSetXDATA.dll" "$inputDirectory/acadPaletteSetXDATA.dll" -Force
    foreach ($sdk in @('AcMgd.dll', 'AcDbMgd.dll', 'AcCoreMgd.dll')) {
        Set-Content "$inputDirectory/$sdk" 'SDK must not ship'
        Assert-Failure { & $package -BuildDirectory $inputDirectory -ZipPath $zip } 'Unexpected runtime assembly'
        Remove-Item "$inputDirectory/$sdk"
    }
    & $package -BuildDirectory $inputDirectory -ZipPath $zip
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        Assert ($archive.Entries.Count -eq 1 -and $archive.Entries[0].FullName -ceq 'acadPaletteSetXDATA.dll') 'ZIP must contain only acadPaletteSetXDATA.dll at its root.'
        $extracted = Join-Path $temporary 'extracted'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
        Assert ((Get-FileHash "$extracted/acadPaletteSetXDATA.dll").Hash -eq (Get-FileHash "$inputDirectory/acadPaletteSetXDATA.dll").Hash) 'ZIP DLL differs from the build output.'
    } finally { $archive.Dispose() }

    # Issue #39: the manual install archive mirrors %USERPROFILE% (TeplovPrint style) and needs no installer.
    $manualZip = Join-Path $temporary 'acadPaletteSetXDATA-manual-install-v0.0.1.zip'
    $sha = '0123456789abcdef0123456789abcdef01234567'
    $bundle = 'AppData/Roaming/Autodesk/ApplicationPlugins/acadPaletteSetXDATA.bundle'
    & $package -BuildDirectory $inputDirectory -ZipPath $zip -ManualInstallZipPath $manualZip -Version v0.0.1 -CommitSha $sha
    $manualEntries = @(
        "$bundle/PackageContents.xml",
        "$bundle/Contents/AutoCAD2021-2024/acadPaletteSetXDATA.dll",
        'INSTALL.md', 'install.cmd', 'uninstall.cmd', 'LICENSE', 'release-manifest.txt')
    $archive = [IO.Compression.ZipFile]::OpenRead($manualZip)
    try {
        Assert ((@($archive.Entries.FullName) -join '|') -ceq ($manualEntries -join '|')) "Manual install ZIP layout is unexpected: $(@($archive.Entries.FullName) -join ', ')."
        $manualDirectory = Join-Path $temporary 'manual'
        [IO.Compression.ZipFile]::ExtractToDirectory($manualZip, $manualDirectory)
    } finally { $archive.Dispose() }
    Assert ((Get-FileHash "$manualDirectory/$bundle/Contents/AutoCAD2021-2024/acadPaletteSetXDATA.dll").Hash -eq (Get-FileHash "$inputDirectory/acadPaletteSetXDATA.dll").Hash) 'Manual install DLL differs from the build output.'
    [xml] $contents = Get-Content "$manualDirectory/$bundle/PackageContents.xml" -Raw -Encoding utf8
    $packageNode = $contents.ApplicationPackage
    Assert ($packageNode.AppVersion -ceq '0.0.1') 'PackageContents.xml must carry the release version.'
    Assert ($packageNode.ProductType -ceq 'Application' -and $packageNode.AutodeskProduct -ceq 'AutoCAD') 'PackageContents.xml must describe an AutoCAD application bundle.'
    $entry = $packageNode.Components.ComponentEntry
    $runtime = $entry.RuntimeRequirements
    Assert ($runtime.OS -ceq 'Win64' -and $runtime.SeriesMin -ceq 'R24.0' -and $runtime.SeriesMax -ceq 'R24.3') 'The bundle must target AutoCAD 2021-2024 x64 (R24.x, .NET Framework 4.8).'
    Assert ($entry.LoadOnAutoCADStartup -ceq 'True' -and $entry.AppType -ceq '.Net') 'AutoCAD must load the .NET module at startup.'
    Assert (Test-Path (Join-Path "$manualDirectory/$bundle" $entry.ModuleName) -PathType Leaf) "ModuleName $($entry.ModuleName) must point to the DLL inside the bundle."
    $commands = @($entry.Commands.Command | ForEach-Object { $_.Global })
    foreach ($command in @('XDATAPALETTE', 'XDATAPALETTE_ON', 'XDATAPALETTE_OFF')) { Assert ($commands -ccontains $command) "PackageContents.xml must declare $command." }
    foreach ($script in @('install.cmd', 'uninstall.cmd')) {
        $text = [IO.File]::ReadAllText("$manualDirectory/$script")
        Assert ($text.Contains("`r`n") -and -not ($text -replace "`r`n", '').Contains("`n")) "$script must use CRLF line endings for cmd.exe."
        Assert ($text.Contains('%APPDATA%\Autodesk\ApplicationPlugins') -and $text.Contains('acadPaletteSetXDATA.bundle')) "$script must target %APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle."
    }
    $releaseManifest = Get-Content "$manualDirectory/release-manifest.txt" -Raw
    Assert ($releaseManifest.Contains('v0.0.1') -and $releaseManifest.Contains($sha)) 'release-manifest.txt must name the version and commit.'
    foreach ($name in $manualEntries | Where-Object { $_ -ne 'release-manifest.txt' }) {
        $hash = (Get-FileHash "$manualDirectory/$name" -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert ($releaseManifest.Contains("$hash  $name")) "release-manifest.txt must contain the SHA-256 of $name."
    }
    Assert-Failure { & $package -BuildDirectory $inputDirectory -ZipPath $zip -ManualInstallZipPath $manualZip -Version 'latest' } 'Version|validation|pattern'
    Set-Content "$inputDirectory/AcMgd.dll" 'SDK must not ship'
    Assert-Failure { & $package -BuildDirectory $inputDirectory -ZipPath $zip -ManualInstallZipPath $manualZip -Version v0.0.1 } 'Unexpected runtime assembly'
    Assert (-not (Test-Path $manualZip)) 'A rejected build must not leave a manual install ZIP.'
    Remove-Item "$inputDirectory/AcMgd.dll"
    & $package -BuildDirectory $inputDirectory -ZipPath $zip -ManualInstallZipPath $manualZip -Version v0.0.1 -CommitSha $sha

    # Mock the GitHub CLI so publication failures are tested without creating releases.
    $mock = @{ Calls = [Collections.Generic.List[object]]::new(); Existing = $null; FailCommand = '' }
    function gh {
        $mock.Calls.Add(@($args))
        $global:LASTEXITCODE = 0
        $command = $args[1]
        if ($command -eq 'view') {
            if ($null -eq $mock.Existing) { $global:LASTEXITCODE = 1 }
            else { $mock.Existing | ConvertTo-Json -Depth 5 -Compress }
        } elseif ($command -eq $mock.FailCommand) { $global:LASTEXITCODE = 1 }
    }
    $notes = Join-Path $temporary 'release-notes.md'
    Set-Content $notes 'Release test'
    $publish = "$root/scripts/Publish-AcadPaletteSetXDataRelease.ps1"
    $parameters = @{ Repository = 'example/repository'; Version = 'v0.0.1'; CommitSha = $sha; ZipPath = $zip; NotesPath = $notes }

    & $publish @parameters
    Assert (($mock.Calls | ForEach-Object { $_[1] }) -join ',' -eq 'view,create,edit') 'Release must be created as a draft and then published.'
    Assert ($mock.Calls[1] -contains '--draft') 'Release creation must use a draft.'
    Assert ($mock.Calls[1] -contains $sha -and $mock.Calls[1] -contains $zip) 'Release must include the built ZIP and exact commit.'
    Assert ($mock.Calls[2] -contains '--draft=false') 'Final step must publish the completed draft.'

    $mock.Calls.Clear()
    $mock.FailCommand = 'create'
    Assert-Failure { & $publish @parameters } 'create|upload'
    Assert ($mock.Calls.Count -eq 2) 'Failed creation/upload must never publish a release.'

    $mock.Calls.Clear()
    $mock.Existing = @{ isDraft = $true; targetCommitish = $sha; assets = @() }
    $mock.FailCommand = 'upload'
    Assert-Failure { & $publish @parameters } 'upload'
    Assert (($mock.Calls | ForEach-Object { $_[1] }) -join ',' -eq 'view,upload') 'Failed draft upload must never publish a release.'

    $mock.Calls.Clear()
    $mock.FailCommand = ''
    & $publish @parameters
    Assert (($mock.Calls | ForEach-Object { $_[1] }) -join ',' -eq 'view,upload,edit') 'Reruns must resume incomplete drafts.'

    $mock.Calls.Clear()
    $mock.FailCommand = 'edit'
    Assert-Failure { & $publish @parameters } 'Failed to publish'
    $mock.FailCommand = ''

    $mock.Calls.Clear()
    $mock.Existing = @{ isDraft = $false; targetCommitish = $sha; assets = @(@{ name = 'acadPaletteSetXDATA.zip'; size = (Get-Item $zip).Length }) }
    & $publish @parameters
    Assert ($mock.Calls.Count -eq 1) 'Reruns must leave a completed release intact.'
    $mock.Existing.assets[0].name = 'unexpected.zip'
    Assert-Failure { & $publish @parameters } 'unexpected'
    $mock.Existing.assets[0].name = 'acadPaletteSetXDATA.zip'
    $mock.Existing.targetCommitish = 'different-commit'
    Assert-Failure { & $publish @parameters } 'different commit'
    $mock.Existing.targetCommitish = $sha
    $mock.Existing.assets = @()
    Assert-Failure { & $publish @parameters } 'incomplete'

    # Issue #39: both archives are uploaded to one draft and a published release must have exactly both.
    $mock.Calls.Clear()
    $mock.Existing = $null
    $both = $parameters.Clone()
    $both.ManualInstallZipPath = $manualZip
    & $publish @both
    Assert (($mock.Calls | ForEach-Object { $_[1] }) -join ',' -eq 'view,create,edit') 'The two-asset release must be created as a draft and then published.'
    Assert ($mock.Calls[1] -contains $zip -and $mock.Calls[1] -contains $manualZip) 'The draft must include acadPaletteSetXDATA.zip and the manual install ZIP.'
    $mock.Calls.Clear()
    $mock.Existing = @{ isDraft = $true; targetCommitish = $sha; assets = @() }
    & $publish @both
    Assert ($mock.Calls[1][1] -eq 'upload' -and $mock.Calls[1] -contains $zip -and $mock.Calls[1] -contains $manualZip) 'A resumed draft must upload both ZIP files.'
    $mock.Calls.Clear()
    $mock.Existing = @{ isDraft = $false; targetCommitish = $sha; assets = @(
        @{ name = 'acadPaletteSetXDATA.zip'; size = (Get-Item $zip).Length },
        @{ name = 'acadPaletteSetXDATA-manual-install-v0.0.1.zip'; size = (Get-Item $manualZip).Length }) }
    & $publish @both
    Assert ($mock.Calls.Count -eq 1) 'Reruns must leave a completed two-asset release intact.'
    Assert-Failure { & $publish @parameters } 'incomplete|unexpected'
    $mock.Existing.assets = @($mock.Existing.assets[0])
    Assert-Failure { & $publish @both } 'incomplete'
    $mock.Existing = $null
    $mock.Calls.Clear()
    $renamed = Join-Path $temporary 'manual.zip'
    Copy-Item $manualZip $renamed
    $wrongName = $parameters.Clone()
    $wrongName.ManualInstallZipPath = $renamed
    Assert-Failure { & $publish @wrongName } 'manual-install-v0.0.1'
    Assert ($mock.Calls.Count -eq 0) 'A misnamed manual install ZIP must not call the GitHub API.'

    $mock.Calls.Clear()
    Remove-Item $zip
    Assert-Failure { & $publish @parameters } 'must exist'
    Assert ($mock.Calls.Count -eq 0) 'Missing ZIP must not call the GitHub API.'

    # An empty local feed and isolated cache reproduce unavailable NuGet packages
    # without a network request or changes to the real project's restore output.
    $restoreProject = Join-Path $temporary 'acadPaletteSetXDATA.csproj'
    Copy-Item "$root/src/acadPaletteSetXDATA/acadPaletteSetXDATA.csproj" $restoreProject
    $emptyFeed = Join-Path $temporary 'empty-feed'
    New-Item $emptyFeed -ItemType Directory | Out-Null
    $restoreLog = Join-Path $temporary 'restore.log'
    & dotnet restore $restoreProject --source $emptyFeed --packages "$temporary/packages" *> $restoreLog
    Assert ($LASTEXITCODE -ne 0) 'Unavailable AutoCAD API packages must fail restore.'
    Assert ((Get-Content $restoreLog -Raw) -match 'AutoCAD.NET') 'Restore failure must identify the required AutoCAD API package.'

    # GitHub's pwsh wrapper propagates LASTEXITCODE. The restore above is an
    # expected failure, so it must not make a successful test step fail.
    $global:LASTEXITCODE = 0
    Write-Host "Passed $script:checks release checks."
} finally {
    if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force }
}
