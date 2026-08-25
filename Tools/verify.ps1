# Compiles the project and runs the EditMode tests in batch mode.
#
# This is the fastest way to find out whether the project is healthy without opening the editor.
# It writes an NUnit result file and surfaces compile errors from the Unity log.
#
# Usage:
#   .\Tools\verify.ps1
#   .\Tools\verify.ps1 -Unity "C:\path\to\Unity.exe" -SkipTests
[CmdletBinding()]
param(
    [string]$Unity,
    [string]$Version = "6000.5.9f1",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

if (-not $Unity) {
    $candidates = @(
        "$env:USERPROFILE\Unity\Hub\Editor\$Version\Editor\Unity.exe",
        "${env:ProgramFiles}\Unity\Hub\Editor\$Version\Editor\Unity.exe"
    )
    $Unity = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Unity -or -not (Test-Path $Unity)) {
    Write-Error "Unity editor not found. Pass -Unity <path to Unity.exe>."
}

$outDir = Join-Path $projectRoot "Server/out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$compileLog = Join-Path $outDir "compile.log"
$testLog = Join-Path $outDir "tests.log"
$testResults = Join-Path $outDir "tests.xml"

Write-Host "[unseen] using $Unity"

# Pass 1: import assets and compile scripts, then quit. -quit makes this a pure compile check.
#
# Start-Process -Wait, not the call operator: Unity.exe is a GUI-subsystem binary, so PowerShell
# does not block on it with `&` and the script would race ahead while Unity was still starting.
Write-Host "[unseen] compiling..."
$proc = Start-Process -FilePath $Unity -Wait -PassThru -NoNewWindow -ArgumentList @(
    "-batchmode", "-nographics", "-quit",
    "-projectPath", $projectRoot,
    "-logFile", $compileLog)
$compileExit = $proc.ExitCode

$errors = @()
if (Test-Path $compileLog) {
    $errors = Select-String -Path $compileLog -Pattern "error CS\d+" -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Line.Trim() } | Select-Object -Unique
}

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "[unseen] $($errors.Count) compile error(s):" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Full log: $compileLog"
    exit 1
}

if ($compileExit -ne 0) {
    Write-Host "[unseen] Unity exited with $compileExit but reported no CS errors. See $compileLog" -ForegroundColor Yellow
    exit $compileExit
}

Write-Host "[unseen] compile clean" -ForegroundColor Green

if ($SkipTests) { exit 0 }

Write-Host "[unseen] running EditMode tests..."
$testProc = Start-Process -FilePath $Unity -Wait -PassThru -NoNewWindow -ArgumentList @(
    "-batchmode", "-nographics",
    "-projectPath", $projectRoot,
    "-runTests", "-testPlatform", "EditMode",
    "-testResults", $testResults,
    "-logFile", $testLog)
$testExit = $testProc.ExitCode

if (Test-Path $testResults) {
    $xml = [xml](Get-Content $testResults)
    $run = $xml.SelectSingleNode("//test-run")
    if ($run) {
        Write-Host ("[unseen] tests: {0} total, {1} passed, {2} failed, {3} skipped" -f `
            $run.total, $run.passed, $run.failed, $run.skipped) `
            -ForegroundColor ($(if ([int]$run.failed -gt 0) { "Red" } else { "Green" }))

        if ([int]$run.failed -gt 0) {
            $xml.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
                Write-Host "  FAILED $($_.fullname)" -ForegroundColor Red
                if ($_.failure.message) { Write-Host "         $($_.failure.'#text')".Trim() }
            }
        }
    }
    Write-Host "Results: $testResults"

    # A zero-test run exits 0 and looks identical to success. Treat it as a failure: it means the
    # runner discovered nothing, which is usually an assembly definition problem, not a green suite.
    if ($run -and [int]$run.total -eq 0) {
        Write-Host "[unseen] no tests were discovered - check Unseen.Tests.asmdef" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "[unseen] no test results produced. See $testLog" -ForegroundColor Yellow
}

exit $testExit
