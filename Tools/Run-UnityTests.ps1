<#
.SYNOPSIS
Run MLF Legends Unity tests locally without assuming CI or claiming results.
.DESCRIPTION
Starts Unity.exe in batchmode, waits for that process to exit, and reports PASS only when
Unity exits with code 0 AND writes a fresh NUnit XML with total > 0, failed = 0, passed = total.
Any other outcome (missing Editor, open/locked project, version mismatch, timeout, abnormal
termination, missing or unparseable XML) ends with "FAIL:" and script exit code 1.
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
    [string]$Mode = 'EditMode',
    [ValidateRange(1, 1440)]
    [int]$TimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    exit 1
}

# Quote one argument for the Windows command line (CommandLineToArgvW rules), so paths with
# spaces and trailing backslashes reach Unity intact.
function ConvertTo-NativeArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '\') {
            $backslashes++
            continue
        }
        if ($ch -eq '"') {
            [void]$builder.Append('\', $backslashes * 2 + 1)
        } elseif ($backslashes -gt 0) {
            [void]$builder.Append('\', $backslashes)
        }
        $backslashes = 0
        [void]$builder.Append($ch)
    }
    [void]$builder.Append('\', $backslashes * 2)
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Show-LogDiagnostics([string]$LogPath) {
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        Write-Host "Editor log was not created: $LogPath" -ForegroundColor Yellow
        return
    }
    $known = @(
        'another Unity instance is running',
        'It looks like another Unity instance',
        'No valid Unity Editor license',
        'License is not active',
        'Scripts have compiler errors',
        'error CS\d+',
        'Aborting batchmode due to failure',
        'Crash!!!'
    )
    $hits = Select-String -LiteralPath $LogPath -Pattern $known | Select-Object -First 20
    if ($hits) {
        Write-Host 'Known problems found in the Editor log:' -ForegroundColor Yellow
        $hits | ForEach-Object { Write-Host ("  line {0}: {1}" -f $_.LineNumber, $_.Line.Trim()) }
    }
    Write-Host "Last 30 lines of ${LogPath}:" -ForegroundColor Yellow
    Get-Content -LiteralPath $LogPath -Tail 30 | ForEach-Object { Write-Host "  $_" }
}

function Get-IntAttribute($Node, [string]$Name) {
    $raw = $Node.GetAttribute($Name)
    $value = 0
    if (-not [int]::TryParse($raw, [ref]$value)) {
        return $null
    }
    return $value
}

# --- Editor ---------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($EditorPath) -or -not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
    Fail "Unity Editor executable not found: '$EditorPath'. Supply -EditorPath with the full path to Unity.exe."
}
$EditorPath = (Resolve-Path -LiteralPath $EditorPath).Path

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$versionFile = Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
    Fail "Not a Unity project (missing $versionFile)."
}
$projectVersion = (Select-String -LiteralPath $versionFile -Pattern '^m_EditorVersion:\s*(\S+)' | Select-Object -First 1).Matches[0].Groups[1].Value
$editorVersion = ((Get-Item -LiteralPath $EditorPath).VersionInfo.ProductVersion -split '_')[0]
if ($editorVersion -ne $projectVersion) {
    # Running a different Editor in batchmode would silently upgrade/reimport the project.
    Fail "Editor version '$editorVersion' does not match project version '$projectVersion'. Supply the matching Unity.exe."
}

