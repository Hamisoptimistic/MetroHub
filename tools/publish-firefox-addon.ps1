<#
.SYNOPSIS
    Publishes and signs the MetroHub Firefox extension on Mozilla Add-ons (AMO).

.DESCRIPTION
    Automates package validation, release metadata generation, version synchronization,
    and submission to Mozilla's AMO signing service via web-ext.

.PARAMETER Version
    Optional target version string (e.g. "1.0.2"). If omitted, uses the version in manifest.json.

.PARAMETER ReleaseNotes
    Changelog description sent to Mozilla for this release.

.PARAMETER Channel
    Distribution channel: "listed" (public AMO store listing) or "unlisted" (self-hosted xpi). Default is "listed".

.EXAMPLE
    .\tools\publish-firefox-addon.ps1 -Version "1.0.2" -ReleaseNotes "Updated extension branding to MetroHub."
#>

[CmdletBinding()]
param (
    [string]$Version,
    [string]$ReleaseNotes = "Performance improvements and UI updates.",
    [string]$Channel = "listed",
    [string]$ApiKey,
    [string]$ApiSecret
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $repoRoot "extensions\metrohub-companion-firefox"
$manifestPath = Join-Path $sourceDir "manifest.json"
$artifactsDir = Join-Path $repoRoot "extensions\signed"
$metadataPath = Join-Path $repoRoot "extensions\amo-metadata.json"
$credentialsPath = Join-Path $repoRoot "extensions\.amo-credentials.json"

# 1. Resolve API Credentials
if (-not $ApiKey -or -not $ApiSecret) {
    if ($env:AMO_API_KEY -and $env:AMO_API_SECRET) {
        $ApiKey = $env:AMO_API_KEY
        $ApiSecret = $env:AMO_API_SECRET
    }
    elseif (Test-Path $credentialsPath) {
        $creds = Get-Content $credentialsPath -Raw | ConvertFrom-Json
        $ApiKey = $creds.apiKey
        $ApiSecret = $creds.apiSecret
    }
}

if (-not $ApiKey -or -not $ApiSecret) {
    Write-Error "AMO credentials not found. Provide -ApiKey/-ApiSecret, set AMO_API_KEY/AMO_API_SECRET environment variables, or ensure extensions/.amo-credentials.json exists."
}

# 2. Check manifest
if (-not (Test-Path $manifestPath)) {
    Write-Error "Firefox manifest not found at: $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if ($Version -and $Version -ne $manifest.version) {
    Write-Host "[*] Bumping Firefox manifest version from $($manifest.version) to $Version..." -ForegroundColor Cyan
    $manifestContent = Get-Content $manifestPath -Raw
    $manifestContent = $manifestContent -replace '("version":\s*")[^"]+(")', "`$1$Version`$2"
    Set-Content -Path $manifestPath -Value $manifestContent -NoNewline
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
}

$currentVersion = $manifest.version
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " Publishing MetroHub Firefox Extension v$currentVersion" -ForegroundColor Green
Write-Host " Channel: $Channel" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

# 3. Create release metadata
$metadata = @{
    version = @{
        release_notes = @{
            "en-US" = $ReleaseNotes
        }
    }
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath -Encoding utf8

if (-not (Test-Path $artifactsDir)) {
    New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
}

try {
    Write-Host "[*] Uploading and signing extension via web-ext..." -ForegroundColor Cyan
    
    npx --yes web-ext sign `
        --source-dir "$sourceDir" `
        --artifacts-dir "$artifactsDir" `
        --channel "$Channel" `
        --amo-metadata "$metadataPath" `
        --api-key "$ApiKey" `
        --api-secret "$ApiSecret"
    
    if ($LASTEXITCODE -ne 0) {
        throw "web-ext sign exited with code $LASTEXITCODE"
    }

    Write-Host ""
    Write-Host "[+] Successfully published and signed v$currentVersion on Mozilla Add-ons!" -ForegroundColor Green
    Write-Host "[+] Artifacts stored in: $artifactsDir" -ForegroundColor Green
}
finally {
    if (Test-Path $metadataPath) {
        Remove-Item $metadataPath -Force -ErrorAction SilentlyContinue
    }
}

