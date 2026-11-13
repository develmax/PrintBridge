param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/PrintBridge.App/PrintBridge.App.csproj'
$output = Join-Path $root "artifacts/publish/$Runtime"
dotnet publish $project -c Release -r $Runtime --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:UseSharedCompilation=false "-p:PrintBridgeSignedRelease=$($Sign.IsPresent.ToString().ToLowerInvariant())" -m:1 -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$assets = Get-Content -Raw (Join-Path (Split-Path $project) 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
$packages = @(
    $assets.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' } |
        ForEach-Object { $_.Value.path }
    $assets.project.frameworks.Values | ForEach-Object { $_.downloadDependencies } |
        Where-Object { $_.name -in "Microsoft.NETCore.App.Runtime.$Runtime", "Microsoft.WindowsDesktop.App.Runtime.$Runtime" } |
        ForEach-Object { "$($_.name.ToLowerInvariant())/$((($_.version -split ',')[0]).Trim('[', ']', ' '))" }
)
$notices = Join-Path $output 'ThirdParty'
foreach ($package in $packages) {
    $directory = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $package } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $directory) { throw "Package directory not found for $package" }
    $destination = Join-Path $notices $package
    New-Item -ItemType Directory -Force $destination | Out-Null
    Get-ChildItem -LiteralPath $directory -File |
        Where-Object Name -Match '^(LICENSE|NOTICE|THIRD.PARTY|PACKAGE-LICENSE)' |
        Copy-Item -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
