using System.Reflection;
using ServiceLib;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;
using v2rayN.WebAPI.Contracts;
using v2rayN.WebAPI.Services;

namespace v2rayN.WebAPI.Tests;

[NotInParallel]
public class NowhereSubscriptionRuntimeTests
{
    [Test]
    public async Task SubscriptionImportConsumesRotationMarkerAndPublishesReconnectEvent()
    {
        var configField = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousConfig = configField.GetValue(AppManager.Instance);
        var config = new Config { IndexId = string.Empty, SubIndexId = string.Empty };
        configField.SetValue(AppManager.Instance, config);
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        var subid = $"web-nowhere-{Guid.NewGuid():N}";
        var subscription = new SubItem { Id = subid, Remarks = "test", Url = "https://example.com/sub" };
        await SQLiteHelper.Instance.ReplaceAsync(subscription);
        try
        {
            var events = new EventHub();
            var runtime = new V2rayRuntime(events, new LogBuffer(), null!, null!, null!);
            const string link = "nowhere://0123456789abcdef0123456789abcdef@portal.example:443?net=tcp#mynode";
            var imported = await runtime.ImportProfilesAsync(new ProfileImportRequest(link, subid, true));
            await imported.Success.Should().BeTrue();
            config.IndexId = (await AppManager.Instance.ProfileItems(subid)).Single().IndexId;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var subscriber = events.Subscribe(timeout.Token).GetAsyncEnumerator();
            var pendingEvent = subscriber.MoveNextAsync().AsTask();
            var changed = link.Replace("net=tcp", "net=udp");
            var refreshed = await runtime.ImportProfilesAsync(new ProfileImportRequest(changed, subid, true));
            await refreshed.Success.Should().BeTrue();
            await (await pendingEvent).Should().BeTrue();
            await subscriber.Current.Type.Should().BeEqualTo("core-reconnect-required");
            await ConfigHandler.ConsumeSubscriptionReconnectRequired(config).Should().BeFalse();
            await config.IndexId.Should().BeEqualTo(string.Empty);
        }
        finally
        {
            foreach (var row in await AppManager.Instance.ProfileItems(subid))
            {
                await SQLiteHelper.Instance.DeleteAsync(row);
            }
            await SQLiteHelper.Instance.DeleteAsync(subscription);
            ConfigHandler.ConsumeSubscriptionReconnectRequired(config);
            configField.SetValue(AppManager.Instance, previousConfig);
        }
    }
}
