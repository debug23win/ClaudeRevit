# Claude Revit installer / updater
# Downloads the latest GitHub release zip and unpacks into the user's Revit Addins folder.
# Re-run any time to update.
#
# Usage:
#   iwr https://raw.githubusercontent.com/debug23win/ClaudeRevit/main/install.ps1 | iex
# Or to install a specific version:
#   $env:CLAUDEREVIT_VERSION = "v1.3"
#   iwr https://raw.githubusercontent.com/debug23win/ClaudeRevit/main/install.ps1 | iex

param(
    [string]$Repo = "debug23win/ClaudeRevit",
    [string]$RevitVersion = "2027",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# Allow override via env var (handy for iex pipeline)
if ([string]::IsNullOrEmpty($Version) -and -not [string]::IsNullOrEmpty($env:CLAUDEREVIT_VERSION)) {
    $Version = $env:CLAUDEREVIT_VERSION
}

Write-Host "Claude Revit installer" -ForegroundColor Cyan
Write-Host "Target: Revit $RevitVersion (repo: $Repo)" -ForegroundColor DarkGray

# Resolve release
$apiBase = "https://api.github.com/repos/$Repo"
if ([string]::IsNullOrEmpty($Version)) {
    Write-Host "Querying latest release..." -ForegroundColor DarkGray
    $release = Invoke-RestMethod "$apiBase/releases/latest"
} else {
    Write-Host "Querying release '$Version'..." -ForegroundColor DarkGray
    $release = Invoke-RestMethod "$apiBase/releases/tags/$Version"
}
# Releases from v1.25 ship one zip per Revit version (…-Revit2026.zip). Pick the one
# matching $RevitVersion; fall back to a legacy single zip for older releases.
$asset = $release.assets | Where-Object { $_.name -like "*Revit$RevitVersion*.zip" } | Select-Object -First 1
if (-not $asset) {
    $asset = $release.assets | Where-Object { $_.name -like "*.zip" -and $_.name -notmatch 'Revit20' } | Select-Object -First 1
}
if (-not $asset) {
    $available = ($release.assets | Where-Object { $_.name -like "*.zip" } | ForEach-Object { $_.name }) -join ", "
    throw "No matching .zip for Revit $RevitVersion in release '$($release.tag_name)'. Available: $available"
}

Write-Host "Found $($asset.name) ($([math]::Round($asset.size / 1KB, 1)) KB)" -ForegroundColor Green

# Download
$tmp = Join-Path $env:TEMP "ClaudeRevit-$($release.tag_name).zip"
Invoke-WebRequest $asset.browser_download_url -OutFile $tmp

# Check if Revit is running — must close before install
$revit = Get-Process -Name Revit -ErrorAction SilentlyContinue
if ($revit) {
    Write-Host ""
    Write-Host "Revit is running (PID $($revit.Id))." -ForegroundColor Yellow
    Write-Host "Close Revit completely, then press Enter to continue." -ForegroundColor Yellow
    Read-Host
}

# Install
$target = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
New-Item -ItemType Directory -Force -Path $target | Out-Null

Expand-Archive -Path $tmp -DestinationPath $target -Force
Remove-Item $tmp -Force

Write-Host ""
Write-Host "Installed Claude Revit $($release.tag_name) to:" -ForegroundColor Green
Write-Host "  $target" -ForegroundColor White
Write-Host ""

# API key hint (v1.7+: the key is stored encrypted via DPAPI, set from inside Revit)
Write-Host "API key: click the gear icon in the Claude chat pane after launching Revit." -ForegroundColor DarkGray
Write-Host "It is stored encrypted (Windows DPAPI) - no environment variable needed." -ForegroundColor DarkGray

Write-Host ""
Write-Host "Done. Launch Revit and look for the Claude tab." -ForegroundColor Cyan
