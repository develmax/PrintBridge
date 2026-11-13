namespace PrintBridge.IntegrationTests;

public sealed class UpdateTests
{
    private static readonly Version Current = new(1, 2, 0);

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
