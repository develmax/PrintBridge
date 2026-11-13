# Release signature verification

Starting with PrintBridge 1.2.6, the EXE, MSI, and installed application carry an Authenticode signature from the PrintBridge publisher certificate. The detached signature on `SHA256SUMS.txt` covers all release packages, including ZIP files. Windows may show a trust warning for this self-signed certificate. You can verify a package without adding the certificate to any Windows trust store.

The [public certificate](printbridge-signing.cer) has the SHA-256 fingerprint below. Compare it with a copy you already trust before verifying a release.

```text
9175970F8E4A4ECEA7C38E15E9AA69B5374F7328BD3251E665AF69AE8F74A35A
```

Download the package, certificate, `SHA256SUMS.txt`, and `SHA256SUMS.txt.sig` into one directory. From PowerShell, run the following, using the filename of the package you downloaded.

```powershell
$package = 'PrintBridge-Setup-1.2.6-win-x64.exe'
$certificatePath = (Resolve-Path 'printbridge-signing.cer').Path
$fingerprint = '9175970F8E4A4ECEA7C38E15E9AA69B5374F7328BD3251E665AF69AE8F74A35A'
if ((Get-FileHash -LiteralPath $certificatePath).Hash -ne $fingerprint) {
    throw 'Unexpected PrintBridge certificate'
}
$certificate = Get-PfxCertificate -FilePath $certificatePath
$publicKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
try {
    $manifest = [IO.File]::ReadAllBytes((Resolve-Path 'SHA256SUMS.txt').Path)
    $signature = [IO.File]::ReadAllBytes((Resolve-Path 'SHA256SUMS.txt.sig').Path)
    if (-not $publicKey.VerifyData($manifest, $signature,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
        throw 'Invalid release signature'
    }
    $hash = (Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant()
    if ("$hash  $package" -cnotin ([Text.Encoding]::ASCII.GetString($manifest) -split '\r?\n')) {
        throw 'Package does not match the signed manifest'
    }
    'Verified PrintBridge release package'
} finally {
    $publicKey.Dispose()
    $certificate.Dispose()
}
```

For the first upgrade from an older release, verify and install the signed 1.2.6 package to establish the trust anchor. The updater in 1.2.6 and later uses its embedded copy of this certificate, verifies the signed manifest before downloading the installer, and verifies the installer again before executing it.

## Publishing a signed release

GitHub Actions builds and tests unsigned CI artifacts. For public release, build on the maintainer's Windows account with the signing key in `Cert:\CurrentUser\My`, WiX, Inno Setup, and Windows SDK SignTool. Run `scripts/build-release-packages.ps1 -Sign` once per runtime, gather the six packages in a single directory, and run `scripts/sign-release-manifest.ps1 -Directory <directory>`. Publish those six files, the checksum manifest, and its detached signature as release assets. Keep the private key and encrypted backups outside Git and GitHub Actions.
