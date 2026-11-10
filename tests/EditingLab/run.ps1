<#
.SYNOPSIS
    Checks spoken edits ("delete the last two words", "replace the word Monday with Tuesday", ...) in a real text box.
.DESCRIPTION
    Opens a small test window (edit-host.ps1) with one text box, brings it to the front, runs editing-lab.selftest.txt
    through VoiceAssistant.exe --self-test (transcripts are scripted; no microphone), prints the results, and closes the
    test window. Run it while you're not using the PC: it stops at the first failed check instead of typing elsewhere.
.EXAMPLE
    .\tests\EditingLab\run.ps1 -Exe .\src\VoiceAssistant.App\bin\Release\net10.0-windows\win-arm64\VoiceAssistant.exe
#>
param([Parameter(Mandatory)] [string] $Exe)

$ErrorActionPreference = 'Stop'

# Windows only lets the process that produced the last input change the foreground window, so a script started in the
# background can't rely on the new Chrome window being in front. Tap an unassigned key (F24) to get that right, bring the
# test window forward, and refuse to run unless it really is in front (otherwise commands would act on your windows).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class LabWindow {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static bool BringToFront(IntPtr h) {
    for (int i = 0; i < 5; i++) {
      keybd_event(0x87, 0, 0, UIntPtr.Zero); keybd_event(0x87, 0, 2, UIntPtr.Zero);
      ShowWindow(h, 9); SetForegroundWindow(h);
      System.Threading.Thread.Sleep(300);
      if (GetForegroundWindow() == h) return true;
    }
    return false;
  }
}
"@
function Get-LabWindow([string] $profileMarker) {
    for ($i = 0; $i -lt 20; $i++) {
        $browser = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profileMarker*" -and $_.CommandLine -notlike '*--type=*' } | Select-Object -First 1
        if ($browser) {
            $p = Get-Process -Id $browser.ProcessId -ErrorAction SilentlyContinue
            if ($p -and $p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }
        }
        Start-Sleep -Milliseconds 500
    }
    return [IntPtr]::Zero
}

$here = $PSScriptRoot
$host_ = Start-Process pwsh -ArgumentList "-NoProfile -STA -ExecutionPolicy Bypass -File `"$(Join-Path $here 'edit-host.ps1')`"" -PassThru -WindowStyle Hidden
try {
    $window = [IntPtr]::Zero
    Add-Type -Namespace EditLab -Name Win -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);'
    for ($i = 0; $i -lt 60 -and $window -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 250
        $window = [EditLab.Win]::FindWindow($null, 'Edit Lab')
    }

    if ($window -eq [IntPtr]::Zero -or -not [LabWindow]::BringToFront($window)) { throw 'The test window could not be brought to the front; nothing was run.' }
    $out = Join-Path $env:TEMP 'va-editlab.out.txt'
    Remove-Item $out -ErrorAction SilentlyContinue
    $p = Start-Process $Exe -ArgumentList "--self-test `"$(Join-Path $here 'editing-lab.selftest.txt')`" `"$out`"" -PassThru
    if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id; throw 'self-test timed out' }
    $lines = Get-Content $out
    $lines
    $pass = @($lines | Where-Object { $_ -match '^\s+PASS' }).Count
    $fail = @($lines | Where-Object { $_ -match '^\s+FAIL' }).Count
    Write-Host "`n$pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0 -and $pass -gt 0) { 'Green' } else { 'Red' })
}
finally {
    if (-not $host_.HasExited) { Stop-Process -Id $host_.Id }
}
