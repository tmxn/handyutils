<#
.SYNOPSIS
  Validates a .natvis file against Visual Studio's own natvis schema.

.DESCRIPTION
  A natvis file that fails schema validation is silently ignored by the debugger -
  no error, no warning, the visualizer simply never appears. That makes this worth
  running after any edit.

  Two constraints catch people out, and both are schema violations rather than
  anything the debugger complains about:

    * <UIVisualizer> must be the LAST child of <Type>, after <DisplayString>
      and <Expand>.
    * A <Type> containing <UIVisualizer> cannot also contain <DisplayString>,
      <Expand>, <StringView> and friends. natvis.xsd defines the body of <Type>
      as an xs:choice between the visualization elements and one or more
      <UIVisualizer> elements - never both. Registering a visualizer therefore
      does not, and cannot, restate how the type is displayed.

  With no arguments, checks both the copy in this repo and the deployed one.

.EXAMPLE
  .\Test-Natvis.ps1

.EXAMPLE
  .\Test-Natvis.ps1 -Path C:\my\other.natvis
#>
[CmdletBinding()]
param([string[]] $Path)

$ErrorActionPreference = 'Stop'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found; is Visual Studio installed?" }
$vsRoot = & $vswhere -latest -property installationPath
if (-not $vsRoot) { throw "No Visual Studio installation found." }

$xsd = Join-Path $vsRoot 'Xml\Schemas\1033\natvis.xsd'
if (-not (Test-Path $xsd)) { throw "natvis.xsd not found at $xsd" }

if (-not $Path) {
  $Path = @(
    (Join-Path (Split-Path $PSScriptRoot -Parent) 'vsix\PlotBridge.natvis'),
    (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Visual Studio 2022\Visualizers\PlotBridge.natvis')
  )
}

$failed = 0
foreach ($file in $Path) {
  if (-not (Test-Path $file)) { "SKIP  $file (not present)"; continue }

  $errors = New-Object System.Collections.Generic.List[string]
  $settings = New-Object System.Xml.XmlReaderSettings
  $settings.ValidationType = [System.Xml.ValidationType]::Schema
  [void]$settings.Schemas.Add("http://schemas.microsoft.com/vstudio/debugger/natvis/2010", $xsd)
  $settings.add_ValidationEventHandler(
    [System.Xml.Schema.ValidationEventHandler]{ param($s, $e) $errors.Add($e.Message) })

  try {
    $reader = [System.Xml.XmlReader]::Create($file, $settings)
    while ($reader.Read()) { }
    $reader.Close()
  }
  catch {
    $errors.Add("not well-formed XML: " + $_.Exception.Message)
  }

  if ($errors.Count -eq 0) {
    $types = ([regex]::Matches((Get-Content $file -Raw), '<Type Name=')).Count
    "OK    $file  ($types Type entries)"
  }
  else {
    $failed++
    "FAIL  $file"
    $errors | Select-Object -First 6 | ForEach-Object { "        $_" }
    if ($errors.Count -gt 6) { "        ... and $($errors.Count - 6) more" }
  }
}

if ($failed -gt 0) { exit 1 }
