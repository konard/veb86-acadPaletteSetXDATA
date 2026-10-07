param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $ZipPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
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
