<#
.SYNOPSIS
Run MLF Legends Unity tests locally without assuming CI or claiming results.
.EXAMPLE
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode EditMode
.EXAMPLE
.\Tools\Run-UnityTests.ps1 -EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -Mode PlayMode

Close the Unity Editor project before running a batchmode test.
Results and logs are generated under TestResults/ (ignored by Git).
#>

[CmdletBinding()]
param(
    [string]$EditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe',
    [ValidateSet('EditMode', 'PlayMode')]
    [string]$Mode = 'EditMode'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
    throw "Unity Editor executable not found: $EditorPath. Supply -EditorPath."
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resultsDir = Join-Path $projectRoot 'TestResults'
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

$resultsXml = Join-Path $resultsDir ("stage00-{0}.xml" -f $Mode.ToLowerInvariant())
$editorLog = Join-Path $resultsDir ("stage00-{0}.log" -f $Mode.ToLowerInvariant())

Remove-Item -LiteralPath $resultsXml -ErrorAction SilentlyContinue

Write-Host "Unity Editor: $EditorPath"
Write-Host "Project: $projectRoot"
Write-Host "Mode: $Mode"
Write-Host "Results: $resultsXml"
Write-Host 'Close the project in the Unity GUI before running these tests.'

# -runTests exits the Editor after writing the results. Do not add -quit.
& $EditorPath -batchmode -nographics -projectPath $projectRoot -runTests -testPlatform $Mode -testResults $resultsXml -logFile $editorLog
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    throw "Unity exited with code $exitCode. Review $editorLog."
}
if (-not (Test-Path -LiteralPath $resultsXml -PathType Leaf)) {
    throw "Unity exited without a test result XML. Review $editorLog."
}

[xml]$xml = Get-Content -LiteralPath $resultsXml -Raw
$testRun = $xml.SelectSingleNode('/test-run')
if ($null -eq $testRun) {
    throw "No NUnit test-run root found in $resultsXml."
}

$total = [int]$testRun.GetAttribute('total')
$failed = [int]$testRun.GetAttribute('failed')
$passed = [int]$testRun.GetAttribute('passed')
Write-Host "Executed: $total | Passed: $passed | Failed: $failed"

if ($total -eq 0 -or $failed -ne 0 -or $passed -ne $total) {
    throw "Unity test result is not an all-passed run. Review $resultsXml and $editorLog."
}

Write-Host "PASS: $Mode tests. Save the XML/log and record the result in Docs/QA.md."
