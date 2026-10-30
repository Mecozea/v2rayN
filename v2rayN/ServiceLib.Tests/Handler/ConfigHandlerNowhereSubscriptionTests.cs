using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Handler;

[NotInParallel]
public class ConfigHandlerNowhereSubscriptionTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Link = "nowhere://" + Key + "@portal.example:443?net=udp&morph=0#node";

    [Test]
    public async Task MixedSubscription_InvalidRefreshPreservesSnapshot_AndRenameRetainsIdentity()
    {
        var configField = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousConfig = configField.GetValue(AppManager.Instance);
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        var subid = $"nowhere-sub-{Guid.NewGuid():N}";
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = subid, Remarks = "test", Url = "https://example.com/sub" });
        var socks = "socks://127.0.0.1:1080#ordinary";
        try
        {
            var first = await ConfigHandler.AddBatchServers(config, Link + "\n" + socks, subid, true);
            await first.Should().BeEqualTo(2);
            var original = await AppManager.Instance.ProfileItems(subid);
            var node = original.Single(t => t.ConfigType == EConfigType.Nowhere);
            config.IndexId = node.IndexId;

            var invalid = await ConfigHandler.AddBatchServers(config, Link + "\nnowhere://broken", subid, true);
            await invalid.Should().BeEqualTo(-1);
            var afterFailure = await AppManager.Instance.ProfileItems(subid);
            await afterFailure.Count.Should().BeEqualTo(2);
            await afterFailure.Any(t => t.IndexId == node.IndexId).Should().BeTrue();

            var renamed = Link.Replace("#node", "#renamed");
            var repeated = await ConfigHandler.AddBatchServers(config, Utils.Base64Encode(renamed + "\n" + socks + "\n" + renamed), subid, true);
            await repeated.Should().BeEqualTo(2);
            var refreshed = await AppManager.Instance.ProfileItems(subid);
            var renamedNode = refreshed.Single(t => t.ConfigType == EConfigType.Nowhere);
            await renamedNode.IndexId.Should().BeEqualTo(node.IndexId);
            await renamedNode.Remarks.Should().BeEqualTo("renamed");
            await config.IndexId.Should().BeEqualTo(node.IndexId);

            var pin = new string('a', 64);
            await ConfigHandler.AddBatchServers(config, renamed.Replace("#renamed", "&pin=" + pin + "#renamed") + "\n" + socks, subid, true);
            var rotated = (await AppManager.Instance.ProfileItems(subid)).Single(t => t.ConfigType == EConfigType.Nowhere);
            await (rotated.IndexId != node.IndexId).Should().BeTrue();
            await config.IndexId.Should().BeEqualTo(string.Empty);
            await ConfigHandler.ConsumeSubscriptionReconnectRequired(config).Should().BeTrue();
            await ConfigHandler.ConsumeSubscriptionReconnectRequired(config).Should().BeFalse();
        }
        finally
        {
            var rows = await AppManager.Instance.ProfileItems(subid);
            foreach (var row in rows)
            {
                await SQLiteHelper.Instance.DeleteAsync(row);
            }
            await SQLiteHelper.Instance.DeleteAsync(new SubItem { Id = subid });
            ConfigHandler.ConsumeSubscriptionReconnectRequired(config);
            configField.SetValue(AppManager.Instance, previousConfig);
        }
    }

    [Test]
    public async Task InvalidNowhereProfile_IsRejectedBeforePersistence()
    {
        var profile = new ProfileItem { ConfigType = EConfigType.Nowhere, Address = "0.0.0.0", Port = 443, Password = Key };
        var result = await ConfigHandler.AddNowhereServer(CoreConfigTestFactory.CreateConfig(), profile, false);
        await result.Should().BeEqualTo(-1);
        await profile.IndexId.Should().BeEqualTo(string.Empty);
    }
}
