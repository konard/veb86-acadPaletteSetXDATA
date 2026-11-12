param(
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [ValidatePattern('^v0\.0\.[0-9]+$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $CommitSha,
    [Parameter(Mandatory)] [string] $ZipPath,
    [Parameter(Mandatory)] [string] $NotesPath,
    # Issue #39: acadPaletteSetXDATA-manual-install-<Version>.zip is published next to acadPaletteSetXDATA.zip.
    [string] $ManualInstallZipPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$assetPaths = @($ZipPath)
if ($ManualInstallZipPath) { $assetPaths += $ManualInstallZipPath }
foreach ($path in $assetPaths + $NotesPath) {
    if (-not (Test-Path $path -PathType Leaf)) { throw 'The validated ZIP files and release notes must exist before creating a release.' }
}
$expected = @{ (Split-Path $ZipPath -Leaf) = (Get-Item $ZipPath).Length }
if ((Split-Path $ZipPath -Leaf) -cne 'acadPaletteSetXDATA.zip') { throw 'The DLL archive must be named acadPaletteSetXDATA.zip.' }
if ($ManualInstallZipPath) {
    $manualName = "acadPaletteSetXDATA-manual-install-$Version.zip"
    if ((Split-Path $ManualInstallZipPath -Leaf) -cne $manualName) { throw "The manual install archive must be named $manualName." }
    $expected[$manualName] = (Get-Item $ManualInstallZipPath).Length
}
$assetNames = ($expected.Keys | Sort-Object) -join ', '

$existingJson = & gh release view $Version --repo $Repository --json isDraft,targetCommitish,assets 2>$null
if ($LASTEXITCODE -eq 0) {
    $existing = $existingJson | ConvertFrom-Json
    if ($existing.targetCommitish -ne $CommitSha) { throw "$Version already belongs to a different commit." }
    if (-not $existing.isDraft) {
        $assets = @($existing.assets)
        $complete = $assets.Count -eq $expected.Count
        foreach ($asset in $assets) {
            $match = $expected.Keys | Where-Object { $_ -ceq $asset.name }
            if (-not $match -or $asset.size -ne $expected[$match]) { $complete = $false }
        }
        if (-not $complete) { throw "$Version is published but its release assets are incomplete or unexpected." }
        Write-Host "$Version is already published for $CommitSha."
        return
    }
    # Resume a draft left by a failed upload on an earlier attempt of the same run.
    & gh release upload $Version @assetPaths --repo $Repository --clobber
    if ($LASTEXITCODE -ne 0) { throw "Failed to upload $assetNames to draft $Version. Nothing was published." }
} else {
    # gh uploads the assets while the release is still a draft. An upload failure
    # leaves an unpublished draft, which the next attempt can resume.
    & gh release create $Version @assetPaths --repo $Repository --target $CommitSha --title $Version --notes-file $NotesPath --draft
    if ($LASTEXITCODE -ne 0) { throw "Failed to create/upload draft $Version. Nothing was published." }
}

& gh release edit $Version --repo $Repository --title $Version --notes-file $NotesPath --draft=false
if ($LASTEXITCODE -ne 0) { throw "Failed to publish completed draft $Version; rerun this workflow to retry." }
