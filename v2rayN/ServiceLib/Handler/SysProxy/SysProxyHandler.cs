namespace ServiceLib.Handler.SysProxy;

public static class SysProxyHandler
{
    private static readonly string _tag = "SysProxyHandler";
    private static readonly Lazy<PacManager> _pacManager = new(() => new PacManager());

    public static async Task<bool> UpdateSysProxy(Config config, bool forceDisable)
    {
        var type = config.SystemProxyItem.SysProxyType;

        if (forceDisable && type != ESysProxyType.Unchanged)
        {
            type = ESysProxyType.ForcedClear;
        }

        try
        {
            var port = config.Inbound.FirstOrDefault(i => i.Protocol == nameof(EInboundProtocol.socks))?.LocalPort ?? 10808;
            if (port <= 0)
            {
                return false;
            }
            switch (type)
            {
                case ESysProxyType.ForcedChange when Utils.IsWindows():
                    var (strProxy, strExceptions) = GetWindowsProxyString(config, port);
                    ProxySettingWindows.SetProxy(strProxy, strExceptions, 2);
                    break;

                case ESysProxyType.ForcedChange when Utils.IsLinux():
                    var exceptions = SanitizeExceptions(config);
                    await ProxySettingLinux.SetProxy(Global.Loopback, port, exceptions);
                    break;

                case ESysProxyType.ForcedChange when Utils.IsMacOS():
                    var exceptions2 = SanitizeExceptions(config);
                    await ProxySettingOSX.SetProxy(Global.Loopback, port, exceptions2);
                    break;

                case ESysProxyType.ForcedClear when Utils.IsWindows():
                    ProxySettingWindows.UnsetProxy();
                    break;

                case ESysProxyType.ForcedClear when Utils.IsLinux():
                    await ProxySettingLinux.UnsetProxy();
                    break;

                case ESysProxyType.ForcedClear when Utils.IsMacOS():
                    await ProxySettingOSX.UnsetProxy();
                    break;

                case ESysProxyType.Pac when Utils.IsWindows():
                    await SetWindowsProxyPac(port);
                    break;
            }

            if (type != ESysProxyType.Pac && Utils.IsWindows())
            {
                _pacManager.Value.Stop();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
        return true;
    }

    /// <summary>Clear only the endpoint last installed by this runtime session.</summary>
    public static async Task<bool> ClearOwnedSysProxy(Config owner)
    {
        if (Utils.IsMacOS())
        {
            if (owner.SystemProxyItem.SysProxyType != ESysProxyType.ForcedChange) { return true; }
            var port = owner.Inbound.FirstOrDefault(i => i.Protocol == nameof(EInboundProtocol.socks))?.LocalPort ?? 10808;
            try
            {
                await ProxySettingOSX.ClearOwnedProxy(Global.Loopback, port);
                return true;
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
                return false;
            }
        }
        if (Utils.IsWindows())
        {
            const string path = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
            var port = owner.Inbound.FirstOrDefault(i => i.Protocol == nameof(EInboundProtocol.socks))?.LocalPort ?? 10808;
            var currentServer = WindowsUtils.RegReadValue(path, "ProxyServer", "") ?? "";
            var currentPac = WindowsUtils.RegReadValue(path, "AutoConfigURL", "") ?? "";
            var enabled = WindowsUtils.RegReadValue(path, "ProxyEnable", "0");
            var ownsCurrent = owner.SystemProxyItem.SysProxyType switch
            {
                ESysProxyType.ForcedChange => enabled == "1"
                    && currentServer == GetWindowsProxyString(owner, port).strProxy,
                ESysProxyType.Pac => Uri.TryCreate(currentPac, UriKind.Absolute, out var uri)
                    && uri.Host == Global.Loopback && uri.Port == port + (int)EInboundProtocol.pac
                    && uri.AbsolutePath == "/pac",
                _ => false,
            };
            if (!ownsCurrent)
            {
                _pacManager.Value.Stop();
                return true;
            }
        }
        return await UpdateSysProxy(owner, true);
    }

    private static string SanitizeExceptions(Config config)
    {
        var exceptions = config.SystemProxyItem.SystemProxyExceptions;
        if (exceptions.IsNullOrEmpty())
        {
            return string.Empty;
        }

        var items = exceptions
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Replace(" ", string.Empty))
            .Where(item => item.Length > 0);

        return string.Join(',', items);
    }

    private static (string strProxy, string strExceptions) GetWindowsProxyString(Config config, int port)
    {
        var strExceptions = config.SystemProxyItem.SystemProxyExceptions.Replace(" ", "");
        if (config.SystemProxyItem.NotProxyLocalAddress)
        {
            strExceptions = $"<local>;{strExceptions}";
        }

        var strProxy = string.Empty;
        if (config.SystemProxyItem.SystemProxyAdvancedProtocol.IsNullOrEmpty())
        {
            strProxy = $"{Global.Loopback}:{port}";
        }
        else
        {
            strProxy = config.SystemProxyItem.SystemProxyAdvancedProtocol
                .Replace("{ip}", Global.Loopback)
                .Replace("{http_port}", port.ToString())
                .Replace("{socks_port}", port.ToString());
        }

        return (strProxy, strExceptions);
    }

    [SupportedOSPlatform("windows")]
    private static async Task SetWindowsProxyPac(int port)
    {
        var portPac = port + (int)EInboundProtocol.pac;
        await _pacManager.Value.StartAsync(port, portPac);
        var strProxy = $"{Global.HttpProtocol}{Global.Loopback}:{portPac}/pac?t={DateTime.Now.Ticks}";
        ProxySettingWindows.SetProxy(strProxy, "", 4);
    }
}
