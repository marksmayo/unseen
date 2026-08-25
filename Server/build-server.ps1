# Builds the Linux headless server from Windows.
#
# Usage:
#   .\Server\build-server.ps1 -Unity "$env:USERPROFILE\Unity\Hub\Editor\6000.5.9f1\Editor\Unity.exe"
#   .\Server\build-server.ps1 -Unity ... -Docker
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Unity,
    [string]$OutputDir = "Server/out/linux",
    [switch]$Docker
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $projectRoot "Server/out/build.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logPath) | Out-Null

Write-Host "[unseen] building headless Linux server into $OutputDir"

& $Unity `
    -quit `
    -batchmode `
    -nographics `
    -projectPath $projectRoot `
    -executeMethod Unseen.EditorTools.UnseenBuild.BuildLinuxServer `
    -buildOutput $OutputDir `
    -logFile $logPath

if ($LASTEXITCODE -ne 0) {
    Write-Error "Unity build failed with exit code $LASTEXITCODE. See $logPath"
}

Write-Host "[unseen] player build complete"

if ($Docker) {
    Write-Host "[unseen] building container image unseen/server:dev"
    docker build `
        -f (Join-Path $projectRoot "Server/docker/Dockerfile") `
        --build-arg "BUILD_DIR=out/linux" `
        -t unseen/server:dev `
        (Join-Path $projectRoot "Server")

    if ($LASTEXITCODE -ne 0) { Write-Error "docker build failed" }
    Write-Host "[unseen] image built. Run it with: docker run --rm -p 7770:7770/udp unseen/server:dev"
}
