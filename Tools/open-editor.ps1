# Opens this project in the Unity editor, with the game scene already loaded.
#
# The Unity Hub installed from winget is an MSIX package and has been unreliable at launching
# editors on this machine, so this bypasses it and runs the editor directly.
[CmdletBinding()]
param(
    [string]$Unity,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    # The project's own version, not a copy of it kept here that goes stale on every upgrade.
    $versionFile = Join-Path $projectRoot "ProjectSettings/ProjectVersion.txt"
    if (Test-Path $versionFile) {
        $Version = (Select-String -Path $versionFile -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    }
}

if (-not $Unity) {
    $candidates = @(
        "$env:USERPROFILE\Unity\Hub\Editor\$Version\Editor\Unity.exe",
        "${env:ProgramFiles}\Unity\Hub\Editor\$Version\Editor\Unity.exe",
        # Installed outside the Hub, which is where this machine's editor actually is - see above.
        "${env:ProgramFiles}\Unity $Version\Editor\Unity.exe"
    )
    $Unity = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Unity -or -not (Test-Path $Unity)) {
    Write-Error "Unity $Version not found. Pass -Unity <path to Unity.exe>."
}

Write-Host "[unseen] opening $projectRoot in $Unity"
Start-Process -FilePath $Unity -ArgumentList @(
    "-projectPath", $projectRoot,
    "-executeMethod", "Unseen.EditorTools.UnseenProjectSetup.OpenGameScene")
Write-Host "[unseen] editor launching - first load takes a minute while it imports."
