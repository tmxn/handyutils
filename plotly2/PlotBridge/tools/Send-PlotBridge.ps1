<#
.SYNOPSIS
  Push a series to a running PlotBridge server.

.DESCRIPTION
  Builds the JSON by hand rather than via ConvertTo-Json, for two reasons:
  Windows PowerShell 5.1 serialises a strongly-typed array (double[], int[])
  as {"value":[...],"Count":n} instead of a bare JSON array, and its default
  number formatting follows the current culture — so on a comma-decimal locale
  you would emit 1,5 and the server would read two numbers. Everything here is
  formatted with InvariantCulture and "R" round-trip precision.

.EXAMPLE
  .\Send-PlotBridge.ps1 -Series 'hull' -X $xs -Y $ys

.EXAMPLE
  # y against its index
  .\Send-PlotBridge.ps1 -Series 'residuals' -Y $errors -Chart 'signal'

.EXAMPLE
  # anything text-shaped, including raw debugger "Copy Value" output
  Get-Content dump.txt -Raw | .\Send-PlotBridge.ps1 -Series 'pts'
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Series,
  [double[]] $X,
  [double[]] $Y,
  [double[]] $Z,
  [Parameter(ValueFromPipeline)] [string] $Text,

  [string] $Board = 'default',
  [string] $Chart = 'main',
  [ValidateSet('auto', '2d', '3d')] [string] $Mode,
  [ValidateSet('markers', 'lines', 'lines+markers')] [string] $DrawMode,
  [double] $Size,
  [string] $Color,
  [switch] $Add,                  # append as a new series instead of replacing
  [int] $Port = 8777
)

begin {
  $inv = [System.Globalization.CultureInfo]::InvariantCulture
  $chunks = New-Object System.Collections.Generic.List[string]

  function Format-Array([double[]] $values) {
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $values.Length; $i++) {
      if ($i -gt 0) { [void]$sb.Append(',') }
      [void]$sb.Append($values[$i].ToString('R', $inv))
    }
    $sb.ToString()
  }

  function Escape-Json([string] $s) {
    $s.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
  }
}

process {
  if ($Text) { $chunks.Add($Text) }
}

end {
  $fields = New-Object System.Collections.Generic.List[string]
  $fields.Add('"board":"'  + (Escape-Json $Board)  + '"')
  $fields.Add('"chart":"'  + (Escape-Json $Chart)  + '"')
  $fields.Add('"series":"' + (Escape-Json $Series) + '"')
  $fields.Add('"replace":' + $(if ($Add) { 'false' } else { 'true' }))
  if ($Mode) { $fields.Add('"mode":"' + $Mode + '"') }

  $style = New-Object System.Collections.Generic.List[string]
  if ($DrawMode)             { $style.Add('"mode":"' + $DrawMode + '"') }
  if ($PSBoundParameters.ContainsKey('Size')) { $style.Add('"size":' + $Size.ToString('R', $inv)) }
  if ($Color)                { $style.Add('"color":"' + (Escape-Json $Color) + '"') }
  if ($style.Count) { $fields.Add('"style":{' + ($style -join ',') + '}') }

  if ($Y -and $Y.Length) {
    if ($X -and $X.Length) { $fields.Add('"x":[' + (Format-Array $X) + ']') }
    $fields.Add('"y":[' + (Format-Array $Y) + ']')
    if ($Z -and $Z.Length)  { $fields.Add('"z":[' + (Format-Array $Z) + ']') }
  }
  elseif ($X -and $X.Length) {
    # Only one array given: treat it as values against index.
    $fields.Add('"values":[' + (Format-Array $X) + ']')
  }
  elseif ($chunks.Count) {
    $fields.Add('"text":"' + (Escape-Json ($chunks -join "`n")) + '"')
  }
  else {
    throw 'Nothing to send: supply -X/-Y, or -Y alone, or pipe text in.'
  }

  $json = '{' + ($fields -join ',') + '}'
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

  try {
    $resp = Invoke-RestMethod -Uri "http://localhost:$Port/push" -Method Post `
      -ContentType 'application/json' -Body $bytes
    Write-Verbose "$Board/$Chart/$Series : $($resp.message)"
    $resp
  }
  catch [System.Net.WebException] {
    $r = $_.Exception.Response
    if ($r) {
      $reader = New-Object IO.StreamReader($r.GetResponseStream())
      throw "PlotBridge rejected the push: $($reader.ReadToEnd())"
    }
    throw "PlotBridge is not answering on port $Port. Start the server first."
  }
}
