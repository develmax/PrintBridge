param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'PrintBridge.sln') -c $Configuration -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
dotnet test (Join-Path $root 'PrintBridge.sln') -c $Configuration --no-build -m:1
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
