using ServiceLib.Handler;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Manager;
using v2rayN.WebAPI.Contracts;

namespace v2rayN.WebAPI.Services;

public sealed partial class V2rayRuntime
{
    // Must run after releasing _mutations: launch operations acquire _coreGate before _mutations.
    private async Task HandleSubscriptionRuntimeChangeAsync(CancellationToken cancellationToken)
    {
        if (!ConfigHandler.ConsumeSubscriptionReconnectRequired(Config))
        {
            return;
        }
        await _coreGate.WaitAsync(cancellationToken);
        try
        {
            var runningId = CurrentCoreRuntime.ProfileId;
            // A concurrent user selection may already have replaced the removed session.
            if (!string.IsNullOrEmpty(runningId)
                && await AppManager.Instance.GetProfileItem(runningId) is null)
            {
                var stopped = await StopCoreLockedAsync(cancellationToken);
                if (!stopped.Success)
                {
                    throw new IOException("The previous subscription session could not be stopped. Reconnection is required.");
                }
            }
            AddLog("subscription", "The active subscription node changed or was removed. Select a node and reconnect.");
            _events.Publish("core-reconnect-required", new { reason = "subscription_profile_changed" });
        }
        finally
        {
            _coreGate.Release();
        }
    }
}
