<#
.SYNOPSIS
    Builds portable, ready-to-run zips of Voice Assistant for sharing (no install, no .NET needed).

.DESCRIPTION
    For each runtime, publishes a self-contained build and zips it into dist\:
      VoiceAssistant-<version>-win-x64.zip      Intel/AMD PCs (most Windows laptops/desktops)
      VoiceAssistant-<version>-win-arm64.zip    ARM PCs (Snapdragon / Surface Pro X / Copilot+ PCs)
    Without -IncludeModels the app downloads the speech models (~265 MB, SHA-256 verified) on first run.
    With -IncludeModels the zip also contains them in a "models" folder and works fully offline.

.EXAMPLE
    .\scripts\package.ps1
.EXAMPLE
    .\scripts\package.ps1 -Runtime win-x64 -IncludeModels
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64'),

    [switch] $IncludeModels
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\VoiceAssistant.App\VoiceAssistant.App.csproj'
$Dist = Join-Path $RepoRoot 'dist'
$ModelsSource = Join-Path $env:LOCALAPPDATA 'VoiceAssistant\models'
$ModelFiles = @('ggml-small.en-q8_0.bin', 'ggml-silero-v6.2.0.bin')

$dotnet = @((Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1), (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) |
    Where-Object { $_ -and (Test-Path $_) -and ((& $_ --list-sdks 2>$null) -match '^10\.') } | Select-Object -First 1
if (-not $dotnet) { throw 'The .NET 10 SDK is required to package. Run scripts\install.ps1 once (it installs it), then retry.' }

$version = ([xml](Get-Content $Project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force $Dist | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

foreach ($rid in $Runtime) {
    $name = "VoiceAssistant-$version-$rid" + $(if ($IncludeModels) { '-offline' } else { '' })
    $folder = Join-Path $Dist $name
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }

    Write-Host "==> Publishing $rid" -ForegroundColor Cyan
    & $dotnet publish $Project -c Release -r $rid --self-contained true -o $folder -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid." }

    # Whisper.net ships native libraries for every architecture; keep only this one.
    Get-ChildItem (Join-Path $folder 'runtimes') -Directory | Where-Object { $_.Name -ne $rid } | Remove-Item -Recurse -Force
    Get-ChildItem $folder -Filter '*.pdb' -Recurse | Remove-Item -Force

    if ($IncludeModels) {
        $models = New-Item -ItemType Directory -Force (Join-Path $folder 'models')
        foreach ($file in $ModelFiles) {
            $source = Join-Path $ModelsSource $file
            if (-not (Test-Path $source)) { throw "Model $file not found in $ModelsSource. Run the app (or install.ps1) once to download it." }
            Copy-Item $source $models
        }
    }

    @"
VOICE ASSISTANT $version  ($rid)
Talk to your Windows PC: dictate into any app, and control apps with "Hey Jarvis". Runs fully on this PC.

START
  1. Unzip this folder anywhere you like (e.g. Documents).
  2. Double-click VoiceAssistant.exe.
     Windows may say "Windows protected your PC" because the app isn't code-signed:
     click "More info" -> "Run anyway".
  3. $(if ($IncludeModels) { 'It starts listening in a few seconds.' } else { 'First run downloads the speech model (~265 MB, one time). Watch the pill at the bottom of the screen.' })
  4. Allow microphone access if Windows asks (Settings > Privacy & security > Microphone > "Let desktop apps access your microphone").
  Look for the purple microphone icon in the system tray (bottom-right, maybe under the ^ arrow).

DICTATE ANYWHERE
  Hold Ctrl+Win, speak, release   -> text is typed where your cursor is
  Ctrl+Win+Space                  -> hands-free dictation (press again to finish)
  Esc cancels  ·  Alt+Shift+Z pastes the last dictation again

VOICE COMMANDS (start with "Hey Jarvis")
  "Hey Jarvis, open Notepad"  ->  "Start typing"  ->  speak  ->  "Stop typing"
  "Hey Jarvis, open Chrome"   ->  "Go to the search box"  ->  "weather in Bangalore"  ->  "Search"
  "Search for pizza near me", "New tab", "Go back", "Press enter", "Pause listening"
  Right-click the tray icon -> "Voice commands..." for the full list.

PAUSE / QUIT
  Ctrl+Alt+Space or left-click the tray icon pauses the microphone. Right-click -> Exit quits.
  Tray -> "Start when I sign in" to launch it automatically.

PRIVACY
  Speech is transcribed on this PC; audio and text are never uploaded or saved to disk.
  Settings: %APPDATA%\VoiceAssistant\settings.json   Models: $(if ($IncludeModels) { 'the models folder next to the app' } else { '%LOCALAPPDATA%\VoiceAssistant\models' })

UNINSTALL
  Exit from the tray, then delete this folder (and %LOCALAPPDATA%\VoiceAssistant to remove the models).

Source: https://github.com/shivam098/windows-voice-assistant
"@ | Set-Content (Join-Path $folder 'README-FIRST.txt') -Encoding UTF8

    $zip = "$folder.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Write-Host "==> Zipping $name" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item $folder -Recurse -Force
    "{0}  {1:N0} MB" -f $zip, ((Get-Item $zip).Length / 1MB)
}
