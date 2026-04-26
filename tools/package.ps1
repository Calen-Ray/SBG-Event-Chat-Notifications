#
# Packages this mod into a Thunderstore-ready zip.
#
# - Reads manifest.json for name + version (canonical source).
# - Builds in Release (skips r2modman auto-deploy).
# - Stages: manifest.json, icon.png, README.md, CHANGELOG.md, DLL.
# - Zips with files at the root of the archive.
# - Output: artifacts/<Team>-<Name>-<Version>.zip
#
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Team          = "Cray"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$manifestPath = Join-Path $RepoRoot "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "manifest.json not found at $manifestPath" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

$name    = $manifest.name
$version = $manifest.version_number
if ([string]::IsNullOrWhiteSpace($name))    { throw "manifest.json 'name' is empty" }
if ([string]::IsNullOrWhiteSpace($version)) { throw "manifest.json 'version_number' is empty" }

if ($name -notmatch '^[a-zA-Z0-9_]+$') { throw "manifest.json 'name' may only contain a-z A-Z 0-9 _ (got '$name')" }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "manifest.json 'version_number' must be Major.Minor.Patch (got '$version')" }

Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $RepoRoot "icon.png"
if (-not (Test-Path $iconPath)) { throw "icon.png missing" }
$img = [System.Drawing.Image]::FromFile($iconPath)
try {
    if ($img.Width -ne 256 -or $img.Height -ne 256) {
        throw "icon.png must be exactly 256x256 (got $($img.Width)x$($img.Height))"
    }
} finally { $img.Dispose() }

if (-not (Test-Path (Join-Path $RepoRoot "README.md"))) { throw "README.md missing" }

Write-Host "==> Building $name v$version ($Configuration)"
& dotnet build -c $Configuration -p:SkipDeploy=true /v:minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$dllPath = Join-Path $RepoRoot "bin/$Configuration/$name.dll"
if (-not (Test-Path $dllPath)) { throw "built DLL not found at $dllPath" }

$stage = Join-Path $RepoRoot ".pkg-stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item $manifestPath                     (Join-Path $stage "manifest.json")
Copy-Item $iconPath                         (Join-Path $stage "icon.png")
Copy-Item (Join-Path $RepoRoot "README.md") (Join-Path $stage "README.md")
if (Test-Path (Join-Path $RepoRoot "CHANGELOG.md")) {
    Copy-Item (Join-Path $RepoRoot "CHANGELOG.md") (Join-Path $stage "CHANGELOG.md")
}
Copy-Item $dllPath (Join-Path $stage "$name.dll")

$artifacts = Join-Path $RepoRoot "artifacts"
if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }
$zipPath = Join-Path $artifacts "$Team-$name-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Push-Location $stage
try {
    Compress-Archive -Path (Get-ChildItem).FullName -DestinationPath $zipPath -CompressionLevel Optimal
} finally { Pop-Location }

Remove-Item -Recurse -Force $stage

Write-Host "==> Packaged: $zipPath" -ForegroundColor Green
