namespace ServiceLib.Tests.Services;

public class NowhereUpdateDiscoveryTests
{
    [Test]
    public async Task SupportedPlatform_ListsNowhereAlongsideOtherCores()
    {
        await CoreInfoManager.Instance.GetCheckUpdateCoreTypes().Contains(ECoreType.Nowhere)
            .Should().BeEqualTo(NowhereInstaller.IsSupported);
        await CoreInfoManager.Instance.IsCheckUpdateSupported(ECoreType.Nowhere)
            .Should().BeEqualTo(NowhereInstaller.IsSupported);
        await CoreInfoManager.Instance.GetCheckPreRelease(ECoreType.Nowhere, true).Should().BeFalse();
    }

    [Test]
    public async Task CheckOnly_ValidMissingAndCorruptCore_DoesNotInstallOrChangeFiles()
    {
        var executable = Environment.GetEnvironmentVariable("NOWHERE_TEST_EXE");
        if (!NowhereInstaller.IsSupported || string.IsNullOrEmpty(executable)) { return; }
        var current = await NowhereInstaller.CheckUpdateAsync(executable);
        await current.Success.Should().BeFalse();
        await current.Msg!.Contains(NowhereInstaller.Version).Should().BeTrue();
        var path = Path.Combine(Path.GetTempPath(), $"nowhere-check-{Guid.NewGuid():N}");
        try
        {
            var missing = await NowhereInstaller.CheckUpdateAsync(path);
            await missing.Success.Should().BeTrue();
            await File.Exists(path).Should().BeFalse();
            await File.WriteAllTextAsync(path, "corrupt");
            var corrupt = await NowhereInstaller.CheckUpdateAsync(path);
            await corrupt.Success.Should().BeTrue();
            await corrupt.Version!.ToStandardVersionString().Should().BeEqualTo(NowhereInstaller.Version);
            await corrupt.Url.Should().BeEqualTo(NowhereInstaller.PackageUri.AbsoluteUri);
            await (await File.ReadAllTextAsync(path)).Should().BeEqualTo("corrupt");
        }
        finally { File.Delete(path); }
    }
}
