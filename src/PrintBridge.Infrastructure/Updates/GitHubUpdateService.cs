using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PrintBridge;

internal sealed record ReleaseAsset(string Name, string State, long Size, string? Digest,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl);

internal sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag,
    bool Draft, bool Prerelease, ReleaseAsset[] Assets);

public sealed class GitHubUpdateService
{
    private const string Repository = "develmax/PrintBridge";
    private const long MaxInstallerSize = 128 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    static GitHubUpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PrintBridge-Updater");
    }

    public async Task<Version?> CheckAsync(Version current, bool msi, CancellationToken token)
    {
        var update = await FindAsync(current, msi, token).ConfigureAwait(false);
        return update?.Version;
    }

    private static async Task<(Version Version, ReleaseAsset Asset, byte[] Manifest, byte[] Signature)?> FindAsync(Version current, bool msi, CancellationToken token)
    {
        var release = await Http.GetFromJsonAsync<GitHubRelease>(
            $"https://api.github.com/repos/{Repository}/releases/latest", token).ConfigureAwait(false);
        var runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null
        };
        if (release is null || runtime is null)
            return null;

        var asset = SelectAsset(release, current, runtime, msi);
        if (asset is null)
            return null;
        var manifest = await FetchProofAsync(release.Tag, ReleaseSignature.ManifestName, 16 * 1024, token).ConfigureAwait(false);
        var signature = await FetchProofAsync(release.Tag, ReleaseSignature.SignatureName, 1024, token).ConfigureAwait(false);
        if (!asset.Digest![7..].Equals(ReleaseSignature.Verify(manifest, signature, asset.Name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update is missing a valid PrintBridge release signature.");
        return (Version.Parse(release.Tag.AsSpan(1)), asset, manifest, signature);
    }

    private static async Task<byte[]> FetchProofAsync(string tag, string name, int limit, CancellationToken token)
    {
        using var response = await Http.GetAsync($"https://github.com/{Repository}/releases/download/{tag}/{name}",
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] buffer = new byte[limit + 1];
        int length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token).ConfigureAwait(false);
        if (length > limit)
            throw new InvalidDataException("The release signature metadata is too large.");
        return buffer[..length];
    }

    public async Task<string?> DownloadAsync(Version current, bool msi, CancellationToken token, Version? expected = null)
    {
        var update = await FindAsync(current, msi, token).ConfigureAwait(false);
        if (update is null || (expected is not null && update.Value.Version != expected))
            return null;
        var asset = update.Value.Asset;

        string folder = Path.Combine(SettingsService.Folder, "Updates", update.Value.Version.ToString());
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, ReleaseSignature.ManifestName), update.Value.Manifest, token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(folder, ReleaseSignature.SignatureName), update.Value.Signature, token).ConfigureAwait(false);
        string path = Path.Combine(folder, asset.Name);
        if (File.Exists(path) && await MatchesAsync(path, asset, token).ConfigureAwait(false))
            return path;

        string temporary = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                long total = 0;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > asset.Size)
                        throw new InvalidDataException("The update exceeds its declared size.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            }
            if (!await MatchesAsync(temporary, asset, token).ConfigureAwait(false))
                throw new InvalidDataException("The update does not match its GitHub release digest.");
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<FileStream> OpenVerifiedPackageAsync(string path, Version current, bool msi, CancellationToken token)
    {
        string file = Path.GetFileName(path);
        var runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new InvalidDataException("Unsupported update architecture.")
        };
        string folder = Path.GetDirectoryName(path)!;
        if (!Version.TryParse(Path.GetFileName(folder), out var version) || version <= current || version.Build < 0 ||
            version.Revision >= 0 || file != $"PrintBridge-Setup-{version}-{runtime}.{(msi ? "msi" : "exe")}" ||
            !Path.GetFullPath(folder).Equals(Path.Combine(SettingsService.Folder, "Updates", version.ToString()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid update package path.");

        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(folder, ReleaseSignature.ManifestName), token).ConfigureAwait(false);
        byte[] signature = await File.ReadAllBytesAsync(Path.Combine(folder, ReleaseSignature.SignatureName), token).ConfigureAwait(false);
        string expected = ReleaseSignature.Verify(manifest, signature, file)
            ?? throw new InvalidDataException("The update is missing a valid PrintBridge release signature.");
        var package = File.OpenRead(path);
        try
        {
            if (package.Length is <= 0 or > MaxInstallerSize ||
                !Convert.ToHexString(await SHA256.HashDataAsync(package, token).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update package does not match the signed release checksum.");
            return package;
        }
        catch
        {
            await package.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static ReleaseAsset? SelectAsset(GitHubRelease release, Version current, string runtime, bool msi)
    {
        if (release.Draft || release.Prerelease || !release.Tag.StartsWith('v') ||
            !Version.TryParse(release.Tag.AsSpan(1), out var version) || version <= current ||
            version.Build < 0 || version.Revision >= 0 || release.Tag != $"v{version}")
            return null;

        string name = $"PrintBridge-Setup-{version}-{runtime}.{(msi ? "msi" : "exe")}";
        string url = $"https://github.com/{Repository}/releases/download/{release.Tag}/{name}";
        return release.Assets.FirstOrDefault(asset => asset.Name == name && asset.DownloadUrl == url &&
            asset.State == "uploaded" && asset.Size is > 0 and <= MaxInstallerSize &&
            asset.Digest is { Length: 71 } digest && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0);
    }

    private static async Task<bool> MatchesAsync(string path, ReleaseAsset asset, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return file.Length == asset.Size && Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false))
            .Equals(asset.Digest![7..], StringComparison.OrdinalIgnoreCase);
    }
}
