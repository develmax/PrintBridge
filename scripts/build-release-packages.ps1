param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$Wix = $env:PRINTBRIDGE_WIX,
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Wix) {
    $localTool = Join-Path $root 'artifacts/tools/wix/wix.exe'
    $Wix = if (Test-Path $localTool) { $localTool } else { (Get-Command wix -ErrorAction Stop).Source }
}
$exe = & (Join-Path $PSScriptRoot 'build-installer.ps1') -Runtime $Runtime -Sign:$Sign |
    Where-Object { $_ -is [System.IO.FileInfo] } | Select-Object -Last 1
if (-not $exe) { throw 'The EXE installer is missing' }
$version = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$arch = $Runtime.Substring(4)
if ($arch -eq 'x64') {
    $upgrade = '8A66D25F-3690-4B77-9679-3CD558842C5F'
    $app = '9446FB4D-E1D2-4CEC-90E2-4C419752F96F'
    $shortcut = '383FB352-73F5-46A1-8CDE-730CD3F90564'
} else {
    $upgrade = '9362BF7B-4E04-4C2F-B71A-41DF968DE6C0'
    $app = 'E0404211-3F38-4C55-BF0F-41C1E58258B7'
    $shortcut = '4A216895-D46B-47F9-BAFC-366262AFD207'
}
$dist = New-Item -ItemType Directory -Force (Join-Path $root "artifacts/dist/$Runtime")
$base = Join-Path $dist.FullName $exe.BaseName
$publish = Join-Path $root "artifacts/publish/$Runtime"
Copy-Item -LiteralPath $exe.FullName -Destination "$base.exe" -Force
Compress-Archive -LiteralPath "$base.exe" -DestinationPath "$base.zip" -Force
Push-Location (Join-Path $root 'installer')
try {
    & $Wix build -arch $arch -define "Arch=$arch" -define "BuildVersion=$version" -define "PublishDir=$publish" -define "UpgradeCode=$upgrade" -define "AppComponent=$app" -define "ShortcutComponent=$shortcut" -pdbtype none -out "$base.msi" PrintBridge.wxs
    if ($LASTEXITCODE -ne 0) { throw 'MSI compilation failed' }
} finally { Pop-Location }
if ($Sign) { & (Join-Path $PSScriptRoot 'sign-binary.ps1') "$base.msi" }
Get-Item "$base.exe", "$base.zip", "$base.msi"
