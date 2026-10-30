# Builds a clean release zip for Nexus Mods / Vortex:
#   dist\MoonSweepersTwitch-<version>.zip
#     MoonSweepersTwitch/MoonSweepersTwitch.dll
#     MoonSweepersTwitch/LICENSE.txt
#
# The Moon Sweepers Vortex extension installs a folder holding a BepInEx plugin DLL to
# BepInEx\plugins\<folder name>. BepInEx and the patcher are separate downloads, never
# bundled here (Vortex would treat the whole archive as BepInEx). Nothing from the local
# install goes in: no config, no Twitch tokens, no Client ID.
#
# Usage (from the repo root):  powershell -ExecutionPolicy Bypass -File tools\package.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\MoonSweepersTwitch.csproj'

$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
& dotnet build $project -c Release -nologo -p:SkipDeploy=true
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was packaged.' }

$info = Get-Content (Join-Path $root 'src\PluginInfo.cs') -Raw
$version = [regex]::Match($info, 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Could not read the version from src\PluginInfo.cs.' }

$dll = Join-Path $root 'src\bin\Release\MoonSweepersTwitch.dll'
if (-not (Test-Path $dll)) { throw "Built DLL not found at $dll." }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "MoonSweepersTwitch-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }

# Write entries by hand so their paths use forward slashes on every PowerShell version.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    $files = @(
        @{ Source = $dll; Entry = 'MoonSweepersTwitch/MoonSweepersTwitch.dll' },
        @{ Source = (Join-Path $root 'LICENSE'); Entry = 'MoonSweepersTwitch/LICENSE.txt' }
    )
    foreach ($f in $files) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.Source, $f.Entry) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Packaged $zip"
