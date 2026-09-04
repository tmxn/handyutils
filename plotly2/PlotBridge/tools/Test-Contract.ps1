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

  # The CAM containers (IActionCollection, FFeature) emit two lines per element -
  # a rawbegin view then a rawend one - so a chain of segments arrives with the
  # shared vertices doubled. Harmless to the polyline, but the count must not
  # surprise anyone reading it: two entities, four lines, four points.
  @{ n = 'CAM rawbegin/rawend pair, two lines per element'; series = 'c12'
     body = "1.5 `t 2.25 `t 0`n3.5 `t 2.25 `t 0`n3.5 `t 2.25 `t 0`n3.5 `t 6 `t 0"
     want = @(@(1.5, 2.25, 0), @(3.5, 2.25, 0), @(3.5, 2.25, 0), @(3.5, 6, 0)) }

  # FAction and FEntity define this fallback for both raw views, so an action
  # with no position (dwell, THC, set-RPM) contributes nothing. It is load
  # bearing that the parser treats a leading '#' as a comment.
  @{ n = 'CAM "# no geometry" fallback is skipped'; series = 'c13'
     body = "# no geometry`n1.5 `t 2.25 `t -3.5`n# no geometry`n4 `t 5.5 `t 6"
     want = @(@(1.5, 2.25, -3.5), @(4, 5.5, 6)) }

  # A null element in the vector: the chart node guards it with a "#" literal,
  # which the debugger renders with its quotes, so it is not a comment - it
  # survives only because it carries no digits at all.
  @{ n = 'CAM null-element placeholder yields no point'; series = 'c14'
     body = "$([char]34)#$([char]34)`n1.5 `t 2.25 `t -3.5"
     want = , @(1.5, 2.25, -3.5) }

  # FPlotDump writes a header line naming the series and its point count. It has
  # numbers in it, so it only stays out of the data because of the '#'.
  @{ n = 'FPlotDump header comment is skipped, numbers and all'; series = 'c15'
     body = "# toolpath / cut  1234 points`n1.5`t2.25`t-3.5"
     want = , @(1.5, 2.25, -3.5) }

  # Regression, found live. A vtable pointer read off a polymorphic object used to
  # plot as the digit runs in its own address - 0x00007ff748... became (0, 7, 748)
  # for every row, and moved between sessions as the DLL rebased. A pointer is not
  # a coordinate, so hex literals are dropped before the numbers are scanned.
  @{ n = 'a pointer value yields no point'; series = 'c16'
     body = "0x00007ff748e51230`n1.5`t2.25`t-3.5"
     want = , @(1.5, 2.25, -3.5) }

  # The same address in the shape the older build actually pushed: 46 identical
  # rows and nothing else. Wrong data must become NO data, not a flat line.
  @{ n = 'a whole series of pointers yields nothing at all'; series = 'c17'
     body = "0x00007ff6a1b2c3d0`n0x00007ff6a1b2c3d0`n0x00007ff6a1b2c3d0"
     want = @() }

  # Dropping the hex must not eat a real coordinate that happens to sit beside one.
  @{ n = 'hex dropped, decimals beside it kept'; series = 'c18'
     body = "0x00007ff748e51230 -0.035038461538460908 `t 0.42999999999999960 `t -0.89370078740157477"
     want = , @(-0.035038461538460908, 0.42999999999999960, -0.89370078740157477) }

  # A DOCUMENTED LIMIT, pinned deliberately. The full vtable rendering carries a
  # digit inside its type annotation - the 9 in "void(*[9])()" - and a lone number
  # on a line is a legitimate value-vs-index point (that is case c5). Telling those
  # apart would mean guessing which digits are data, which is exactly what the
  # tolerant paste path must not do. So this one is stopped on the client instead:
  # DebuggerPoints.IsPlottableChild drops a child named __vfptr before it is ever
  # pushed. If a future parser change makes this yield nothing, that is an
  # improvement - update this case.
  @{ n = 'vtable type annotation still leaks its digit (client-side filter covers it)'; series = 'c19'
     body = "0x00007ff748e51230 {CAMEngine.dll!void(*[9])()}"
     want = , @(0, 9) }
)

# A case with no plottable numbers at all is refused by /push rather than landing
# as an empty series - which is the point of such a case, so the rejection is the
# assertion. rejected = $true records that it happened.
$rejected = @{}
foreach ($c in $cases) {
  try {
    $null = Invoke-RestMethod -Uri "$base/push?board=$board&chart=main&series=$($c.series)" `
              -Method Post -ContentType 'text/plain' -Body $c.body
  }
  catch { $rejected[$c.series] = $true }
}
Start-Sleep -Milliseconds 300

$byName = @{}
foreach ($s in (Invoke-RestMethod -Uri "$base/snapshot?board=$board").charts[0].series) { $byName[$s.name] = $s }

$pass = 0; $fail = 0
foreach ($c in $cases) {
  $s = $byName[$c.series]
  $ok = $true; $detail = ''

  if ($c.want.Count -eq 0) {
    # Nothing plottable: /push must refuse it, and no series may appear.
    if (-not $rejected[$c.series]) { $ok = $false; $detail = 'expected /push to refuse it, but it was accepted' }
    elseif ($s) { $ok = $false; $detail = 'refused, but a series appeared anyway' }
  }
  elseif (-not $s) { $ok = $false; $detail = 'series missing' }
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
