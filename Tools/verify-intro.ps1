# Checks actual video decoding and lifecycle in an isolated Unity project.
[CmdletBinding()]
param([string]$Unity = 'C:\Program Files\Unity 6000.6.0f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$validationDir = Join-Path $projectRoot 'Temp/IntroValidation'
$logPath = Join-Path $projectRoot 'Logs/intro-playback.log'
foreach ($folder in @('Assets/Editor', 'Assets/Resources/Intro', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $validationDir $folder) | Out-Null
}
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'Logs') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'Assets/Unseen/Scripts/Client/FirstLoadIntro.cs') -Destination (Join-Path $validationDir 'Assets/FirstLoadIntro.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'IntroValidation.cs') -Destination (Join-Path $validationDir 'Assets/Editor/IntroValidation.cs')
Copy-Item -LiteralPath (Join-Path $projectRoot 'Assets/Unseen/Resources/Intro/UnseenIntro.mp4') -Destination (Join-Path $validationDir 'Assets/Resources/Intro/UnseenIntro.mp4')
Copy-Item -LiteralPath (Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $validationDir 'ProjectSettings/ProjectVersion.txt')
@'
{"dependencies":{"com.unity.modules.video":"1.0.0","com.unity.modules.imgui":"1.0.0","com.unity.modules.audio":"1.0.0","com.unity.modules.imageconversion":"1.0.0"}}
'@ | Set-Content -LiteralPath (Join-Path $validationDir 'Packages/manifest.json')
# Graphics are required to decode into a render texture. The editor stays hidden.
foreach ($method in @('IntroValidation.Run', 'IntroValidation.RunSkip')) {
$logPath = Join-Path $projectRoot ('Logs/' + $method + '.log')
$run = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -Wait -WorkingDirectory $validationDir -ArgumentList @(
    '-batchmode', '-force-d3d11', '-projectPath', ('"' + $validationDir + '"'),
    '-executeMethod', $method, '-logFile', ('"' + $logPath + '"'))
$passed = Select-String -LiteralPath $logPath -Pattern 'INTRO VALIDATION PASSED' -Quiet
if ($run.ExitCode -ne 0 -or -not $passed) {
    Get-Content -LiteralPath $logPath -Tail 50
    throw 'Intro playback validation failed.'
}
Select-String -LiteralPath $logPath -Pattern 'INTRO:' | ForEach-Object { $_.Line }
}
Write-Host 'Intro playback validation passed.'
Write-Host ('Decoded frame: ' + (Join-Path $validationDir 'playback.png'))
