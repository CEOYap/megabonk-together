<#
.SYNOPSIS
    Saves a PNG of every running Megabonk window (even unfocused or behind others), named by copy.

.DESCRIPTION
    For hands-off sessions (run-local-session.ps1): the logs say what code ran, a capture shows what
    the game is actually doing (which map, whether the host is walking into a wall, a stuck menu).
    Uses PrintWindow, so the windows do not need to be in front.

.EXAMPLE
    pwsh scripts/test/capture-game-windows.ps1 -OutDir artifacts\local-session\captures
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path "artifacts\local-session\captures"),
    [string] $HostPath = "C:\Program Files (x86)\Steam\steamapps\common\Megabonk"
)

Add-Type -AssemblyName System.Drawing
if (-not ([System.Management.Automation.PSTypeName]'OmniBonkCapture').Type) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class OmniBonkCapture {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    public struct RECT { public int L, T, R, B; }
}
"@
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format "HHmmss"
foreach ($p in Get-Process Megabonk -ErrorAction SilentlyContinue) {
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { continue }
    $r = New-Object OmniBonkCapture+RECT
    [OmniBonkCapture]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    # 2 = PW_RENDERFULLCONTENT, needed for a DirectX window.
    [OmniBonkCapture]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $name = if ($p.Path -like "$HostPath*") { "host" } else { "client" }
    $file = Join-Path $OutDir "$name-$stamp.png"
    $bmp.Save($file)
    $g.Dispose(); $bmp.Dispose()
    $file
}
