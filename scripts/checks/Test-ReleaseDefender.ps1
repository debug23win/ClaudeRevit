# Isolated contract checks: mocks never change AV, services, registry or network.
$ErrorActionPreference = 'Stop'
$activationScript = Join-Path $PSScriptRoot '..\Enable-ReleaseDefender.ps1'
$original = @{}
foreach ($name in @('GITHUB_ACTIONS','RUNNER_OS','RUNNER_ENVIRONMENT')) { $original[$name] = [Environment]::GetEnvironmentVariable($name) }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
try {
    $env:GITHUB_ACTIONS = 'false'
    try { & $activationScript; throw 'Local invocation was allowed' }
    catch { Assert ($_.Exception.Message -like '*restricted to disposable*') 'Local guard failed' }
    $qaMock=@{}
    function Test-Path { param($LiteralPath) return $true }
    function Set-ItemProperty { param($LiteralPath,$Name,$Value,$Type) Assert ($Name -eq 'ForceDefenderPassiveMode' -and $Value -eq 0) 'Policy must enable active mode'; $qaMock.policyWrites++ }
    function Start-Service { [CmdletBinding()]param($Name) Assert ($Name -eq 'WinDefend') 'Unexpected service'; $qaMock.serviceStarts++ }
    function Set-MpPreference { [CmdletBinding()]param($DisableRealtimeMonitoring,$DisableBehaviorMonitoring,$DisableArchiveScanning,$DisableScriptScanning,$DisableIOAVProtection) Assert (-not ($DisableRealtimeMonitoring -or $DisableBehaviorMonitoring -or $DisableArchiveScanning -or $DisableScriptScanning -or $DisableIOAVProtection)) 'Protection was weakened'; $qaMock.preferenceWrites++ }
    function Get-MpPreference { return @{ExclusionPath=@('C:\','D:\','C:\retained-example')} }
    function Remove-MpPreference { [CmdletBinding()]param($ExclusionPath) Assert (($ExclusionPath -join ',') -eq 'C:\,D:\') 'Only runner whole-drive exclusions may be removed'; $qaMock.exclusionRemovals++ }
    function Update-MpSignature { [CmdletBinding()]param() $qaMock.signatureUpdates++ }
    function Get-MpComputerStatus { [CmdletBinding()]param() $qaMock.statusReads++; return @{ AntivirusEnabled=$qaMock.ready; RealTimeProtectionEnabled=$qaMock.ready } }
    function Start-Sleep { param($Seconds) $qaMock.waits++ }
    $env:GITHUB_ACTIONS='true'; $env:RUNNER_OS='Windows'; $env:RUNNER_ENVIRONMENT='github-hosted'
    $qaMock.policyWrites=0; $qaMock.serviceStarts=0; $qaMock.preferenceWrites=0; $qaMock.exclusionRemovals=0; $qaMock.signatureUpdates=0; $qaMock.statusReads=0; $qaMock.waits=0; $qaMock.ready=$true
    & $activationScript | Out-Null
    Assert ($qaMock.policyWrites -eq 1 -and $qaMock.serviceStarts -eq 1 -and $qaMock.preferenceWrites -eq 1 -and $qaMock.exclusionRemovals -eq 1 -and $qaMock.signatureUpdates -eq 1 -and $qaMock.statusReads -eq 1) 'Ready scanner was not verified'
    $qaMock.ready=$false; $qaMock.statusReads=0; $qaMock.waits=0
    try { & $activationScript | Out-Null; throw 'Inactive scanner was accepted' }
    catch { Assert ($_.Exception.Message -like '*publication remains blocked*') 'Inactive scanner did not fail closed' }
    Assert ($qaMock.statusReads -eq 12 -and $qaMock.waits -eq 12) 'Activation wait must be bounded'
    Write-Output 'RELEASE_DEFENDER_CHECKS_PASSED'
}
finally { foreach ($name in $original.Keys) { [Environment]::SetEnvironmentVariable($name,$original[$name]) } }
