<#
.SYNOPSIS
    Runs the voice navigation acceptance script against a fresh Chrome window of local test pages.
.DESCRIPTION
    Opens videos.html, lab.html and mail.html in a NEW Chrome window with a separate, temporary profile (so "second
    tab" means the lab page whatever else is open), runs nav-lab.selftest.txt through VoiceAssistant.exe --self-test,
    prints the results, and closes only that test window. Your own windows are not touched. Run it while you're not
    using the PC: Windows won't let a background process bring the test window to the front while you're typing
    elsewhere (the script then stops at the first failed check instead of typing anywhere).
.EXAMPLE
    .\tests\NavigationLab\run.ps1 -Exe .\src\VoiceAssistant.App\bin\Release\net10.0-windows\win-arm64\VoiceAssistant.exe
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$pages = 'videos.html', 'lab.html', 'mail.html' | ForEach-Object { ([Uri](Join-Path $here $_)).AbsoluteUri }
$profileDir = Join-Path $env:TEMP 'va-navlab-profile'

Start-Process $Chrome -ArgumentList (@("--user-data-dir=$profileDir", '--no-first-run', '--no-default-browser-check', '--hide-crash-restore-bubble', '--new-window') + $pages) | Out-Null
try {
    Start-Sleep -Seconds 8
    $out = Join-Path $env:TEMP 'va-navlab.out.txt'
    Remove-Item $out -ErrorAction SilentlyContinue
    $p = Start-Process $Exe -ArgumentList '--self-test', (Join-Path $here 'nav-lab.selftest.txt'), $out -PassThru
    if (-not $p.WaitForExit(240000)) { Stop-Process -Id $p.Id; throw 'self-test timed out' }
    $lines = Get-Content $out
    $lines
    $pass = @($lines | Where-Object { $_ -match '^\s+PASS' }).Count
    $fail = @($lines | Where-Object { $_ -match '^\s+FAIL' }).Count
    Write-Host "`n$pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0 -and $pass -gt 0) { 'Green' } else { 'Red' })
}
finally {
    # Close only the test profile's Chrome: ask its windows to close first so the profile isn't marked as crashed.
    $test = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*va-navlab-profile*' })
    foreach ($proc in $test) {
        $p = Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne 0) { $p.CloseMainWindow() | Out-Null }
    }

    Start-Sleep -Seconds 2
    $test | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
