namespace ServiceLib.Tests.Services;

public class BundledSingboxTests
{
    [Test]
    public async Task LegacyUserCore_DoesNotShadowCompatibleBundle_AndRemainsUnchanged()
    {
        var legacy = Environment.GetEnvironmentVariable("LEGACY_SINGBOX_TEST_EXE");
        var bundled = Environment.GetEnvironmentVariable("SINGBOX_TEST_EXE");
        if (string.IsNullOrEmpty(legacy) || string.IsNullOrEmpty(bundled)) { return; }
        var original = await File.ReadAllBytesAsync(legacy);
        await BundledSingbox.Resolve(legacy, bundled).Should().BeEqualTo(bundled);
        await (await File.ReadAllBytesAsync(legacy)).SequenceEqual(original).Should().BeTrue();
        await BundledSingbox.Resolve(bundled, legacy).Should().BeEqualTo(bundled);
        await BundledSingbox.Resolve("missing-user-core", bundled).Should().BeEqualTo(bundled);
        var rejected = false;
        try { BundledSingbox.Resolve(legacy, "missing-bundle-core"); }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains("1.14.0"); }
        await rejected.Should().BeTrue();
    }
}
