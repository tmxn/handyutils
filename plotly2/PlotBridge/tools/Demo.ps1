<#
.SYNOPSIS
  Pushes sample data through every input path PlotBridge accepts.

.DESCRIPTION
  Doubles as the smoke test: exercises columnar JSON, the points[] form,
  values-only, text/plain with raw debugger output, the drop folder, and the
  cross-origin guard. Run it with the server already up.

.EXAMPLE
  .\Demo.ps1
#>
[CmdletBinding()]
param([int] $Port = 8777)

$ErrorActionPreference = 'Stop'
$send = Join-Path $PSScriptRoot 'Send-PlotBridge.ps1'
$base = "http://localhost:$Port"
$inv  = [cultureinfo]::InvariantCulture

try { Invoke-RestMethod "$base/health" | Out-Null }
catch { throw "No PlotBridge on port $Port. Start it with: dotnet run --project PlotBridge/server" }

# 1 — 5000-point 2D spiral, columnar JSON
$n = 5000; $x = New-Object double[] $n; $y = New-Object double[] $n
for ($i = 0; $i -lt $n; $i++) { $t = $i * 0.01; $x[$i] = $t * [Math]::Cos($t); $y[$i] = $t * [Math]::Sin($t) }
'1. spiral 5k   -> ' + (& $send -Series 'spiral 5k' -X $x -Y $y -Mode 2d -Port $Port).message

# 2 — a second series on the same chart: check it gets its own slot AND symbol
$n2 = 400; $x2 = New-Object double[] $n2; $y2 = New-Object double[] $n2
for ($i = 0; $i -lt $n2; $i++) { $t = $i * 0.125; $x2[$i] = $t * [Math]::Cos($t) * 1.15; $y2[$i] = $t * [Math]::Sin($t) * 1.15 }
'2. outer       -> ' + (& $send -Series 'outer' -X $x2 -Y $y2 -DrawMode 'lines+markers' -Size 4 -Port $Port).message

# 3 — raw Visual Studio "Copy Value" output, posted as text/plain
$lines = @()
for ($i = 0; $i -lt 60; $i++) {
  $t = $i * 0.1
  $lines += ('[{0}] {{X={1} Y={2}}}' -f $i, ($t*2).ToString('R',$inv), ([Math]::Sin($t)*8).ToString('R',$inv))
}
$r = Invoke-RestMethod -Uri "$base/push?chart=main&series=from%20debugger" -Method Post `
       -ContentType 'text/plain' -Body ($lines -join "`n")
'3. debug text  -> ' + $r.message

# 4 — 3D helix on its own chart
$n3 = 3000; $x3 = New-Object double[] $n3; $y3 = New-Object double[] $n3; $z3 = New-Object double[] $n3
for ($i = 0; $i -lt $n3; $i++) {
  $t = $i * 0.02
  $x3[$i] = [Math]::Cos($t) * (10 + $t*0.15); $y3[$i] = [Math]::Sin($t) * (10 + $t*0.15); $z3[$i] = $t * 1.5
}
'4. helix 3d    -> ' + (& $send -Series 'helix' -X $x3 -Y $y3 -Z $z3 -Chart 'helix' -Mode 3d -Port $Port).message

# 5 — points[] array-of-rows form
$rows = @(); for ($i = 0; $i -lt 50; $i++) { $rows += ('[{0},{1},{2}]' -f $i, ($i*$i/25.0), ($i*0.5)) }
$json = '{"chart":"helix","series":"parabola 3d","points":[' + ($rows -join ',') + ']}'
'5. points[]    -> ' + (Invoke-RestMethod -Uri "$base/push" -Method Post -ContentType 'application/json' -Body $json).message

# 6 — values only: y against index, and the chart starts with equal aspect off
$vals = New-Object double[] 80
for ($i = 0; $i -lt 80; $i++) { $vals[$i] = [Math]::Sin($i*0.2)*5 + $i*0.05 }
'6. values      -> ' + (& $send -Series 'y vs index' -Y $vals -Chart 'signal' -Port $Port).message

# 7 — drop folder: filename picks board__chart__series
$drop = Join-Path $env:LOCALAPPDATA 'PlotBridge\drop'
$tsv = (0..199 | ForEach-Object {
  ($_*0.05).ToString('R',$inv) + "`t" + ([Math]::Cos($_*0.05)*3).ToString('R',$inv)
}) -join "`n"
Set-Content -Path (Join-Path $drop 'signal__cosine.tsv') -Value $tsv -Encoding utf8
'7. drop file   -> signal__cosine.tsv written to ' + $drop

# 8 — a cross-origin POST must be refused
try {
  Invoke-RestMethod -Uri "$base/push?series=evil" -Method Post -ContentType 'text/plain' `
    -Headers @{ Origin = 'https://evil.example' } -Body "1`t2" | Out-Null
  '8. origin      -> NOT BLOCKED (regression!)'
} catch {
  '8. origin      -> refused with ' + [int]$_.Exception.Response.StatusCode
}

# 9 — what the server would restore on reconnect
Start-Sleep -Milliseconds 400
$snap = Invoke-RestMethod -Uri "$base/snapshot?board=default"
'9. snapshot    -> ' + (($snap.charts | ForEach-Object { $_.name + '(' + @($_.series).Count + ')' }) -join '  ')
