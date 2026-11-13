using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PrintBridge;

public static class ReleaseSignature
{
    internal const string ManifestName = "SHA256SUMS.txt";
    internal const string SignatureName = "SHA256SUMS.txt.sig";

    internal static string? Verify(byte[] manifest, byte[] signature, string name)
    {
        using var key = OpenPublisherKey();
        return Verify(key, manifest, signature, name);
    }

    public static bool VerifyExecutable(string path)
    {
        using var key = OpenPublisherKey();
        try
        {
            return VerifyExecutable(path, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyExecutable(string path, RSA key)
    {
        using var signatureFile = File.OpenRead(path + ".sig");
        byte[] signature = new byte[key.KeySize / 8];
        if (signatureFile.Length != signature.Length)
            return false;
        signatureFile.ReadExactly(signature);
        using var executable = File.OpenRead(path);
        return key.VerifyHash(SHA256.HashData(executable), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static RSA OpenPublisherKey()
    {
        using var stream = typeof(ReleaseSignature).Assembly.GetManifestResourceStream("PrintBridge.ReleaseCertificate")!;
        using var certificateBytes = new MemoryStream();
        stream.CopyTo(certificateBytes);
        using var certificate = X509CertificateLoader.LoadCertificate(certificateBytes.ToArray());
        return certificate.GetRSAPublicKey()!;
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
