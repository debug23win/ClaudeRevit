param([switch]$Require2027)
$ErrorActionPreference = 'Stop'
$checkExe = Join-Path $PSScriptRoot 'output\DetectionCheck.exe'
if (-not (Test-Path -LiteralPath $checkExe)) { throw 'Compile DetectionCheck.iss before running this check.' }
$outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'output'))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $outputRoot ('fixture-' + [guid]::NewGuid().ToString('N'))))
if (-not $fixtureRoot.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Invalid fixture path.' }
$fixtureDir = Join-Path $fixtureRoot 'Custom drive\Revit 2027'
$checkLog = Join-Path $fixtureRoot 'detection.log'
try {
    New-Item -ItemType Directory -Path $fixtureDir | Out-Null
    # Native executable with FileVersion 27 from the compiled Inno check; never run the copy.
    Copy-Item -LiteralPath $checkExe -Destination (Join-Path $fixtureDir 'Revit.exe')
    $checkArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', ('/FIXTURE="' + $fixtureDir + '"'), ('/LOG="' + $checkLog + '"'))
    if ($Require2027) { $checkArgs += '/REQUIRE2027' }
    $process = Start-Process -FilePath $checkExe -ArgumentList $checkArgs -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Installer detection check timed out.' }
    # Inno intentionally exits before installation, so inspect its recorded verdict.
    if (-not (Test-Path -LiteralPath $checkLog)) { throw 'Installer check did not produce a log.' }
    $verdict = Select-String -LiteralPath $checkLog -Pattern 'INSTALLER_DETECTION_CHECKS_' | Select-Object -Last 1
    if (-not $verdict -or $verdict.Line -notmatch 'INSTALLER_DETECTION_CHECKS_PASSED') {
        Get-Content -LiteralPath $checkLog | Write-Output
        throw 'Installer detection checks failed.'
    }
    Write-Output $verdict.Line
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
