# tools/launch-xtb.ps1
#
# The ONE WAY to see the VerseOps XrmToolBox plugin live on this machine.
#
# DO NOT launch VerseOps.XrmToolBox\bin\Release\net48\XrmToolBox.exe — that
# SDK-bundled copy triggers a "Windows Identity Foundation 3.5" prereq dialog
# every launch on this box. It's a host-side check, unrelated to our plugin.
#
# This script uses the official portable XrmToolBox at
# %LOCALAPPDATA%\XrmToolBox-portable\ (sha256-verified ZIP from
# https://www.xrmtoolbox.com/, downloaded 2026-06-08) which does NOT
# trigger the WIF check.
#
# What it does:
#   1. (Optional) Build the plugin in Release.
#   2. Copy the 5 plugin DLLs into the portable's Plugins\VerseOps\ folder.
#   3. Stop any running XrmToolBox.exe so the new DLLs aren't locked.
#   4. Launch the portable host.
#   5. Wait for the main window (EnumWindows-based — Get-Process.MainWindowHandle
#      lies about hwnd=0 even after the window is visible) and bring it to
#      the foreground.

[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $SkipForeground
)

$ErrorActionPreference = 'Stop'

$repo     = Split-Path -Parent $PSScriptRoot
$src      = Join-Path $repo 'VerseOps.XrmToolBox\bin\Release\net48'
$portable = Join-Path $env:LOCALAPPDATA 'XrmToolBox-portable'
# The portable XTB shares its plugin search path with the installed XTB:
# %APPDATA%\MscrmTools\XrmToolBox\Plugins (flat — no per-plugin subfolders).
# Deploying anywhere else (including %LOCALAPPDATA%\XrmToolBox-portable\Plugins\)
# silently does NOTHING — XTB will load the stale copy from this Roaming path
# instead and you'll spend hours wondering why your edits don't show up.
$pluginDir = Join-Path $env:APPDATA 'MscrmTools\XrmToolBox\Plugins'
$exe       = Join-Path $portable 'XrmToolBox.exe'

if (-not (Test-Path $exe)) {
    throw "Portable XrmToolBox not found at $exe. Re-download from https://www.xrmtoolbox.com/ and unzip to that path."
}

if ($Build) {
    Write-Host "--- Building VerseOps.XrmToolBox (Release) ---" -ForegroundColor Cyan
    & dotnet build (Join-Path $repo 'VerseOps.XrmToolBox\VerseOps.XrmToolBox.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

if (-not (Test-Path (Join-Path $src 'VerseOps.XrmToolBox.dll'))) {
    throw "Plugin build output not found at $src — re-run with -Build or build manually."
}

Write-Host "--- Stopping any running XrmToolBox.exe ---" -ForegroundColor Cyan
Get-Process -Name XrmToolBox -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  stopping PID $($_.Id)"
    $_ | Stop-Process -Force
}
Start-Sleep -Milliseconds 600

Write-Host "--- Deploying plugin to $pluginDir ---" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
$dlls = @(
    'VerseOps.XrmToolBox.dll',
    'VerseOps.Api.Core.dll',
    'Microsoft.Identity.Client.dll',
    'Microsoft.Identity.Client.Extensions.Msal.dll',
    'Microsoft.IdentityModel.Abstractions.dll'
)
foreach ($d in $dlls) {
    $p = Join-Path $src $d
    if (Test-Path $p) {
        Copy-Item $p $pluginDir -Force
        $sz = [int]((Get-Item $p).Length / 1KB)
        Write-Host "  $d  ($sz KB)"
    } else {
        Write-Warning "  MISSING $d in $src"
    }
}

Write-Host "--- Launching portable XrmToolBox ---" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -WorkingDirectory $portable -PassThru
Write-Host "  PID = $($p.Id)"

if ($SkipForeground) {
    Write-Host "  -SkipForeground set; not foregrounding."
    return
}

# Wait for the main window. Don't trust Get-Process.MainWindowHandle —
# it stays 0 even after the visible window is created. Enumerate windows.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinHelper {
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int n);
  public static IntPtr FindMainFor(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        if (sb.Length > 8) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@ -ErrorAction SilentlyContinue

$deadline = (Get-Date).AddSeconds(40)
$hwnd = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { throw "XrmToolBox exited unexpectedly (ec=$($p.ExitCode))" }
    $hwnd = [WinHelper]::FindMainFor([uint32]$p.Id)
    if ($hwnd -ne [IntPtr]::Zero) { break }
}

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Warning "Main window did not appear within 40s. Check the taskbar — XTB may still be loading."
    return
}

[WinHelper]::ShowWindowAsync($hwnd, 9) | Out-Null   # SW_RESTORE
[WinHelper]::SetForegroundWindow($hwnd) | Out-Null
Write-Host "  Main window hwnd=0x$($hwnd.ToInt64().ToString('X')) — foregrounded." -ForegroundColor Green
Write-Host ""
Write-Host "  Click the VerseOps tile to load the plugin." -ForegroundColor Green