# --- Project must not be open ---------------------------------------------------------------
$lockFile = Join-Path $projectRoot 'Temp\UnityLockfile'
if (Test-Path -LiteralPath $lockFile -PathType Leaf) {
    try {
        $stream = [System.IO.File]::Open($lockFile, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        Write-Host "Stale $lockFile found (not held by any process); continuing." -ForegroundColor Yellow
    } catch {
        Fail "Project is open in another Unity instance ($lockFile is locked). Close the Unity Editor for $projectRoot and retry."
    }
}

# --- Output paths ---------------------------------------------------------------------------
$resultsDir = Join-Path $projectRoot 'TestResults'
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
$resultsXml = Join-Path $resultsDir ("stage00-{0}.xml" -f $Mode.ToLowerInvariant())
$editorLog = Join-Path $resultsDir ("stage00-{0}.log" -f $Mode.ToLowerInvariant())

foreach ($stale in @($resultsXml, $editorLog)) {
    if (Test-Path -LiteralPath $stale) {
        try {
            Remove-Item -LiteralPath $stale -Force
        } catch {
            Fail "Cannot delete previous output '$stale' (is it open elsewhere?): $($_.Exception.Message)"
        }
    }
}

Write-Host "Unity Editor: $EditorPath ($editorVersion)"
Write-Host "Project: $projectRoot"
Write-Host "Mode: $Mode"
Write-Host "Results: $resultsXml"
Write-Host "Editor log: $editorLog"
Write-Host "Timeout: $TimeoutMinutes min"

# --- Run ------------------------------------------------------------------------------------
# -runTests exits the Editor after writing the results. Do not add -quit.
$unityArgs = @(
    '-batchmode', '-nographics',
    '-projectPath', $projectRoot,
    '-runTests', '-testPlatform', $Mode,
    '-testResults', $resultsXml,
    '-logFile', $editorLog
)
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $EditorPath
$startInfo.Arguments = ($unityArgs | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
$startInfo.WorkingDirectory = $projectRoot
$startInfo.UseShellExecute = $false

# Wait on Unity.exe itself (not via '&' or Start-Process -Wait): '&' does not block on the GUI
# subsystem binary, and Start-Process -Wait also waits for helper processes Unity leaves behind.
$startTime = Get-Date
Write-Host "Starting: `"$EditorPath`" $($startInfo.Arguments)"
try {
    $process = [System.Diagnostics.Process]::Start($startInfo)
} catch {
    Fail "Could not start Unity Editor: $($_.Exception.Message)"
}
Write-Host "Unity PID $($process.Id); waiting for exit..."

if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    Write-Host "Unity did not exit within $TimeoutMinutes min; terminating PID $($process.Id)." -ForegroundColor Yellow
    try { $process.Kill(); $process.WaitForExit(30000) | Out-Null } catch { }
    Show-LogDiagnostics $editorLog
    Fail "Unity timed out after $TimeoutMinutes min. Review $editorLog."
}
$process.WaitForExit()
$exitCode = $process.ExitCode
$elapsed = (Get-Date) - $startTime
Write-Host ("Unity exited with code {0} after {1:N0} s." -f $exitCode, $elapsed.TotalSeconds)

# --- Results --------------------------------------------------------------------------------
# Parse the XML even on a nonzero exit code: Unity returns 2 for failed tests and the XML lists them.
$summary = $null
$xmlProblem = $null
if (-not (Test-Path -LiteralPath $resultsXml -PathType Leaf)) {
    $xmlProblem = "Unity did not write a test result XML ($resultsXml)."
} elseif ((Get-Item -LiteralPath $resultsXml).LastWriteTime -lt $startTime.AddSeconds(-2)) {
    $xmlProblem = "Result XML $resultsXml is older than this run."
} else {
    $xml = New-Object System.Xml.XmlDocument
    try {
        $xml.Load($resultsXml)
    } catch {
        $xmlProblem = "Result XML $resultsXml is not valid XML: $($_.Exception.Message)"
    }
    if ($null -eq $xmlProblem) {
        $testRun = $xml.SelectSingleNode('/test-run')
        if ($null -eq $testRun) {
            $xmlProblem = "No NUnit /test-run root in $resultsXml."
        } else {
            $summary = [ordered]@{
                Total        = Get-IntAttribute $testRun 'total'
                Passed       = Get-IntAttribute $testRun 'passed'
                Failed       = Get-IntAttribute $testRun 'failed'
                Inconclusive = Get-IntAttribute $testRun 'inconclusive'
                Skipped      = Get-IntAttribute $testRun 'skipped'
                Result       = $testRun.GetAttribute('result')
            }
            if ($null -eq $summary.Total -or $null -eq $summary.Passed -or $null -eq $summary.Failed) {
                $xmlProblem = "/test-run in $resultsXml lacks integer total/passed/failed attributes."
            }
        }
    }
}

if ($null -ne $summary) {
    Write-Host ("Executed: {0} | Passed: {1} | Failed: {2} | Inconclusive: {3} | Skipped: {4} | Result: {5}" -f `
        $summary.Total, $summary.Passed, $summary.Failed, $summary.Inconclusive, $summary.Skipped, $summary.Result)
    $failedCases = $xml.SelectNodes("//test-case[starts-with(@result,'Failed')]")
    foreach ($case in $failedCases | Select-Object -First 25) {
        Write-Host "  FAILED: $($case.GetAttribute('fullname'))" -ForegroundColor Red
        $message = $case.SelectSingleNode('failure/message')
        if ($null -ne $message) {
            Write-Host "    $(($message.InnerText.Trim() -split "`r?`n")[0])"
        }
    }
}

if ($exitCode -ne 0) {
    # Exit code 2 with a parsed XML already lists the failed tests; the log tail only adds noise.
    if (-not ($exitCode -eq 2 -and $null -ne $summary -and $summary.Failed -gt 0)) {
        Show-LogDiagnostics $editorLog
    }
    $hint = switch ($exitCode) {
        2 { ' (Unity: one or more tests failed)' }
        3 { ' (Unity: test run error, e.g. compilation or setup failure)' }
        default { ' (0x{0:X8}; abnormal termination)' -f $exitCode }
    }
    if ($null -ne $xmlProblem) { Write-Host $xmlProblem -ForegroundColor Yellow }
    Fail "Unity exited with code $exitCode$hint. Review $editorLog."
}
if ($null -ne $xmlProblem) {
    Show-LogDiagnostics $editorLog
    Fail "$xmlProblem Exit code 0 alone is not treated as success. Review $editorLog."
}
if ($summary.Total -le 0 -or $summary.Failed -ne 0 -or $summary.Passed -ne $summary.Total) {
    Show-LogDiagnostics $editorLog
    Fail "Not an all-passed run (total=$($summary.Total), passed=$($summary.Passed), failed=$($summary.Failed)). Review $resultsXml and $editorLog."
}

Write-Host "PASS: $Mode tests ($($summary.Passed)/$($summary.Total)). Save the XML/log and record the result in Docs/QA.md." -ForegroundColor Green
exit 0
