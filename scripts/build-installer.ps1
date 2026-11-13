param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$Iscc = $env:PRINTBRIDGE_ISCC
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Iscc) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
        (Join-Path $root 'artifacts/tools/inno/ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
    )
    $Iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if (-not $Iscc) { throw 'ISCC.exe is missing. Install Inno Setup or pass -Iscc.' }

& (Join-Path $PSScriptRoot 'publish.ps1') -Runtime $Runtime
$version = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64compatible' }
$output = if ($Runtime -eq 'win-arm64') { 'artifacts/installer/arm64' } else { 'artifacts/installer' }
& $Iscc "/O$(Join-Path $root $output)" "/DBuildVersion=$version" "/DArch=$arch" "/DPublishDir=$root\artifacts\publish\$Runtime" (Join-Path $root 'installer/PrintBridge.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
Get-Item (Join-Path (Join-Path $root $output) 'PrintBridge-Setup.exe')
