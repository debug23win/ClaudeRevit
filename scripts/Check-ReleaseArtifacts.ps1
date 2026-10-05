param(
    [Parameter(Mandatory)][string[]]$Paths,
    [Parameter(Mandatory)][string]$ReportPath,
    [string]$SourceCommit = ''
)

# Fail closed. A successful scan is evidence from one engine at one time, not a
# guarantee of safety. Do not add exclusions, restore quarantine or disable AV.
$ErrorActionPreference = 'Stop'
$report = [ordered]@{ source_commit = $SourceCommit; utc = [DateTime]::UtcNow.ToString('o'); status = 'incomplete'; protection = $null; engine = $null; files = @(); error = $null }
try {
    $status = Get-MpComputerStatus -ErrorAction Stop
    $report.protection = @{ mode = $status.AMRunningMode; antivirus_enabled = [bool]$status.AntivirusEnabled; realtime_enabled = [bool]$status.RealTimeProtectionEnabled }
    if (-not $status.AntivirusEnabled -or -not $status.RealTimeProtectionEnabled) {
        throw 'Microsoft Defender must be active; release scanning cannot be skipped.'
    }
    if ($status.AntivirusSignatureLastUpdated -lt (Get-Date).AddDays(-2)) {
        throw 'Defender intelligence is more than two days old. Update it before release scanning.'
    }
    $report.engine = @{ product = $status.AMProductVersion; intelligence = $status.AntivirusSignatureVersion; intelligence_updated = $status.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o') }
    $platform = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    $scanner = Get-ChildItem -LiteralPath $platform -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $scanner) { $scanner = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe' }
    if (-not (Test-Path -LiteralPath $scanner -PathType Leaf)) { throw 'Defender scanner is unavailable.' }
    foreach ($path in $Paths) {
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        # This custom-scan option ignores exclusions, scans archives and reports
        # detections without remediation. It does not disable real-time protection.
        $scanText = (& $scanner -Scan -ScanType 3 -File $item.FullName -DisableRemediation 2>&1 | Out-String)
        $scanExit = $LASTEXITCODE
        $fileReport = [ordered]@{ name = $item.Name; scan_exit = $scanExit; scan_output = $scanText.Trim() }
        $report.files += $fileReport
        if ($scanExit -ne 0) { throw "Defender scan failed or detected a threat in $($item.Name) (exit $scanExit)." }
        # Read the original only after scanning; a quarantined/unreadable file also
        # fails the gate. Never substitute a rebuilt or renamed file for analysis.
        if (-not $item.PSIsContainer) {
            $fileReport.size = $item.Length
            $fileReport.sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($item.Extension -in @('.exe', '.dll', '.ps1')) {
                $signature = Get-AuthenticodeSignature -LiteralPath $item.FullName
                $fileReport.signature = [string]$signature.Status
                $fileReport.signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
                if ($signature.Status -in @('HashMismatch', 'NotTrusted', 'UnknownError')) { throw "Invalid or unverifiable signature in $($item.Name)." }
            }
        }
    }
    $report.status = 'passed'
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    $reportDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
