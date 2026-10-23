# Publishes a new WuxAuto version on GitHub; every copy of the app offers it on its next start.
#
#   .\release.ps1 0.3.0 "Removed the 15 min boss limit"
#
# Builds the zip with that version stamped in (publish.ps1) and creates the GitHub release
# v<Version> on the current commit with the zip attached. The release is built from
# the current commit of main, which must be committed and pushed - the tag then matches the code.
# Needs the GitHub CLI (gh), logged in with access to the repository.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Notes
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed ($LASTEXITCODE)" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
$tag = "v$Version"

# --- the commit to release: clean, on main, same as GitHub
if (git status --porcelain) { throw 'There are uncommitted changes. Commit (and merge to main) first.' }
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "Releases are made from main (you are on '$branch')." }
Run git @('fetch', 'origin', 'main', '--tags', '--quiet')
$local = (git rev-parse HEAD).Trim()
$remote = (git rev-parse origin/main).Trim()
if ($local -ne $remote) { throw 'Local main differs from GitHub. Pull or push first.' }
if (git tag --list $tag) { throw "Tag $tag already exists." }

# --- must be newer than the latest release, or nobody would be offered it
# (no release yet = gh fails; Windows PowerShell 5 turns its stderr into an error, hence the try)
$latest = $null
try { $latest = gh release view --json tagName --jq .tagName 2>$null } catch { }
if ($LASTEXITCODE -eq 0 -and $latest) {
    if ([version]$Version -le [version]($latest.TrimStart('v'))) { throw "$tag is not newer than the latest release $latest." }
}

# --- build, tag, release
& (Join-Path $root 'publish.ps1') -Version $Version
$zip = Join-Path $root 'dist\WuxAuto-win-x64.zip'

# gh creates the tag on GitHub at this commit together with the release (one step: a failed
# release leaves no stray tag behind), then the tag is fetched back.
Run gh @('release', 'create', $tag, $zip, '--target', $local, '--title', "WuxAuto $Version", '--notes', $Notes)
Run git @('fetch', 'origin', '--tags', '--quiet')

Write-Host "Released $tag - copies of WuxAuto will offer it on their next start."
