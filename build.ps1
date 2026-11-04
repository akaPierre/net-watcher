<#
.SYNOPSIS
  Builds the release files into .\dist:
    NetWatcher-<version>-portable.exe   single file, runs without installing (copy it anywhere)
    NetWatcher-Setup-<version>.msi      installer: Start menu + desktop shortcuts, uninstall entry

.EXAMPLE
  .\build.ps1                 # uses <Version> from src\NetWatcher\NetWatcher.csproj
  .\build.ps1 -Version 0.3.0  # build a specific version number
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csproj = Join-Path $root 'src\NetWatcher\NetWatcher.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

$dist = Join-Path $root 'dist'
$publish = Join-Path $dist 'publish'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Write-Host "== Publishing Net Watcher $Version (self-contained, single file)" -ForegroundColor Cyan
dotnet publish $csproj -c Release -o $publish "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host "== Building installer" -ForegroundColor Cyan
dotnet build (Join-Path $root 'installer\NetWatcher.Installer.wixproj') -c Release -o $dist `
    "-p:ProductVersion=$Version" "-p:AppPublishDir=$publish"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

Copy-Item (Join-Path $publish 'NetWatcher.exe') (Join-Path $dist "NetWatcher-$Version-portable.exe")
Remove-Item $publish -Recurse -Force
Get-ChildItem $dist -Exclude *.msi, *.exe | Remove-Item -Recurse -Force  # wixpdb, cab leftovers

# ---- Website downloads: copy the files next to the site and describe them in release.json ----
$downloads = Join-Path $root 'website\downloads'
if (Test-Path $downloads) { Remove-Item $downloads -Recurse -Force }
New-Item -ItemType Directory $downloads | Out-Null
$files = foreach ($f in Get-ChildItem $dist) {
    Copy-Item $f.FullName $downloads
    [ordered]@{
        name   = $f.Name
        kind   = if ($f.Extension -eq '.msi') { 'installer' } else { 'portable' }
        bytes  = $f.Length
        sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$release = [ordered]@{
    version = $Version
    date    = (Get-Date).ToString('yyyy-MM-dd')
    files   = @($files)
}
[IO.File]::WriteAllText((Join-Path $downloads 'release.json'), ($release | ConvertTo-Json -Depth 4),
    (New-Object Text.UTF8Encoding $false))  # no BOM, so any web host serves clean JSON

Write-Host "`n== Done" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
Write-Host "Website downloads updated in website\downloads"
