<#
.SYNOPSIS
    Smoke test for the app window, the idea board and reminder pop-ups, on the real app.
.DESCRIPTION
    Starts VoiceAssistant.exe, drives its window through UI Automation and checks board.json:
      A1  the window opens on start
      A2  a note typed in the Ideas column is saved
      A3  a reminder typed with a time ("in 1 minute") is saved with a due time
      A4  closing the window keeps the app running
      A5  starting the app again brings the running copy's window back (no second copy)
      A6  the reminder pops up without taking focus; Done ticks it off
      A7  Settings and What you can say pages open
    Your board.json is backed up first and restored afterwards. Voice Assistant must not be running. Run it while you're
    not using the PC: it types into the app's window and stops at the first failed check.
.EXAMPLE
    .\tests\AppLab\run.ps1 -Exe .\src\VoiceAssistant.App\bin\Release\net10.0-windows\win-arm64\VoiceAssistant.exe
#>
param([Parameter(Mandatory)] [string] $Exe)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class AppLabWin {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static bool BringToFront(IntPtr h) {
    for (int i = 0; i < 5; i++) {
      Tap(0x87); ShowWindow(h, 9); SetForegroundWindow(h);
      System.Threading.Thread.Sleep(300);
      if (GetForegroundWindow() == h) return true;
    }
    return false;
  }
}
"@

$A = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$boardFile = Join-Path $env:APPDATA 'VoiceAssistant\board.json'
$backup = "$boardFile.applab-backup"
$results = [System.Collections.Generic.List[string]]::new()
$failed = $false

function Check([string] $id, [string] $what, [bool] $ok) {
    $line = '  {0} {1} {2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $what)
    $results.Add($line)
    Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) { $script:failed = $true; throw "stopped at $id" }
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Find-Window([int] $processId, [string] $name) {
    $cond = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($A::ProcessIdProperty, $processId),
        [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, $name))
    foreach ($w in $A::RootElement.FindAll($Tree::Children, $cond)) {
        # The status pill has the same title as the main window; the main window is the one with the page buttons.
        if ($name -ne 'Voice Assistant' -or (Find-Named $w '*Idea board*')) { return $w }
    }
    return $null
}

function Find-Named($root, [string] $like) {
    foreach ($e in $root.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -like $like) { return $e }
    }
    return $null
}

function Invoke-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Board-Items { if (Test-Path $boardFile) { (Get-Content $boardFile -Raw | ConvertFrom-Json).items } else { @() } }

function Add-Note($window, [string] $box, [string] $text) {
    $edit = Find-Named $window $box
    if (-not $edit) { return $false }
    $edit.SetFocus()
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
    Start-Sleep -Milliseconds 200
    [AppLabWin]::Tap(0x0D)
    return $true
}

if (Get-Process VoiceAssistant -ErrorAction SilentlyContinue) { throw 'Voice Assistant is running; exit it first (tray › Exit).' }
if (Test-Path $boardFile) { Copy-Item $boardFile $backup -Force }
Remove-Item $boardFile -ErrorAction SilentlyContinue
$app = $null
try {
    $app = Start-Process $Exe -PassThru
    $window = Wait-Until { Find-Window $app.Id 'Voice Assistant' } 30
    Check 'A1' 'window opens on start' ($null -ne $window)
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    if (-not [AppLabWin]::BringToFront($hwnd)) { throw 'The app window could not be brought to the front; nothing was typed.' }

    Invoke-Element (Find-Named $window '*Idea board*')
    Start-Sleep -Milliseconds 500
    $typed = Add-Note $window 'Add to Ideas' 'AppLab idea one'
    $idea = Wait-Until { Board-Items | Where-Object { $_.text -eq 'AppLab idea one' -and $_.list -eq 'Ideas' } } 5
    Check 'A2' 'note typed in Ideas is saved' ($typed -and $null -ne $idea)

    $typed = Add-Note $window 'Add to Reminders' 'AppLab stretch in 1 minute'
    $reminder = Wait-Until { Board-Items | Where-Object { $_.text -eq 'AppLab stretch' -and $_.list -eq 'Reminders' -and $_.due } } 5
    Check 'A3' 'reminder with a time is saved with its due time' ($typed -and $null -ne $reminder)

    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Seconds 1
    $app.Refresh()
    $hidden = -not (Find-Window $app.Id 'Voice Assistant')
    Check 'A4' 'closing the window keeps the app running' ((-not $app.HasExited) -and $hidden)

    $second = Start-Process $Exe -PassThru
    $secondExited = $second.WaitForExit(15000)
    $window = Wait-Until { Find-Window $app.Id 'Voice Assistant' } 10
    $front = $window -and (Wait-Until { [AppLabWin]::GetForegroundWindow() -eq [IntPtr]$window.Current.NativeWindowHandle } 5)
    Check 'A5' 'starting it again shows the running copy (second copy exits; window in front)' ($secondExited -and $null -ne $window -and [bool]$front)

    $popup = Wait-Until { Find-Window $app.Id 'Reminder' } 100
    $popupFocused = $popup -and ([AppLabWin]::GetForegroundWindow() -eq [IntPtr]$popup.Current.NativeWindowHandle)
    $textShown = $popup -and (Find-Named $popup 'AppLab stretch')
    $done = if ($popup) { Find-Named $popup 'Done' } else { $null }
    if ($done) { Invoke-Element $done }
    $ticked = Wait-Until { Board-Items | Where-Object { $_.text -eq 'AppLab stretch' -and $_.done } } 5
    Check 'A6' 'reminder pops up without focus; Done ticks it off' ($null -ne $popup -and -not $popupFocused -and [bool]$textShown -and $null -ne $ticked)

    Invoke-Element (Find-Named $window '*Settings*')
    $settings = Wait-Until { Find-Named $window 'Save and restart' } 5
    Invoke-Element (Find-Named $window '*What you can say*')
    $help = Wait-Until { Find-Named $window '*remind me to call mom*' } 5
    Check 'A7' 'Settings and What you can say pages open' ($null -ne $settings -and $null -ne $help)
}
catch {
    if (-not $failed) { $results.Add("  FAIL $($_.Exception.Message)"); Write-Host "  FAIL $($_.Exception.Message)" -ForegroundColor Red }
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; $app.WaitForExit(5000) | Out-Null }
    Remove-Item $boardFile -ErrorAction SilentlyContinue
    if (Test-Path $backup) { Move-Item $backup $boardFile -Force }
}

$pass = @($results | Where-Object { $_ -match '^\s+PASS' }).Count
$fail = @($results | Where-Object { $_ -match '^\s+FAIL' }).Count
Write-Host "`n$pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0 -and $pass -gt 0) { 'Green' } else { 'Red' })
