# Runs a real server and real clients as separate processes, and reports what happened.
#
# Everything this project knows about its own netcode comes from one of two places: unit tests over
# loopback in a single process, or a load test with sixty-four bots and nobody connected. The second
# reported `out 0 kbps` for four minutes, and that zero was the whole story - encoding and interest
# management are the costs that scale with players, and neither had ever been paid.
#
# So this launches genuinely separate processes that find each other over a socket. It is the only
# arrangement that exercises the handshake, the sequence windows, prediction, reconciliation, and
# the destructible ids two machines have to agree on without exchanging a list.
#
# Usage:
#   .\Tools\multiplayer-test.ps1
#   .\Tools\multiplayer-test.ps1 -Clients 4 -Seconds 120 -Netsim mobile
#   .\Tools\multiplayer-test.ps1 -SkipBuild        # reuse the last player build
[CmdletBinding()]
param(
    [int]$Clients = 2,
    [int]$Seconds = 60,
    [int]$Port = 7787,
    [int]$Seed = 20260827,

    # How long the server holds the lobby once the first client is in it. Generous by default,
    # because a client spends most of a minute generating its own town before it can connect at
    # all - and a countdown shorter than that starts the match with whoever loaded first and locks
    # everybody else out to spectate.
    [int]$Lobby = 90,

    # domestic or mobile. Empty means the real link, which on one machine is loopback and perfect.
    [string]$Netsim = "",

    [switch]$SkipBuild,
    [string]$Unity,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $versionFile = Join-Path $projectRoot "ProjectSettings/ProjectVersion.txt"
    if (Test-Path $versionFile) {
        $Version = (Select-String -Path $versionFile -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    }
}

if (-not $Unity) {
    $candidates = @(
        "$env:USERPROFILE\Unity\Hub\Editor\$Version\Editor\Unity.exe",
        "${env:ProgramFiles}\Unity\Hub\Editor\$Version\Editor\Unity.exe",
        "${env:ProgramFiles}\Unity $Version\Editor\Unity.exe"
    )
    $Unity = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$outDir  = Join-Path $projectRoot "Server/out/multiplayer"
$buildDir = Join-Path $projectRoot "Server/out/client"
$exe = Join-Path $buildDir "unseen.exe"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Get-ChildItem $outDir -Filter *.log -ErrorAction SilentlyContinue | Remove-Item -Force

# One build, run as both roles.
#
# The server is the same binary launched with -server rather than a dedicated-server subtarget
# build, which would need the Windows Dedicated Server module installed and a second build every
# run. What it costs is that the server carries rendering code it never calls; what it buys is that
# this script works on any machine that can build the game at all.
if (-not $SkipBuild) {
    if (-not $Unity -or -not (Test-Path $Unity)) {
        Write-Error "Unity $Version not found. Pass -Unity <path>, or -SkipBuild to reuse $exe."
    }

    Write-Host "[mp] building the player into $buildDir (this takes a few minutes)"
    $buildStartedAt = Get-Date

    Start-Process -FilePath $Unity -PassThru -NoNewWindow -ArgumentList @(
        "-quit", "-batchmode", "-nographics",
        "-projectPath", $projectRoot,
        "-executeMethod", "Unseen.EditorTools.UnseenBuild.BuildWindowsClient",
        "-buildOutput", "Server/out/client",
        "-logFile", (Join-Path $outDir "build.log")) | Out-Null

    # Waited on by watching the log, not the process and not the exit code.
    #
    # Neither of those works. Unity.exe is a GUI-subsystem binary so the call operator does not
    # block on it at all, and it relaunches itself during a build, so even Start-Process -Wait
    # returns while the build runs on - with an exit code that means nothing. This script declared
    # a build failure twice while the build it was complaining about finished perfectly.
    #
    # Nor is a fresh unseen.exe the signal: that file is the launcher stub and an incremental build
    # does not rewrite it. The code goes into unseen_Data. Asking the log what happened is the only
    # thing here that is actually about the build.
    $buildLog = Join-Path $outDir "build.log"
    $deadline = (Get-Date).AddMinutes(25)
    $done = $false

    while ((Get-Date) -lt $deadline -and -not $done) {
        Start-Sleep -Seconds 5

        $unityRunning = @(Get-Process Unity -ErrorAction SilentlyContinue).Count -gt 0
        if ($unityRunning -or -not (Test-Path $buildLog)) { continue }

        $done = Select-String -Path $buildLog -Pattern "Exiting batchmode successfully" -Quiet
        if ($done) { break }

        $failed = Select-String -Path $buildLog -Pattern "Build Failed|error CS|BuildFailedException" -Quiet
        if ($failed) { Write-Error "build failed; see $buildLog" }
    }

    if (-not $done) { Write-Error "the build never reported success; see $buildLog" }
    Write-Host "[mp] build finished"
}

if (-not (Test-Path $exe)) { Write-Error "no player at $exe. Run without -SkipBuild." }

$netsimArgs = @()
if ($Netsim) { $netsimArgs = @("-netsim", $Netsim) }

# A player build logs nothing by default - UnseenLog.Verbose is false outside the editor, on the
# reasonable grounds that a shipped game should not narrate itself. This harness measures the game
# by reading what it says, so it has to ask. Discovered the hard way: the first run produced a
# server that loaded the scene, ran perfectly, and wrote not one line about it.
$commonArgs = @("-batchmode", "-nographics", "-unseen-verbose") + $netsimArgs

$processes = @()

try {
    $serverLog = Join-Path $outDir "server.log"

    Write-Host "[mp] server on port $Port, seed $Seed"
    $processes += Start-Process -FilePath $exe -PassThru -ArgumentList ($commonArgs + @(
        "-server", "-port", $Port, "-seed", $Seed, "-lobby", $Lobby, "-logFile", $serverLog))

    # The server has to be listening before anybody dials it. A client that arrives early retries
    # on its own - the handshake is built for that - but starting them together makes every run
    # begin with a burst of failures that look like a fault and are not.
    $ready = $false
    for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
        Start-Sleep -Milliseconds 1000
        if (Test-Path $serverLog) {
            $ready = (Select-String -Path $serverLog -Pattern "booted as DedicatedServer" -Quiet)
        }
    }

    if (-not $ready) { Write-Error "the server never finished booting; see $serverLog" }
    Write-Host "[mp] server up"

    for ($c = 1; $c -le $Clients; $c++) {
        # The same seed as the server, always. Both sides generate the town from it and agree on
        # destructible ids by counting their own copy - a client on a different seed is playing in
        # a different town and would break the wrong walls.
        $processes += Start-Process -FilePath $exe -PassThru -ArgumentList ($commonArgs + @(
            "-connect", "127.0.0.1:$Port",
            "-name", "player$c",
            "-seed", $Seed,
            "-logFile", (Join-Path $outDir "client$c.log")))

        Write-Host "[mp] client $c launched"
    }

    Write-Host "[mp] running for $Seconds s"
    Start-Sleep -Seconds $Seconds
}
finally {
    foreach ($p in $processes) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- what happened

Write-Host ""
Write-Host "[mp] ---- server ----"

$serverLines = @(Select-String -Path (Join-Path $outDir "server.log") -Pattern "\| \d+ players \|" |
    ForEach-Object { $_.Line })

if ($serverLines.Count -eq 0) {
    Write-Host "[mp] the server logged no status lines at all"
} else {
    $serverLines | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }

    $peak = 0
    foreach ($line in $serverLines) {
        if ($line -match "\| (\d+) players \|") {
            $n = [int]$Matches[1]
            if ($n -gt $peak) { $peak = $n }
        }
    }

    Write-Host ""
    Write-Host "[mp] peak connected clients: $peak of $Clients"
}

Write-Host ""
Write-Host "[mp] ---- clients ----"

$connected = 0
$withBody = 0

for ($c = 1; $c -le $Clients; $c++) {
    $log = Join-Path $outDir "client$c.log"
    if (-not (Test-Path $log)) { Write-Host "  client $c : no log"; continue }

    $last = @(Select-String -Path $log -Pattern "\[Unseen\] client \|" | ForEach-Object { $_.Line }) |
        Select-Object -Last 1

    if ($last) {
        $connected++
        if ($last -match "\bbody,") { $withBody++ }
        Write-Host "  client $c : $($last -replace '^.*\[Unseen\] ', '')"
    } else {
        $problem = @(Select-String -Path $log -Pattern "Exception|error|failed|refused" |
            ForEach-Object { $_.Line }) | Select-Object -First 1
        Write-Host "  client $c : never reported status. $problem"
    }
}

Write-Host ""
Write-Host "[mp] clients reporting: $connected of $Clients, with a body: $withBody"
Write-Host "[mp] logs in $outDir"

if ($peak -lt $Clients -or $withBody -lt $Clients) { exit 1 }
