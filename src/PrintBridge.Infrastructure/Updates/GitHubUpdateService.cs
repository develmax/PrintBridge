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

    public async Task<string?> DownloadAsync(Version current, bool msi, CancellationToken token)
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

        string folder = Path.Combine(SettingsService.Folder, "Updates");
        Directory.CreateDirectory(folder);
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
