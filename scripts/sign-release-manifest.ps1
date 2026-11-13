param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$public = Get-PfxCertificate (Join-Path $PSScriptRoot '../docs/printbridge-signing.cer')
$certificate = Get-Item "Cert:/CurrentUser/My/$($public.Thumbprint)"
$key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
try {
    $version = ([xml](Get-Content -Raw (Join-Path $PSScriptRoot '../Directory.Build.props'))).Project.PropertyGroup.Version
    $files = foreach ($runtime in 'win-x64','win-arm64') {
        foreach ($ext in 'exe','msi','zip') {
            Get-Item -LiteralPath (Join-Path $Directory "PrintBridge-Setup-$version-$runtime.$ext")
        }
    }
    foreach ($file in $files | Where-Object Extension -in '.exe','.msi') {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.SignerCertificate.Thumbprint -ne $public.Thumbprint -or $signature.Status -notin 'Valid','NotTrusted','UnknownError') {
            throw "Unsigned or invalid release package: $($file.Name)"
        }
    }
    $lines = $files | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
    }
    $bytes = [Text.Encoding]::ASCII.GetBytes(($lines -join "`n") + "`n")
    $signed = $key.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [IO.File]::WriteAllBytes((Join-Path $Directory 'SHA256SUMS.txt'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $Directory 'SHA256SUMS.txt.sig'), $signed)
} finally {
    if ($key) { $key.Dispose() }
}
