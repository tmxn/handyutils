<#
.SYNOPSIS
  Checks that the strings the debugger produces still parse into the right points.

.DESCRIPTION
  The extension does not decode memory layouts. It reads each element's formatted
  value string out of the debugger and posts those lines verbatim; the server turns
  them into coordinates. That makes one assumption load-bearing: a gp_Pnt (or
  whatever) renders with its numbers visible in the Value column.

  This pins that assumption down. Each case is a value string Visual Studio is
  likely to produce, with the coordinates it must decode to. If the server's text
  parser is ever tightened, or OCCT ships a natvis that changes how gp_Pnt
  displays, this is what catches it.

  Run with the server up. Requires no debugger and no extension.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File PlotBridge\tools\Test-Contract.ps1
#>
[CmdletBinding()]
param([int] $Port = 8777)

$ErrorActionPreference = 'Stop'
$base = "http://localhost:$Port"
$board = 'contract-test'

try { Invoke-RestMethod "$base/health" | Out-Null }
catch { throw "No PlotBridge on port $Port. Start it with: dotnet run --project PlotBridge/server" }

# want: one entry per expected point. Note the unary comma on single-point cases -
# without it PowerShell flattens a one-element array of arrays.
$cases = @(
  @{ n = 'gp_Pnt, default struct summary'; series = 'c1'
     body = "{coord={x=1.5 y=2.25 z=-3.5 } }`n{coord={x=4 y=5.5 z=6 } }"
     want = @(@(1.5, 2.25, -3.5), @(4, 5.5, 6)) }

  @{ n = 'gp_XYZ, members shown directly'; series = 'c2'
     body = "{x=1.5 y=2.25 z=-3.5}`n{x=4 y=5.5 z=6}"
     want = @(@(1.5, 2.25, -3.5), @(4, 5.5, 6)) }

  @{ n = 'natvis DisplayString, comma separated'; series = 'c3'
     body = "1.5, 2.25, -3.5`n4, 5.5, 6"
     want = @(@(1.5, 2.25, -3.5), @(4, 5.5, 6)) }

  @{ n = 'gp_Pnt2d, two numbers per element'; series = 'c4'
     body = "{coord={x=1.5 y=2.25 } }`n{coord={x=4 y=5.5 } }"
     want = @(@(1.5, 2.25), @(4, 5.5)) }

  @{ n = 'vector<double>, bare scalars -> y vs index'; series = 'c5'
     body = "1.5`n2.25`n-3.5"
     want = @(@(0, 1.5), @(1, 2.25), @(2, -3.5)) }

  @{ n = 'full 17-digit doubles, as VS prints them'; series = 'c6'
     body = "{coord={x=0.10000000000000001 y=-2.2999999999999998 z=3.1415926535897931 } }"
     want = , @(0.10000000000000001, -2.2999999999999998, 3.1415926535897931) }

  @{ n = 'scientific notation, both exponent signs'; series = 'c7'
     body = "{coord={x=1.2340000000000001e-05 y=-6.7889999999999999e+21 z=0 } }"
     want = , @(1.2340000000000001e-05, -6.7889999999999999e+21, 0) }

  @{ n = 'element index prefix is not a coordinate'; series = 'c8'
     body = "[0] {coord={x=7.5 y=8.25 z=9 } }`n[1] {coord={x=10 y=11 z=12 } }"
     want = @(@(7.5, 8.25, 9), @(10, 11, 12)) }

  # What a [chart3d] synthetic yields: FCCore.natvis writes "{x} <tab> {y} <tab>
  # {z}", which leaves a space either side of each tab.
  @{ n = '[chart3d] view, tab separated with spaces'; series = 'c9'
     body = "1.5 `t 2.25 `t -3.5`n4 `t 5.5 `t 6"
     want = @(@(1.5, 2.25, -3.5), @(4, 5.5, 6)) }

  @{ n = '[chart2d] view, two columns'; series = 'c10'
     body = "1.5 `t 2.25`n4 `t 5.5"
     want = @(@(1.5, 2.25), @(4, 5.5)) }

  # What the deep-scan fallback emits: each opaque element expanded and its
  # numeric children joined with tabs.
  @{ n = 'deep-scan join of expanded members'; series = 'c11'
     body = "1.5`t2.25`t-3.5`n4`t5.5`t6"
     want = @(@(1.5, 2.25, -3.5), @(4, 5.5, 6)) }
)

foreach ($c in $cases) {
  $null = Invoke-RestMethod -Uri "$base/push?board=$board&chart=main&series=$($c.series)" `
            -Method Post -ContentType 'text/plain' -Body $c.body
}
Start-Sleep -Milliseconds 300

$byName = @{}
foreach ($s in (Invoke-RestMethod -Uri "$base/snapshot?board=$board").charts[0].series) { $byName[$s.name] = $s }

$pass = 0; $fail = 0
foreach ($c in $cases) {
  $s = $byName[$c.series]
  $ok = $true; $detail = ''

  if (-not $s) { $ok = $false; $detail = 'series missing' }
  elseif (@($s.y).Count -ne $c.want.Count) { $ok = $false; $detail = "got $(@($s.y).Count) points, wanted $($c.want.Count)" }
  else {
    for ($i = 0; $i -lt $c.want.Count -and $ok; $i++) {
      $w = $c.want[$i]
      $got = @([double]$s.x[$i], [double]$s.y[$i])

      # $null -ne, not truthiness: a z array of @(0.0) unwraps to 0, which is falsy.
      if ($w.Count -ge 3) {
        if ($null -eq $s.z) { $ok = $false; $detail = 'expected a z axis, got none'; break }
        $got += [double]$s.z[$i]
      }
      elseif ($null -ne $s.z) { $ok = $false; $detail = 'got an unexpected z axis'; break }

      for ($k = 0; $k -lt $w.Count; $k++) {
        $tol = [Math]::Max([Math]::Abs($w[$k]) * 1e-15, 1e-300)
        if ([Math]::Abs($got[$k] - $w[$k]) -gt $tol) {
          $ok = $false; $detail = "point $i axis $k : got $($got[$k]), wanted $($w[$k])"; break
        }
      }
    }
  }

  if ($ok) { $pass++; "PASS  $($c.n)" } else { $fail++; "FAIL  $($c.n)`n        $detail" }
}

$null = Invoke-RestMethod -Uri "$base/clear?board=$board" -Method Post
""
"$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
