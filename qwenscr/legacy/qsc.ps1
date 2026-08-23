param([Parameter(Position=0)][string]$out,
      [Parameter(Position=1)][int]$procpid = 6716,
      [Parameter(Position=2)][int]$x = 0,
      [Parameter(Position=3)][int]$y = 0,
      [Parameter(Position=4)][int]$w = 0,
      [Parameter(Position=5)][int]$h = 0)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
public static class WinApi {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
"@
$p = Get-Process -Id $procpid
$hwnd = $p.MainWindowHandle
$rect = New-Object WinApi+RECT
[void][WinApi]::GetWindowRect($hwnd, [ref]$rect)
$left   = [int]$rect.Left
$top    = [int]$rect.Top
$right  = [int]$rect.Right
$bottom = [int]$rect.Bottom
"W=$($right-$left) H=$($bottom-$top) X=$left Y=$top"
$rw = $right - $left
$rh = $bottom - $top
if ($w -gt 0 -and $h -gt 0) { $left += $x; $top += $y; $rw = $w; $rh = $h }
$bmp = New-Object System.Drawing.Bitmap($rw, $rh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size($rw, $rh)))
$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"SAVED $out ${rw}x${rh}"
