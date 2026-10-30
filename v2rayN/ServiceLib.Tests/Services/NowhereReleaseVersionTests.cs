namespace ServiceLib.Tests.Services;

public class NowhereReleaseVersionTests
{
    [Test]
    public async Task ForkReleaseTag_ComparesVersionAndPreservesAssetUrlTag()
    {
        var remote = new SemanticVersion("nowhere-v7.25.7");
        await (remote > new SemanticVersion("7.25.6")).Should().BeTrue();
        await remote.ToStandardVersionString().Should().BeEqualTo("7.25.7");
        var template = CoreInfoManager.Instance.GetCoreInfo(ECoreType.v2rayN)!.DownloadUrlOSXArm64;
        await string.Format(template, remote).Should().BeEqualTo(
            "https://github.com/Mecozea/v2rayN/releases/download/nowhere-v7.25.7/v2rayN-macos-arm64.zip");
    }
    [Test]
    public async Task AppUpdateMetadata_UsesForkForDiscoveryAndDownloads()
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(ECoreType.v2rayN)!;
        await info.Url.Should().BeEqualTo("https://github.com/Mecozea/v2rayN/releases");
        await info.ReleaseApiUrl.Should().BeEqualTo("https://api.github.com/repos/Mecozea/v2rayN/releases");
    }

    [Test]
    [Arguments("")]
    [Arguments("https://github.com/Mecozea/v2rayN/releases/latest")]
    [Arguments("not-a-release")]
    public async Task InvalidReleaseResponse_IsNotReportedAsVersionZero(string tag)
    {
        await SemanticVersion.TryParse(tag, out _).Should().BeFalse();
    }
}
