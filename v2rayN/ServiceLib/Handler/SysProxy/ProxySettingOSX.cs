using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Handler.SysProxy;

[SupportedOSPlatform("macos")]
public static class ProxySettingOSX
{
    private static readonly string _proxySetFileName = $"{Global.ProxySetOSXShellFileName.Replace(Global.NamespaceSample, "")}.sh";

    public static async Task SetProxy(string host, int port, string exceptions)
    {
        List<string> args = ["set", host, port.ToString()];
        if (exceptions.IsNotEmpty())
        {
            args.AddRange(exceptions.Split(','));
        }

        await ExecCmd(args);
    }

    public static async Task UnsetProxy()
    {
        List<string> args = ["clear"];
        await ExecCmd(args);
    }

    public static async Task ClearOwnedProxy(string host, int port)
    {
        // A custom script has no ownership-query contract: never blindly clear another app's proxy.
        if (AppManager.Instance.Config.SystemProxyItem?.CustomSystemProxyScriptPath.IsNotEmpty() == true)
        {
            return;
        }
        await ExecCmd(["clear-owned", host, port.ToString()]);
    }

    private static async Task ExecCmd(List<string> args)
    {
        var customSystemProxyScriptPath = AppManager.Instance.Config.SystemProxyItem?.CustomSystemProxyScriptPath;
        var fileName = (customSystemProxyScriptPath.IsNotEmpty() && File.Exists(customSystemProxyScriptPath))
            ? customSystemProxyScriptPath
            : await FileUtils.CreateLinuxShellFile(_proxySetFileName, EmbedUtils.GetEmbedText(Global.ProxySetOSXShellFileName), true);

        // TODO: temporarily notify which script is being used
        NoticeManager.Instance.SendMessage(fileName);

        await Cli.Wrap(fileName).WithArguments(args).ExecuteBufferedAsync();
    }
}
