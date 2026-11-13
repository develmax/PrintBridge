namespace PrintBridge.IntegrationTests;

public sealed class UpdateTests
{
    private static readonly Version Current = new(1, 2, 0);

    [Fact]
    public void ReleaseSignatureRejectsModifiedManifestAndAnotherSigningKey()
    {
        using var publisher = System.Security.Cryptography.RSA.Create(2048);
        using var other = System.Security.Cryptography.RSA.Create(2048);
        const string file = "PrintBridge-Setup-1.2.6-win-x64.exe";
        string digest = new('a', 64);
        byte[] manifest = System.Text.Encoding.ASCII.GetBytes($"{digest}  {file}\n");
        byte[] signature = publisher.SignData(manifest, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        Assert.Equal(digest, ReleaseSignature.Verify(publisher, manifest, signature, file));
        Assert.Null(ReleaseSignature.Verify(publisher, manifest, signature, file + ".zip"));
        Assert.Null(ReleaseSignature.Verify(other, manifest, signature, file));
        manifest[0] = (byte)'b';
        Assert.Null(ReleaseSignature.Verify(publisher, manifest, signature, file));
    }

    [Theory]
    [InlineData(60, false, 300)]
    [InlineData(60, true, 60)]
    [InlineData(120, false, 120)]
    public void MigratesOnlyTheLegacyDefaultInterval(int seconds, bool configured, int expected)
    {
        Assert.Equal(300, new AppSettings().IntervalSeconds);
        var settings = SettingsService.NormalizeIdleInterval(new AppSettings(IntervalSeconds: seconds, IdleIntervalConfigured: configured));
        Assert.Equal(expected, settings.IntervalSeconds);
        Assert.True(settings.IdleIntervalConfigured);
    }

    [Fact]
    public void UpdatesDefaultToEnabledAndRespectSavedOptOut()
    {
        Assert.True(new AppSettings().AutoUpdate);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.AutoUpdate);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AutoUpdate\":false}")!.AutoUpdate);
    }

    [Theory]
    [InlineData("win-x64", false, "exe")]
    [InlineData("win-arm64", true, "msi")]
    public void SelectsNewerStablePackageMatchingInstallation(string runtime, bool msi, string extension)
    {
        var expected = Asset($"PrintBridge-Setup-1.2.1-{runtime}.{extension}");
        var release = new GitHubRelease("v1.2.1", false, false, [Asset("unrelated.exe"), expected]);

        Assert.Same(expected, GitHubUpdateService.SelectAsset(release, Current, runtime, msi));
        Assert.Null(GitHubUpdateService.SelectAsset(release, Current, runtime, !msi));
        Assert.Null(GitHubUpdateService.SelectAsset(release, new Version(1, 2, 1), runtime, msi));
        Assert.Null(GitHubUpdateService.SelectAsset(release with { Prerelease = true }, Current, runtime, msi));
        Assert.Null(GitHubUpdateService.SelectAsset(release with { Assets = [expected with { Digest = null }] }, Current, runtime, msi));
        Assert.Null(GitHubUpdateService.SelectAsset(release with { Assets = [expected with { DownloadUrl = "https://example.invalid/file" }] }, Current, runtime, msi));
    }

    private static ReleaseAsset Asset(string name) => new(name, "uploaded", 1024,
        "sha256:" + new string('a', 64), $"https://github.com/develmax/PrintBridge/releases/download/v1.2.1/{name}");
}
