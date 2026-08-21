<#
  Opens every panel and every dialog of the GUI against real session databases and
  fails if any of them writes a startup error log.

  There is no headless test framework for a VCL application, and the crashes worth
  catching here - a panel that touches a control before it exists, a query that does not
  match the schema - all happen while the window is being built. Starting the process
  once per panel finds them, and it is the same switch a screenshot uses.

    powershell -File smoke.ps1 -Exe ..\NapGui.exe -Sessions a\session.db,b\session.db
#>
param(
  [string]$Exe = '',
  [Parameter(Mandatory = $true)][string[]]$Sessions,
  [int]$SettleSeconds = 5
)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\NapGui.exe' }
$exePath = (Resolve-Path $Exe).Path
$logPath = [System.IO.Path]::ChangeExtension($exePath, '.error.log')
$failures = 0

# Messages go to the host, not to the output stream: inside a function the output
# stream is the return value, and a Write-Output here would be read as the verdict.
function Invoke-Gui([string[]]$Arguments, [string]$What) {
  Remove-Item $logPath -ErrorAction SilentlyContinue
  $process = Start-Process -FilePath $exePath -ArgumentList $Arguments -PassThru
  Start-Sleep -Seconds $SettleSeconds
  $died = $process.HasExited
  Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
  if (Test-Path $logPath) {
    Write-Host "  FAIL  $What"
    Get-Content $logPath | ForEach-Object { Write-Host "        $_" }
    return $false
  }
  if ($died) {
    Write-Host "  FAIL  $What (the process exited on its own)"
    return $false
  }
  Write-Host "  ok    $What"
  return $true
}

foreach ($session in $Sessions) {
  $db = (Resolve-Path $session).Path
  Write-Host "session: $db"
  foreach ($tab in @('report', 'tree', 'graph', 'source', 'summary', 'memory', 'monitor')) {
    if (-not (Invoke-Gui @("`"$db`"", "--tab=$tab") "panel $tab")) { $failures++ }
  }
  foreach ($dialog in @('settings', 'layouts')) {
    if (-not (Invoke-Gui @("`"$db`"", "--dialog=$dialog") "dialog $dialog")) { $failures++ }
  }

  # The export path runs to completion instead of being killed, so it is checked by its
  # output rather than by the absence of a crash.
  foreach ($extension in @('csv', 'xlsx')) {
    $out = Join-Path ([System.IO.Path]::GetTempPath()) "nap-smoke.$extension"
    Remove-Item $out -ErrorAction SilentlyContinue
    Start-Process -FilePath $exePath -ArgumentList @("`"$db`"", "--export=$out") -Wait
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) {
      Write-Host "  ok    export $extension ($((Get-Item $out).Length) bytes)"
    }
    else {
      Write-Host "  FAIL  export $extension produced nothing"
      $failures++
    }
    Remove-Item $out -ErrorAction SilentlyContinue
  }
  Write-Output ''
}

if ($failures -eq 0) {
  Write-Output 'all checks passed'
  exit 0
}
Write-Output "$failures checks FAILED"
exit 1
