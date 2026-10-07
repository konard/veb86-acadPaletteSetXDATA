param(
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [ValidatePattern('^v0\.0\.[0-9]+$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $CommitSha,
    [Parameter(Mandatory)] [string] $ZipPath,
    [Parameter(Mandatory)] [string] $NotesPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
if (-not (Test-Path $ZipPath -PathType Leaf) -or -not (Test-Path $NotesPath -PathType Leaf)) {
    throw 'The validated ZIP and release notes must exist before creating a release.'
}

$existingJson = & gh release view $Version --repo $Repository --json isDraft,targetCommitish,assets 2>$null
if ($LASTEXITCODE -eq 0) {
    $existing = $existingJson | ConvertFrom-Json
    if ($existing.targetCommitish -ne $CommitSha) { throw "$Version already belongs to a different commit." }
    if (-not $existing.isDraft) {
        $assets = @($existing.assets)
        if ($assets.Count -ne 1 -or $assets[0].name -ne 'AutoCADHttp.zip' -or $assets[0].size -ne (Get-Item $ZipPath).Length) {
            throw "$Version is published but its release assets are incomplete or unexpected."
        }
        Write-Host "$Version is already published for $CommitSha."
        return
    }
    # Resume a draft left by a failed upload on an earlier attempt of the same run.
    & gh release upload $Version $ZipPath --repo $Repository --clobber
    if ($LASTEXITCODE -ne 0) { throw "Failed to upload AutoCADHttp.zip to draft $Version. Nothing was published." }
} else {
    # gh uploads the asset while the release is still a draft. An upload failure
    # leaves an unpublished draft, which the next attempt can resume.
    & gh release create $Version $ZipPath --repo $Repository --target $CommitSha --title $Version --notes-file $NotesPath --draft
    if ($LASTEXITCODE -ne 0) { throw "Failed to create/upload draft $Version. Nothing was published." }
}

& gh release edit $Version --repo $Repository --title $Version --notes-file $NotesPath --draft=false
if ($LASTEXITCODE -ne 0) { throw "Failed to publish completed draft $Version; rerun this workflow to retry." }
