# Writes NapGui.cfg from the IDE's Win64 library path: DevExpress and SynEdit are
# used from source there, so the compiler needs the same folders the IDE uses.
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'dcu') | Out-Null
$key = 'HKCU:\SOFTWARE\Embarcadero\BDS\23.0\Library\Win64'
$searchPath = (Get-ItemProperty $key -Name 'Search Path').'Search Path'
$bds = (Get-ItemProperty 'HKCU:\SOFTWARE\Embarcadero\BDS\23.0' -Name 'RootDir').RootDir.TrimEnd('\')
$searchPath = $searchPath -replace '\$\(BDS\)', $bds
$own = Join-Path $PSScriptRoot 'src'
@"
-U"$own;$searchPath"
-I"$own;$searchPath"
-R"$own;$searchPath"
-NSSystem;Winapi;Vcl;Data;FireDAC;Vcl.Imaging;System.Win
-E"$PSScriptRoot"
-N0"$PSScriptRoot\dcu"
-\$O+
-W-SYMBOL_PLATFORM
"@ | Set-Content -Path (Join-Path $PSScriptRoot 'NapGui.cfg') -Encoding ASCII
