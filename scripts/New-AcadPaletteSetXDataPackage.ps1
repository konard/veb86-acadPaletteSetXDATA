param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $ZipPath,
    # Issue #39: the manual install without an installer, unpacked into %APPDATA% of the user (like TeplovPrint).
    [string] $ManualInstallZipPath,
    [ValidatePattern('^v?[0-9]+\.[0-9]+\.[0-9]+$')] [string] $Version = '0.0.0',
    [string] $CommitSha = 'local'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
if ($ManualInstallZipPath) {
    $ManualInstallZipPath = [IO.Path]::GetFullPath($ManualInstallZipPath)
    if (Test-Path $ManualInstallZipPath) { Remove-Item $ManualInstallZipPath -Force }
}
$dll = Join-Path $BuildDirectory 'acadPaletteSetXDATA.dll'
if (-not (Test-Path $dll -PathType Leaf)) { throw "Build did not produce acadPaletteSetXDATA.dll: $dll" }

$unexpected = @(Get-ChildItem $BuildDirectory -Recurse -Filter '*.dll' | Where-Object { $_.Name -cne 'acadPaletteSetXDATA.dll' })
if ($unexpected.Count -gt 0) { throw "Unexpected runtime assembly in build output: $($unexpected.Name -join ', '). AutoCAD API DLLs must not be deployed." }

$assembly = [Reflection.AssemblyName]::GetAssemblyName([IO.Path]::GetFullPath($dll))
if ($assembly.Name -cne 'acadPaletteSetXDATA') { throw 'Build output is not the acadPaletteSetXDATA managed assembly.' }
$stream = [IO.File]::OpenRead($dll)
$reader = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if (-not $reader.HasMetadata -or $reader.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::Amd64) {
        throw 'acadPaletteSetXDATA.dll must be a managed x64 PE assembly.'
    }
} finally {
    $reader.Dispose()
    $stream.Dispose()
}

New-Item ([IO.Path]::GetDirectoryName($ZipPath)) -ItemType Directory -Force | Out-Null
try {
    $archive = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $dll, 'acadPaletteSetXDATA.dll') | Out-Null
    } finally { $archive.Dispose() }
} catch {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    throw
}
Write-Host "Created $ZipPath containing only acadPaletteSetXDATA.dll."
if (-not $ManualInstallZipPath) { return }

# The archive mirrors the user profile: AppData/Roaming/Autodesk/ApplicationPlugins/acadPaletteSetXDATA.bundle,
# which AutoCAD loads at startup; install.cmd copies it into %APPDATA% without administrator rights.
$root = Split-Path $PSScriptRoot -Parent
$bundleEntry = 'AppData/Roaming/Autodesk/ApplicationPlugins/acadPaletteSetXDATA.bundle'
$manifestSource = Join-Path $root 'installer/acadPaletteSetXDATA.bundle/PackageContents.xml'
$appVersion = $Version.TrimStart('v')
$package = [xml]::new()
$package.PreserveWhitespace = $true
$package.Load($manifestSource)
$package.ApplicationPackage.SetAttribute('AppVersion', $appVersion)
$components = @($package.ApplicationPackage.Components.ComponentEntry)
if ($components.Count -ne 1) { throw 'PackageContents.xml must have exactly one ComponentEntry.' }
$module = $components[0].ModuleName
if ($module -notmatch '^\./Contents/[^/]+/acadPaletteSetXDATA\.dll$') { throw "Unexpected ModuleName in PackageContents.xml: $module" }

$files = [ordered]@{
    "$bundleEntry/$($module.Substring(2))" = [IO.Path]::GetFullPath($dll)
    'INSTALL.md' = Join-Path $root 'installer/manual/INSTALL.md'
    'install.cmd' = Join-Path $root 'installer/manual/install.cmd'
    'uninstall.cmd' = Join-Path $root 'installer/manual/uninstall.cmd'
    'LICENSE' = Join-Path $root 'LICENSE'
}
foreach ($file in $files.Values) { if (-not (Test-Path $file -PathType Leaf)) { throw "Manual install file is missing: $file" } }

$staging = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item $staging -ItemType Directory | Out-Null
try {
    $packageContents = Join-Path $staging 'PackageContents.xml'
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $writer = [Xml.XmlWriter]::Create($packageContents, $settings)
    try { $package.Save($writer) } finally { $writer.Dispose() }
    $files.Insert(0, "$bundleEntry/PackageContents.xml", $packageContents)

    $manifest = [Collections.Generic.List[string]]::new()
    $manifest.Add("acadPaletteSetXDATA manual install v$appVersion")
    $manifest.Add("Commit SHA: $CommitSha")
    $manifest.Add('Target: AutoCAD 2021 x64 / .NET Framework 4.8 (PackageContents SeriesMin R24.0, SeriesMax R24.3)')
    $manifest.Add('Install path: %APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle')
    $manifest.Add('')
    $manifest.Add('SHA-256:')
    foreach ($entry in $files.Keys) { $manifest.Add("$((Get-FileHash $files[$entry] -Algorithm SHA256).Hash.ToLowerInvariant())  $entry") }
    $releaseManifest = Join-Path $staging 'release-manifest.txt'
    [IO.File]::WriteAllLines($releaseManifest, $manifest, [Text.UTF8Encoding]::new($false))
    $files['release-manifest.txt'] = $releaseManifest

    New-Item ([IO.Path]::GetDirectoryName($ManualInstallZipPath)) -ItemType Directory -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::Open($ManualInstallZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.Keys) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $files[$entry], $entry) | Out-Null
        }
    } finally { $archive.Dispose() }
} catch {
    if (Test-Path $ManualInstallZipPath) { Remove-Item $ManualInstallZipPath -Force }
    throw
} finally {
    Remove-Item $staging -Recurse -Force
}
Write-Host "Created $ManualInstallZipPath with $bundleEntry for %APPDATA% (AppVersion $appVersion)."
