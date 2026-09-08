<#
  Builds the redistributable package: everything a machine needs to debug and profile a
  .NET for Android app, without a clone of this repository and without a dotnet SDK beyond
  the runtime the tools need (building an app for profiling needs the SDK, as always).

      powershell -File build\package.ps1                 Release package into dist\
      powershell -File build\package.ps1 -SkipDsRouter   no network: leave tools\ empty
      powershell -File build\package.ps1 -SkipGui        do not look for the Delphi GUI

  Layout of the package (see docs/PACKAGING.md):

      bin\      the unified MCP server (debugger + profiler), the profiler's own MCP server,
                nap.exe (control service + one-shot commands), nap-weave
      tools\    dotnet-dsrouter, so the install does not depend on a global tool
      build\    NetAndroidProfiler.Weaving.targets and the copy of nap-weave it runs
      gui\      NapGui.exe, when the Delphi GUI has been built
      .claude-plugin\, .mcp.json, skills\   the Claude Code plugin: the unpacked folder is one
      install.cmd, README.txt, LICENSE, THIRD-PARTY-NOTICES.txt
#>
param(
  [string]$Configuration = 'Release',
  [string]$OutputDir = '',
  [string]$DsRouterVersion = '9.0.661903',
  [switch]$SkipDsRouter,
  [switch]$SkipGui
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'dist' }

# The version lives in Directory.Build.props and nowhere else.
$props = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
if ($props -notmatch '<NapVersion>([^<]+)</NapVersion>') { throw 'NapVersion not found in Directory.Build.props' }
$version = $Matches[1]
$name = "net-android-$version"
$staging = Join-Path $OutputDir $name

Write-Host "packaging $name ($Configuration)"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

function Publish([string]$project, [string]$into) {
  Write-Host "  publish $project"
  & dotnet publish (Join-Path $repo $project) -c $Configuration -o $into --nologo -v:m | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

# The servers, nap and nap-weave share a folder: their dependency closures overlap almost
# entirely, and one folder is one thing to put on a PATH. The unified server is the one the
# plugin registers; the profiler's own server stays for whoever wants only that.
$bin = Join-Path $staging 'bin'
Publish 'src\NetAndroid.Mcp\NetAndroid.Mcp.csproj' $bin
Publish 'src\NetAndroidProfiler.Mcp\NetAndroidProfiler.Mcp.csproj' $bin
Publish 'src\NetAndroidProfiler.Cli\NetAndroidProfiler.Cli.csproj' $bin
Publish 'src\NetAndroidProfiler.Weave\NetAndroidProfiler.Weave.csproj' $bin

# Build-time weaving is driven from an app's own build, so the targets file and the copy
# of nap-weave it runs travel together.
$msbuild = Join-Path $staging 'build'
New-Item -ItemType Directory -Force -Path (Join-Path $msbuild 'tools') | Out-Null
Copy-Item (Join-Path $repo 'build\NetAndroidProfiler.Weaving.targets') $msbuild
Publish 'src\NetAndroidProfiler.Weave\NetAndroidProfiler.Weave.csproj' (Join-Path $msbuild 'tools')

# dsrouter is a Microsoft tool we drive as a process; shipping it removes the one
# install step a user would otherwise have to remember. ToolLocator prefers this copy.
$tools = Join-Path $staging 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
if ($SkipDsRouter) {
  Write-Host '  dsrouter: skipped'
}
else {
  Write-Host "  dsrouter $DsRouterVersion"
  & dotnet tool install dotnet-dsrouter --tool-path $tools --version $DsRouterVersion 2>&1 | Out-Null
  if (-not (Test-Path (Join-Path $tools 'dotnet-dsrouter.exe'))) {
    Write-Warning 'dotnet-dsrouter could not be fetched; the package will fall back to a global tool.'
  }
  else {
    # The tool store keeps the nupkg it was installed from next to the files it
    # extracted. The shim needs the files, nobody needs the archive: it is a third of
    # the package for nothing.
    Get-ChildItem $tools -Recurse -Filter *.nupkg | Remove-Item -Force
  }
}

if (-not $SkipGui) {
  $gui = Join-Path $repo 'gui\NapGui.exe'
  if (Test-Path $gui) {
    New-Item -ItemType Directory -Force -Path (Join-Path $staging 'gui') | Out-Null
    Copy-Item $gui (Join-Path $staging 'gui')
    Write-Host '  gui: NapGui.exe'
  }
  else {
    Write-Warning 'gui\NapGui.exe not found: build it with gui\build-gui.cmd to include it.'
  }
}

# The plugin files make the unpacked folder a Claude Code plugin (the skill and the server
# registration); they are copied as they are, the skill is tested from the repository.
Copy-Item (Join-Path $repo 'plugin\*') $staging -Recurse -Force
# The plugin's version is the package's: stamped here, so the repository copy carries none.
$pluginJson = Join-Path $staging '.claude-plugin\plugin.json'
$plugin = Get-Content $pluginJson -Raw | ConvertFrom-Json
$plugin | Add-Member -NotePropertyName version -NotePropertyValue $version -Force
[IO.File]::WriteAllText($pluginJson, ($plugin | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.txt') $staging
Copy-Item (Join-Path $repo 'LICENSE') $staging
Copy-Item (Join-Path $PSScriptRoot 'package-files\install.cmd') $staging
(Get-Content (Join-Path $PSScriptRoot 'package-files\README.txt') -Raw).Replace('@VERSION@', $version) |
  Set-Content (Join-Path $staging 'README.txt') -Encoding UTF8

$zip = Join-Path $OutputDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $staging -DestinationPath $zip
Write-Host "wrote $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
