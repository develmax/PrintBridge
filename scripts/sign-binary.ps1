param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$public = Get-PfxCertificate (Join-Path $PSScriptRoot '../docs/printbridge-signing.cer')
$certificate = Get-Item "Cert:/CurrentUser/My/$($public.Thumbprint)"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) {
    throw 'The PrintBridge signing key is unavailable or expired.'
}
$tool = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin" -Recurse -Filter signtool.exe |
    Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName | Select-Object -Last 1
if (-not $tool) { throw 'Install the Windows SDK signing tools.' }
& $tool.FullName sign /sha1 $certificate.Thumbprint /fd SHA256 $Path | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path" }
$signature = Get-AuthenticodeSignature -LiteralPath $Path
if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $signature.Status -notin 'Valid','NotTrusted','UnknownError') {
    throw "Signature verification failed for $Path"
}
