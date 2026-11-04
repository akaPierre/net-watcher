<#
.SYNOPSIS
  Builds a release and publishes it on GitHub. The website's download buttons
  pick it up automatically (they read the latest GitHub Release).

.DESCRIPTION
  1. Runs build.ps1 (portable exe + MSI into .\dist)
  2. Creates the Git tag v<version> and a GitHub Release with both files attached

  Requires the GitHub CLI (gh), signed in:  gh auth login
  Raise <Version> in src\NetWatcher\NetWatcher.csproj before each release.

.EXAMPLE
  .\release.ps1
  .\release.ps1 -Notes "Fixes the printer alert."
#>
param([string]$Notes = "")

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI not found. Install it: winget install GitHub.cli" }
gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not signed in. Run: gh auth login" }

$csproj = Join-Path $root 'src\NetWatcher\NetWatcher.csproj'
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"

gh release view $tag *> $null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists. Raise <Version> in NetWatcher.csproj first." }

if (git -C $root status --porcelain) { throw "There are uncommitted changes. Commit them first so the release matches the code." }

& (Join-Path $root 'build.ps1') -Version $version

$files = Get-ChildItem (Join-Path $root 'dist') -Include *.msi, *.exe -File -Recurse | ForEach-Object FullName
if (-not $Notes) {
    $Notes = @"
**Installer** (recommended): ``NetWatcher-Setup-$version.msi``. Installs per user, no admin rights needed; upgrades older versions in place.
**Portable**: ``NetWatcher-$version-portable.exe``. Single file, no install.

Windows 10/11, 64-bit. The files are not code-signed: if SmartScreen warns, click *More info -> Run anyway*.
"@
}

git -C $root push origin HEAD
gh release create $tag @files --title "Net Watcher $version" --notes $Notes --target (git -C $root rev-parse HEAD)
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
Write-Host "`nPublished $tag. The website shows it within a minute." -ForegroundColor Green
