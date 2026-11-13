using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PrintBridge;

internal static class ReleaseSignature
{
    internal const string ManifestName = "SHA256SUMS.txt";
    internal const string SignatureName = "SHA256SUMS.txt.sig";

    internal static string? Verify(byte[] manifest, byte[] signature, string name)
    {
        using var stream = typeof(ReleaseSignature).Assembly.GetManifestResourceStream("PrintBridge.ReleaseCertificate")!;
        using var certificateBytes = new MemoryStream();
        stream.CopyTo(certificateBytes);
        using var certificate = X509CertificateLoader.LoadCertificate(certificateBytes.ToArray());
        using var key = certificate.GetRSAPublicKey()!;
        return Verify(key, manifest, signature, name);
    }

    internal static string? Verify(RSA key, byte[] manifest, byte[] signature, string name)
    {
        if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return null;
        string suffix = "  " + name;
        var matches = Encoding.ASCII.GetString(manifest).Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length == 64 + suffix.Length && line.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 && matches[0].AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            ? matches[0][..64]
            : null;
    }
}
