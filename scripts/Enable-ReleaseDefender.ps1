# GitHub's disposable Windows images disable monitoring and can force passive mode.
# Strengthen that runner before packaging; never change a developer's AV settings.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Defender activation is restricted to disposable GitHub-hosted Windows runners.'
}
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Advanced Threat Protection'
if (Test-Path -LiteralPath $policy) {
    Set-ItemProperty -LiteralPath $policy -Name ForceDefenderPassiveMode -Value 0 -Type DWord
}
Start-Service -Name WinDefend -ErrorAction Stop
Set-MpPreference -DisableRealtimeMonitoring $false -DisableBehaviorMonitoring $false -DisableArchiveScanning $false -DisableScriptScanning $false -DisableIOAVProtection $false -ErrorAction Stop
# Remove only the two whole-drive exclusions supplied by the runner image.
$broadExclusions = @((Get-MpPreference).ExclusionPath | Where-Object { $_ -in @('C:\','D:\') })
if ($broadExclusions.Count) { Remove-MpPreference -ExclusionPath $broadExclusions -ErrorAction Stop }
Update-MpSignature -ErrorAction Stop
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    $status = Get-MpComputerStatus -ErrorAction Stop
    if ($status.AntivirusEnabled -and $status.RealTimeProtectionEnabled) {
        $status | Select-Object AMRunningMode, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureVersion
        return
    }
    Start-Sleep -Seconds 2
}
throw 'Defender activation failed; release publication remains blocked.'
